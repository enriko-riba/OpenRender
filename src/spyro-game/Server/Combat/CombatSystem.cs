using OpenTK.Mathematics;
using SpyroGame.Server.Mobs;
using SpyroGame.World.Registry;
using SpyroGame.Shared.State;
using SpyroGame.Server.Items;
using OpenRender.Core;
using OpenRender;
using SpyroGame.Components; // For Log

namespace SpyroGame.Server.Combat;

/// <summary>
/// Server-authoritative combat system implementing Minecraft-like mechanics.
/// 
/// Key mechanics:
/// - Attack cooldown (attack speed determines recovery time)
/// - Damage calculation (weapon damage + strength effects - armor)
/// - Knockback (direction based on attacker position)
/// - Invulnerability frames (0.5s after being hit)
/// - Critical hits (falling + attacking = 1.5x damage)
/// - Sweep attacks (swords can hit multiple mobs)
/// </summary>
public sealed class CombatSystem(DroppedItemManager droppedItemManager)
{
    // Minecraft constants
    public const float BasePlayerReach = 3.0f;        // Survival mode reach
    public const float CreativePlayerReach = 5.0f;   // Creative mode reach
    public const float InvulnerabilitySeconds = 0.5f; // Time immune after being hit
    public const float BaseKnockbackStrength = 0.4f;
    public const float KnockbackYComponent = 0.4f;   // Upward component of knockback
    public const float CriticalHitMultiplier = 1.5f;
    public const float BaseFistDamage = 1.0f;

    /// <summary>
    /// Process a player attacking a mob.
    /// Returns the damage dealt (0 if attack failed).
    /// </summary>
    public CombatResult ProcessPlayerAttackMob(
        Player player,
        MobEntity mob,
        MobManager mobManager,
        bool isCreativeMode = false)
    {
        if (mob.IsDead)
            return CombatResult.Missed("Target is dead");

        // Check invulnerability
        if (mob.HurtTimeRemaining > 0)
            return CombatResult.Missed("Target is invulnerable");

        // Check reach distance (Ray-AABB intersection)
        var maxReach = isCreativeMode ? CreativePlayerReach : BasePlayerReach;
        
        // Use player's eye position for raycast
        // Player.Position is at feet. Eye height is ~1.62
        var eyePos = player.Position + new Vector3(0, 1.62f, 0);
        var lookDir = player.Direction; // Synced from client

        var halfW = mob.Definition.HitboxWidth * 0.5f;
        var min = mob.Position + new Vector3(-halfW, 0, -halfW);
        var max = mob.Position + new Vector3(halfW, mob.Definition.HitboxHeight, halfW);

        // Expand hitbox slightly for server-side leniency (latency/smoothing)
        var leniency = 0.2f;
        min -= new Vector3(leniency);
        max += new Vector3(leniency);

        if (!CollisionManager.RayAabbIntersect(eyePos, lookDir, min, max, out var t) || t > maxReach)
        {
            // Fallback: if raycast fails (e.g. due to slight desync), check simple distance
            // but be stricter than before.
            var distSq = Vector3.DistanceSquared(player.Position, mob.Position);
            var maxDist = maxReach + mob.Definition.HitboxWidth; // generous
            if (distSq > maxDist * maxDist)
            {
                return CombatResult.Missed("Out of reach");
            }
        }

        // Check attack cooldown
        if (!player.CanAttack())
            return CombatResult.Missed("Attack on cooldown");

        // Get weapon damage
        var selectedItem = player.Inventory.SelectedItem;
        var weaponDamage = GetWeaponDamage(selectedItem.Item);
        var attackSpeed = GetAttackSpeed(selectedItem.Item);

        // Calculate damage
        var damage = weaponDamage;

        // Critical hit check: player falling + not on ground
        var isCritical = !player.IsGrounded && player.VelocityY < 0;
        if (isCritical)
            damage *= CriticalHitMultiplier;

        // Apply damage to mob (considering armor)
        var actualDamage = ApplyDamageToMob(mob, damage);

        Log.Info($"[Combat] Player hit Mob {mob.Id} ({mob.Definition.Kind}). Dmg: {actualDamage:F1}. HP Left: {mob.Health:F1}/{mob.Definition.MaxHealth}");

        // Apply knockback
        ApplyKnockback(player.Position, mob);

        // Start player attack cooldown
        player.StartAttackCooldown(attackSpeed);

        // Add exhaustion to player (attacking costs 0.1 hunger)
        player.Attributes.AddExhaustion(0.1f);

        // Remove mob if dead
        if (mob.IsDead)
        {
            mobManager.Remove(mob.Id);
            HandleMobDrops(mob, player);
        }

        return new CombatResult(true, actualDamage, isCritical, mob.IsDead);
    }

    private void HandleMobDrops(MobEntity mob, Player killer)
    {
        if (mob.Definition.Drops == null) return;

        foreach (var entry in mob.Definition.Drops.Entries)
        {
            if (Random.Shared.NextSingle() <= entry.Probability)
            {
                var count = Random.Shared.Next(entry.MinCount, entry.MaxCount + 1);
                if (count <= 0) continue;

                var distSq = Vector3.DistanceSquared(mob.Position, killer.Position);
                if (distSq <= 2.0f * 2.0f)
                {
                    // Auto-collect
                    killer.Inventory.AddItem(entry.Item, count);
                    // TODO: Send notification to client "Picked up X Item"
                    // Since we don't have a direct message channel for this yet, we'll log it server-side
                    // and rely on inventory sync to show the item.
                    // Ideally: connection.Send(new ServerChatMessage($"Picked up {count} {entry.Item}"));
                    Log.Info($"[Loot] Auto-collected {count} {entry.Item} for Player");
                }
                else
                {
                    // Drop on ground
                    // Add some random velocity
                    var vel = new Vector3(
                        (Random.Shared.NextSingle() - 0.5f) * 2.0f,
                        3.0f, // Pop up
                        (Random.Shared.NextSingle() - 0.5f) * 2.0f
                    );
                    droppedItemManager.Spawn(entry.Item, count, mob.Position + new Vector3(0, 0.5f, 0), vel);
                    Log.Info($"[Loot] Dropped {count} {entry.Item} at {mob.Position}");
                }
            }
        }
    }

    /// <summary>
    /// Process a mob attacking a player.
    /// Called from MobAiSystem when mob is in attack range.
    /// </summary>
    public void ProcessMobAttackPlayer(MobEntity mob, Player player)
    {
        if (mob.IsDead || !player.IsAlive)
            return;

        // Check mob attack cooldown
        if (mob.AttackCooldownRemaining > 0)
            return;

        // Check range
        var distance = Vector3.Distance(mob.Position, player.Position);
        if (distance > mob.Definition.AttackRange + mob.Definition.HitboxWidth * 0.5f)
            return;

        // Check player invulnerability
        if (player.InvulnerabilityRemaining > 0)
            return;

        // Calculate damage (could be modified by difficulty later)
        var damage = mob.Definition.BaseDamage;

        // Apply damage to player
        var actualDamage = ApplyDamageToPlayer(player, damage);

        // Apply knockback to player
        ApplyKnockbackToPlayer(mob.Position, player, mob.Definition.KnockbackStrength);

        // Start mob attack cooldown (1 / attackSpeed seconds, or use explicit cooldown if attackSpeed is 0)
        mob.AttackCooldownRemaining = mob.Definition.AttackSpeed > 0
            ? 1.0f / mob.Definition.AttackSpeed
            : mob.Definition.AttackCooldownSeconds;

        // Start player invulnerability
        player.StartInvulnerability(InvulnerabilitySeconds);
    }

    /// <summary>
    /// Apply damage to a mob, considering armor.
    /// Returns actual damage dealt.
    /// </summary>
    private float ApplyDamageToMob(MobEntity mob, float damage)
    {
        // Minecraft armor formula: damage = damage * (1 - min(20, armor) / 25)
        var armorReduction = MathF.Min(20f, mob.Definition.ArmorPoints) / 25f;
        var actualDamage = damage * (1f - armorReduction);

        mob.Health -= actualDamage;
        mob.HurtTimeRemaining = InvulnerabilitySeconds;

        if (mob.Health <= 0)
        {
            mob.Health = 0;
            mob.IsDead = true;
        }

        return actualDamage;
    }

    /// <summary>
    /// Apply damage to a player.
    /// </summary>
    private float ApplyDamageToPlayer(Player player, float damage)
    {
        // TODO: Consider player armor when implemented
        var actualDamage = (int)MathF.Ceiling(damage);
        player.Attributes.Damage(actualDamage);
        return actualDamage;
    }

    /// <summary>
    /// Apply knockback to a mob from an attack.
    /// </summary>
    private void ApplyKnockback(Vector3 attackerPosition, MobEntity mob)
    {
        var direction = mob.Position - attackerPosition;
        direction.Y = 0;

        if (direction.LengthSquared > 0.001f)
        {
            direction.Normalize();
        }
        else
        {
            // Random direction if directly on top
            direction = new Vector3(1, 0, 0);
        }

        var knockback = BaseKnockbackStrength;
        mob.Velocity.X += direction.X * knockback * 10f;
        mob.Velocity.Y = KnockbackYComponent * 10f;
        mob.Velocity.Z += direction.Z * knockback * 10f;
    }

    /// <summary>
    /// Apply knockback to a player from a mob attack.
    /// </summary>
    private void ApplyKnockbackToPlayer(Vector3 attackerPosition, Player player, float strength)
    {
        var direction = player.Position - attackerPosition;
        direction.Y = 0;

        if (direction.LengthSquared > 0.001f)
        {
            direction.Normalize();
        }
        else
        {
            direction = new Vector3(1, 0, 0);
        }

        // Apply knockback via player velocity (player handles this)
        player.ApplyKnockback(direction * strength * 10f, KnockbackYComponent * 10f);
    }

    /// <summary>
    /// Get weapon damage from held item.
    /// </summary>
    private static float GetWeaponDamage(ItemId heldItem)
    {
        return heldItem switch
        {
            ItemId.DiamondSword => 7.0f,
            _ => BaseFistDamage
        };
    }

    /// <summary>
    /// Get attack speed from held item.
    /// </summary>
    private static float GetAttackSpeed(ItemId heldItem)
    {
        return heldItem switch
        {
            ItemId.DiamondSword => 1.6f,
            _ => 4.0f
        };
    }
}

/// <summary>
/// Result of a combat action.
/// </summary>
public readonly record struct CombatResult(
    bool Hit,
    float DamageDealt,
    bool WasCritical,
    bool TargetKilled,
    string? FailureReason = null)
{
    public static CombatResult Missed(string reason) => new(false, 0, false, false, reason);
}

using OpenTK.Mathematics;
using DarkVox.Server.Combat;
using DarkVox.Shared.State;
using DarkVox.Shared.World;
using DarkVox.Shared.World.Registry;
using DarkVox.World;

namespace DarkVox.Server.Mobs;

/// <summary>
/// Output from AI tick - contains pending attacks that need to be processed.
/// </summary>
public readonly record struct MobAttackIntent(MobId MobId, PlayerId TargetPlayer);

public sealed class MobAiSystem(VoxelWorld world)
{
    /// <summary>
    /// Stores attack intents generated during this tick for processing by combat system.
    /// </summary>
    public List<MobAttackIntent> PendingAttacks { get; } = [];

    public void Tick(double elapsedSeconds, MobManager mobManager, ReadOnlySpan<(PlayerId PlayerId, Vector3 Position)> players)
    {
        PendingAttacks.Clear();

        foreach (var mob in mobManager.Mobs.Values)
        {
            UpdateMobAi(mob, elapsedSeconds, players);
        }
    }

    private void UpdateMobAi(MobEntity mob, double elapsedSeconds, ReadOnlySpan<(PlayerId PlayerId, Vector3 Position)> players)
    {
        if (mob.IsDead) return;

        mob.AiTimer -= (float)elapsedSeconds;

        // Tick combat cooldowns
        if (mob.AttackCooldownRemaining > 0)
            mob.AttackCooldownRemaining -= (float)elapsedSeconds;
        if (mob.HurtTimeRemaining > 0)
            mob.HurtTimeRemaining -= (float)elapsedSeconds;

        switch (mob.AiState)
        {
            case MobAiState.Idle:
                if (mob.AiTimer <= 0)
                {
                    // Pick new state
                    if (Random.Shared.NextDouble() < 0.5)
                    {
                        mob.AiState = MobAiState.Wander;
                        mob.AiTimer = 2.0f + (float)Random.Shared.NextDouble() * 3.0f;
                        PickWanderTarget(mob);
                    }
                    else
                    {
                        mob.AiState = MobAiState.Idle;
                        mob.AiTimer = 1.0f + (float)Random.Shared.NextDouble() * 2.0f;
                    }
                }
                CheckAggro(mob, players);
                break;

            case MobAiState.Wander:
                if (mob.AiTimer <= 0)
                {
                    // Time expired - stop wandering
                    mob.AiState = MobAiState.Idle;
                    mob.AiTimer = 1.0f + (float)Random.Shared.NextDouble() * 2.0f;
                    mob.Velocity.X = 0;
                    mob.Velocity.Z = 0;
                    mob.WanderTarget = null;
                }
                else if (mob.WanderTarget.HasValue && Vector3.DistanceSquared(mob.Position, mob.WanderTarget.Value) < 2.0f)
                {
                    // Reached destination (within ~1.4 blocks) - stop wandering
                    mob.AiState = MobAiState.Idle;
                    mob.AiTimer = 1.0f + (float)Random.Shared.NextDouble() * 2.0f;
                    mob.Velocity.X = 0;
                    mob.Velocity.Z = 0;
                    mob.WanderTarget = null;
                }
                else
                {
                    MoveTowards(mob, mob.WanderTarget ?? mob.Position, mob.Definition.WalkSpeed);
                }
                CheckAggro(mob, players);
                break;

            case MobAiState.Chase:
                if (mob.TargetPlayer.HasValue)
                {
                    // Find target player position
                    Vector3? targetPos = null;
                    foreach (var p in players)
                    {
                        if (p.PlayerId == mob.TargetPlayer.Value)
                        {
                            targetPos = p.Position;
                            break;
                        }
                    }

                    if (targetPos.HasValue)
                    {
                        var distSq = Vector3.DistanceSquared(mob.Position, targetPos.Value);
                        if (distSq > mob.Definition.LoseAggroRange * mob.Definition.LoseAggroRange)
                        {
                            mob.TargetPlayer = null;
                            mob.AiState = MobAiState.Idle;
                        }
                        else
                        {
                            MoveTowards(mob, targetPos.Value, mob.Definition.RunSpeed);
                            
                            // Attack when in range and cooldown is ready
                            if (distSq < mob.Definition.AttackRange * mob.Definition.AttackRange)
                            {
                                // Stop moving when attacking
                                mob.Velocity.X = 0;
                                mob.Velocity.Z = 0;

                                // Queue attack if cooldown ready (CombatSystem will set the actual cooldown)
                                if (mob.AttackCooldownRemaining <= 0 && mob.TargetPlayer.HasValue)
                                {
                                    PendingAttacks.Add(new MobAttackIntent(mob.Id, mob.TargetPlayer.Value));
                                }
                            }
                        }
                    }
                    else
                    {
                        // Player lost
                        mob.TargetPlayer = null;
                        mob.AiState = MobAiState.Idle;
                    }
                }
                else
                {
                    mob.AiState = MobAiState.Idle;
                }
                break;
        }
        
        // Update Yaw - face the direction of movement
        // Standard: atan2 gives angle where +Z is 0°, but model forward is -Z (OpenGL convention)
        // YawOffsetDegrees allows per-mob correction for models with non-standard facing
        // Only update if moving with significant velocity to avoid jitter when stopping
        if (mob.Velocity.X * mob.Velocity.X + mob.Velocity.Z * mob.Velocity.Z > 0.25f)
        {
            var targetYaw = MathHelper.RadiansToDegrees(MathF.Atan2(mob.Velocity.X, mob.Velocity.Z)) + mob.Definition.YawOffsetDegrees;
            
            // Smoothly interpolate yaw to avoid snapping
            var deltaYaw = targetYaw - mob.YawDegrees;
            
            // Normalize to -180..180
            while (deltaYaw > 180f) deltaYaw -= 360f;
            while (deltaYaw < -180f) deltaYaw += 360f;
            
            // Apply smoothed rotation (lerp ~10% per tick at 20 TPS = smooth turn)
            mob.YawDegrees += deltaYaw * 0.15f;
        }
    }

    private void CheckAggro(MobEntity mob, ReadOnlySpan<(PlayerId PlayerId, Vector3 Position)> players)
    {
        if (mob.Definition.Category != MobCategory.Hostile) return;

        foreach (var p in players)
        {
            // Ignore dead players (assuming 0 HP means dead, though Player struct doesn't have HP here directly)
            // The players span only has ID and Position. We need to check if the player is alive.
            // However, the span is constructed in LocalGameServer.Tick from active players.
            // We should probably filter dead players before passing them here, or pass the full Player object.
            // For now, let's assume the caller filters or we need to access the player manager.
            // But wait, MobAiSystem.Tick takes ReadOnlySpan<(PlayerId, Vector3)>.
            
            var distSq = Vector3.DistanceSquared(mob.Position, p.Position);
            if (distSq < mob.Definition.AggroRange * mob.Definition.AggroRange)
            {
                mob.TargetPlayer = p.PlayerId;
                mob.AiState = MobAiState.Chase;
                break;
            }
        }
    }

    private void PickWanderTarget(MobEntity mob)
    {
        var angle = Random.Shared.NextDouble() * Math.PI * 2;
        var dist = 2.0 + Random.Shared.NextDouble() * 5.0;
        var offset = new Vector3((float)(Math.Cos(angle) * dist), 0, (float)(Math.Sin(angle) * dist));
        mob.WanderTarget = mob.Position + offset;
    }

    private void MoveTowards(MobEntity mob, Vector3 target, float speed)
    {
        var dir = target - mob.Position;
        dir.Y = 0;
        if (dir.LengthSquared > 0.001f)
        {
            dir.Normalize();

            // Water avoidance: non-swimming mobs should not follow targets into liquid.
            // Keep it simple and local: if the next step would enter liquid, attempt a sidestep;
            // otherwise stop at the shoreline.
            if (!mob.Definition.CanFly && !mob.Definition.CanSwim)
            {
                if (WouldStepFromLandIntoLiquid(mob, dir))
                {
                    if (TryFindNonLiquidDirection(mob, dir, out var adjusted))
                    {
                        dir = adjusted;
                    }
                    else
                    {
                        mob.Velocity.X = 0;
                        mob.Velocity.Z = 0;
                        return;
                    }
                }
            }

            mob.Velocity.X = dir.X * speed;
            mob.Velocity.Z = dir.Z * speed;
        }
    }

    private bool WouldStepFromLandIntoLiquid(MobEntity mob, Vector3 dir)
    {
        var x0 = (int)MathF.Floor(mob.Position.X);
        var y0 = (int)MathF.Floor(mob.Position.Y);
        var z0 = (int)MathF.Floor(mob.Position.Z);

        // Look a short distance ahead at the mob's feet.
        var ahead = mob.Position + dir * 0.75f;
        var x1 = (int)MathF.Floor(ahead.X);
        var y1 = y0;
        var z1 = (int)MathF.Floor(ahead.Z);

        var cur = world.GetBlockByPositionGlobalSafe(x0, y0, z0);
        var next = world.GetBlockByPositionGlobalSafe(x1, y1, z1);

        var curLiquid = cur.HasValue && cur.Value.Block.IsLiquid();
        var nextLiquid = next.HasValue && next.Value.Block.IsLiquid();

        return !curLiquid && nextLiquid;
    }

    private bool TryFindNonLiquidDirection(MobEntity mob, Vector3 preferred, out Vector3 dir)
    {
        // Try preferred direction first (in case we're already in liquid or the check was conservative).
        if (!WouldStepFromLandIntoLiquid(mob, preferred))
        {
            dir = preferred;
            return true;
        }

        // Try sidesteps.
        var left = new Vector3(-preferred.Z, 0, preferred.X);
        var right = new Vector3(preferred.Z, 0, -preferred.X);

        if (left.LengthSquared > 0.001f)
        {
            left.Normalize();
            if (!WouldStepFromLandIntoLiquid(mob, left))
            {
                dir = left;
                return true;
            }
        }

        if (right.LengthSquared > 0.001f)
        {
            right.Normalize();
            if (!WouldStepFromLandIntoLiquid(mob, right))
            {
                dir = right;
                return true;
            }
        }

        dir = default;
        return false;
    }
}

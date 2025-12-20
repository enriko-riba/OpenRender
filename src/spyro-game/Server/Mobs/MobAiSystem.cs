using OpenTK.Mathematics;
using SpyroGame.Server.Combat;
using SpyroGame.Shared.State;
using SpyroGame.World;

namespace SpyroGame.Server.Mobs;

/// <summary>
/// Output from AI tick - contains pending attacks that need to be processed.
/// </summary>
public readonly record struct MobAttackIntent(MobId MobId, PlayerId TargetPlayer);

public sealed class MobAiSystem()
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
            mob.Velocity.X = dir.X * speed;
            mob.Velocity.Z = dir.Z * speed;
        }
    }
}

using OpenTK.Mathematics;
using SpyroGame.Shared.State;
using SpyroGame.World;

namespace SpyroGame.Server.Mobs;

public sealed class MobAiSystem()
{
    public void Tick(double elapsedSeconds, MobManager mobManager, ReadOnlySpan<(PlayerId PlayerId, Vector3 Position)> players)
    {
        foreach (var mob in mobManager.Mobs.Values)
        {
            UpdateMobAi(mob, elapsedSeconds, players);
        }
    }

    private void UpdateMobAi(MobEntity mob, double elapsedSeconds, ReadOnlySpan<(PlayerId PlayerId, Vector3 Position)> players)
    {
        if (mob.IsDead) return;

        mob.AiTimer -= (float)elapsedSeconds;

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
                if (mob.AiTimer <= 0 || (mob.WanderTarget.HasValue && Vector3.DistanceSquared(mob.Position, mob.WanderTarget.Value) < 1.0f))
                {
                    mob.AiState = MobAiState.Idle;
                    mob.AiTimer = 1.0f + (float)Random.Shared.NextDouble() * 2.0f;
                    mob.Velocity.X = 0;
                    mob.Velocity.Z = 0;
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
                            
                            // Attack logic (simple)
                            if (distSq < mob.Definition.AttackRange * mob.Definition.AttackRange)
                            {
                                // Attack! (Just stop moving for now)
                                mob.Velocity.X = 0;
                                mob.Velocity.Z = 0;
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
        
        // Update Yaw
        if (mob.Velocity.LengthSquared > 0.1f)
        {
            mob.YawDegrees = MathHelper.RadiansToDegrees(MathF.Atan2(mob.Velocity.X, mob.Velocity.Z));
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

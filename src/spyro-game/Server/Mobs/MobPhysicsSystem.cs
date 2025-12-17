using OpenTK.Mathematics;
using SpyroGame.World;

namespace SpyroGame.Server.Mobs;

/// <summary>
/// Handles movement and collision for mobs.
/// Shared kinematic logic with Player (simplified).
/// </summary>
public sealed class MobPhysicsSystem(VoxelWorld world)
{
    private const float Gravity = -20.0f;
    private const float MaxPhysicsStepSeconds = 1f / 90f;
    private const float TerminalVelocity = -50.0f;

    public void Tick(double elapsedSeconds, MobManager mobManager)
    {
        foreach (var mob in mobManager.Mobs.Values)
        {
            SimulateMob(mob, elapsedSeconds);
        }
    }

    private void SimulateMob(MobEntity mob, double elapsedSeconds)
    {
        var remaining = (float)elapsedSeconds;
        var steps = 0;

        while (remaining > 0f)
        {
            var dt = MathF.Min(remaining, MaxPhysicsStepSeconds);
            ApplyMovementStep(mob, dt);
            remaining -= dt;
            steps++;

            if (steps > 16) break; // Safety valve
        }
    }

    private void ApplyMovementStep(MobEntity mob, float dt)
    {
        // Apply gravity
        if (!mob.Definition.CanFly)
        {
            mob.Velocity.Y += Gravity * dt;
            if (mob.Velocity.Y < TerminalVelocity) mob.Velocity.Y = TerminalVelocity;
        }

        // Apply friction/damping (simplified)
        var friction = mob.OnGround ? 10.0f : 2.0f;
        var damping = MathF.Exp(-friction * dt);
        mob.Velocity.X *= damping;
        mob.Velocity.Z *= damping;

        // Move
        var delta = mob.Velocity * dt;
        Move(mob, delta);

        // Check ground status
        CheckGround(mob);
    }

    private void Move(MobEntity mob, Vector3 delta)
    {
        // Separate XZ and Y movement for stability
        var deltaXZ = new Vector3(delta.X, 0, delta.Z);
        var deltaY = new Vector3(0, delta.Y, 0);

        // Move XZ
        mob.Position += deltaXZ;
        ResolveCollisionXZ(mob);

        // Move Y
        mob.Position += deltaY;
        ResolveCollisionY(mob);

        // World floor limit
        if (mob.Position.Y < 1.0f)
        {
            mob.Position.Y = 1.0f;
            mob.Velocity.Y = 0;
            mob.OnGround = true;
        }
    }

    private void ResolveCollisionXZ(MobEntity mob)
    {
        var width = mob.Definition.HitboxWidth;
        var halfWidth = width * 0.5f;
        var height = mob.Definition.HitboxHeight;

        var minX = (int)MathF.Floor(mob.Position.X - halfWidth);
        var maxX = (int)MathF.Floor(mob.Position.X + halfWidth);
        var minY = (int)MathF.Floor(mob.Position.Y);
        var maxY = (int)MathF.Floor(mob.Position.Y + height);
        var minZ = (int)MathF.Floor(mob.Position.Z - halfWidth);
        var maxZ = (int)MathF.Floor(mob.Position.Z + halfWidth);

        for (var y = minY; y <= maxY; y++)
        {
            for (var z = minZ; z <= maxZ; z++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var block = world.GetBlockByPositionGlobalSafe(x, y, z);
                    if (block.HasValue && block.Value.IsSolid)
                    {
                        ResolveBoxCollision(mob, x, y, z, halfWidth);
                    }
                }
            }
        }
    }

    private void ResolveBoxCollision(MobEntity mob, int x, int y, int z, float halfWidth)
    {
        // AABB of the block
        var min = new Vector3(x, y, z);
        var max = new Vector3(x + 1, y + 1, z + 1);

        // Closest point on AABB to mob center (XZ only)
        var closestX = Math.Clamp(mob.Position.X, min.X, max.X);
        var closestZ = Math.Clamp(mob.Position.Z, min.Z, max.Z);

        var dx = mob.Position.X - closestX;
        var dz = mob.Position.Z - closestZ;
        var distSq = dx * dx + dz * dz;

        if (distSq < halfWidth * halfWidth)
        {
            var dist = MathF.Sqrt(distSq);
            var penetration = halfWidth - dist;

            Vector3 normal;
            if (dist < 1e-4f)
            {
                normal = Vector3.Normalize(new Vector3(mob.Position.X - (min.X + max.X) * 0.5f, 0, mob.Position.Z - (min.Z + max.Z) * 0.5f));
                if (normal.LengthSquared < 0.1f) normal = Vector3.UnitX;
            }
            else
            {
                normal = new Vector3(dx / dist, 0, dz / dist);
            }

            mob.Position += normal * penetration;

            // Kill velocity into the wall
            var dot = Vector3.Dot(mob.Velocity, normal);
            if (dot < 0)
            {
                mob.Velocity -= normal * dot;
            }
        }
    }

    private void ResolveCollisionY(MobEntity mob)
    {
        var width = mob.Definition.HitboxWidth;
        var halfWidth = width * 0.5f;
        var height = mob.Definition.HitboxHeight;

        var minX = (int)MathF.Floor(mob.Position.X - halfWidth);
        var maxX = (int)MathF.Floor(mob.Position.X + halfWidth);
        var minY = (int)MathF.Floor(mob.Position.Y);
        var maxY = (int)MathF.Floor(mob.Position.Y + height);
        var minZ = (int)MathF.Floor(mob.Position.Z - halfWidth);
        var maxZ = (int)MathF.Floor(mob.Position.Z + halfWidth);

        for (var y = minY; y <= maxY; y++)
        {
            for (var z = minZ; z <= maxZ; z++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var block = world.GetBlockByPositionGlobalSafe(x, y, z);
                    if (block.HasValue && block.Value.IsSolid)
                    {
                        // Simple Y resolution
                        if (mob.Velocity.Y < 0 && mob.Position.Y < y + 1)
                        {
                            // Landing
                            mob.Position.Y = y + 1;
                            mob.Velocity.Y = 0;
                            mob.OnGround = true;
                        }
                        else if (mob.Velocity.Y > 0 && mob.Position.Y + height > y)
                        {
                            // Hitting head
                            mob.Position.Y = y - height;
                            mob.Velocity.Y = 0;
                        }
                    }
                }
            }
        }
    }

    private void CheckGround(MobEntity mob)
    {
        // Simple check: is there a solid block immediately below?
        var y = (int)MathF.Floor(mob.Position.Y - 0.1f);
        var x = (int)MathF.Floor(mob.Position.X);
        var z = (int)MathF.Floor(mob.Position.Z);

        var block = world.GetBlockByPositionGlobalSafe(x, y, z);
        if (block.HasValue && block.Value.IsSolid)
        {
            mob.OnGround = true;
        }
        else
        {
            mob.OnGround = false;
        }
    }
}

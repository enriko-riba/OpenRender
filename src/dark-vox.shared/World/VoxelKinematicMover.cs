using OpenTK.Mathematics;

namespace DarkVox.Shared.World;

public readonly record struct KinematicCollider(float Radius, float Height, float StepHeight);

public static class VoxelKinematicMover
{
    private const float MaxAutoJumpStepHeight = 1.05f; // full block (1.0) + small tolerance

    public static void Move(
        VoxelWorld world,
        ref Vector3 position,
        ref Vector3 velocity,
        ref bool isGrounded,
        in KinematicCollider collider,
        Vector3 delta,
        float worldFloorY = 1.0f,
        bool enableAutoJump = false,
        float gravityMagnitude = 20.0f,
        float autoJumpHorizontalDamping = 1.0f,
        float autoJumpMinDot = 0.7f,
        float autoJumpExtraClearance = 0.2f)
    {
        // Separate XZ and Y movement for stability
        var deltaXZ = new Vector3(delta.X, 0, delta.Z);
        var deltaY = new Vector3(0, delta.Y, 0);

        position += deltaXZ;
        ResolveCollisionXZ(
            world,
            ref position,
            ref velocity,
            ref isGrounded,
            collider,
            enableAutoJump,
            gravityMagnitude,
            autoJumpHorizontalDamping,
            autoJumpMinDot,
            autoJumpExtraClearance);

        position += deltaY;
        ResolveCollisionY(world, ref position, ref velocity, collider);

        // Prevent falling below the world floor.
        if (position.Y < worldFloorY)
        {
            position.Y = worldFloorY;
            velocity.Y = 0;
            isGrounded = true;
        }

        CheckGround(world, ref position, ref velocity, ref isGrounded, collider);
    }

    private static void ResolveCollisionXZ(
        VoxelWorld world,
        ref Vector3 position,
        ref Vector3 velocity,
        ref bool isGrounded,
        in KinematicCollider collider,
        bool enableAutoJump,
        float gravityMagnitude,
        float autoJumpHorizontalDamping,
        float autoJumpMinDot,
        float autoJumpExtraClearance)
    {
        var currentBlock = world.GetBlockByPositionGlobalSafe((int)position.X, (int)position.Y, (int)position.Z);
        if (currentBlock == null) return;

        // Check a few blocks high so tall entities don't clip.
        var neighbors = world.GetCollideCandidateBlocks(currentBlock.Value, 3);
        foreach (var neighbor in neighbors)
        {
            if (neighbor == null || !neighbor.Value.IsSolid) continue;

            var (min, max) = neighbor.Value.Aabb;

            // Vertical overlap
            if (position.Y >= max.Y || position.Y + collider.Height <= min.Y) continue;

            // Closest point on AABB to cylinder axis
            var closestX = Math.Clamp(position.X, min.X, max.X);
            var closestZ = Math.Clamp(position.Z, min.Z, max.Z);

            var dx = position.X - closestX;
            var dz = position.Z - closestZ;
            var distSq = dx * dx + dz * dz;

            if (distSq >= collider.Radius * collider.Radius)
            {
                continue;
            }

            var dist = MathF.Sqrt(MathF.Max(distSq, 0));
            var penetration = collider.Radius - dist;

            Vector3 normal;
            if (dist < 1e-4f)
            {
                normal = Vector3.Normalize(new Vector3(position.X - (min.X + max.X) * 0.5f, 0, position.Z - (min.Z + max.Z) * 0.5f));
                if (normal.LengthSquared < 0.1f) normal = Vector3.UnitX;
            }
            else
            {
                normal = new Vector3(dx / dist, 0, dz / dist);
            }

            // For auto-jump we want the obstacle face normal, not a corner radial normal.
            // Radial normals near corners can make shallow-angle slides look "head-on" and spam small jumps.
            var autoJumpWallNormal = normal;
            if (MathF.Abs(dx) > MathF.Abs(dz))
            {
                autoJumpWallNormal = new Vector3(MathF.Sign(dx) == 0 ? 1.0f : MathF.Sign(dx), 0, 0);
            }
            else if (MathF.Abs(dz) > 1e-6f)
            {
                autoJumpWallNormal = new Vector3(0, 0, MathF.Sign(dz));
            }

            // Step-up when grounded.
            if (isGrounded)
            {
                var obstacleTop = max.Y;
                var stepHeight = obstacleTop - position.Y;

                // Prefer step-up for small ledges.
                if (stepHeight > 0 && stepHeight <= collider.StepHeight)
                {
                    var stepped = position;
                    stepped.Y = obstacleTop + 0.001f;
                    if (!IsBoxBlocked(world, stepped, collider))
                    {
                        position = stepped;
                        velocity.Y = MathF.Max(0, velocity.Y);
                        isGrounded = true;
                        continue;
                    }
                }

                // Auto-jump is for full-block elevation changes that are NOT stepable.
                if (enableAutoJump && stepHeight > collider.StepHeight + 1e-4f && stepHeight <= MaxAutoJumpStepHeight)
                {
                    if (TryAutoJump(
                            world,
                            ref position,
                            ref velocity,
                            ref isGrounded,
                            collider,
                            neighbor.Value,
                            autoJumpWallNormal,
                            gravityMagnitude,
                            autoJumpHorizontalDamping,
                            autoJumpMinDot,
                            autoJumpExtraClearance))
                    {
                        // Nudge out of the obstacle so we don't stay embedded.
                        position += normal * (penetration + 0.001f);
                        continue;
                    }
                }
            }
            else
            {
                // When rising, allow slipping past 1-block ledges without snagging.
                var obstacleTop = max.Y;
                var stepHeight = obstacleTop - position.Y;
                if (stepHeight <= collider.StepHeight && velocity.Y > 0)
                {
                    continue;
                }

                // Airborne wall response: slide horizontally.
                if (Vector3.Dot(velocity, normal) < 0)
                {
                    var dot = Vector3.Dot(velocity, normal);
                    velocity -= normal * dot;
                }
            }

            // Push out and slide.
            position += normal * (penetration + 0.001f);

            var vDot = Vector3.Dot(velocity, normal);
            if (vDot < 0)
            {
                velocity -= normal * vDot;
            }
        }
    }

    private static bool TryAutoJump(
        VoxelWorld world,
        ref Vector3 position,
        ref Vector3 velocity,
        ref bool isGrounded,
        in KinematicCollider collider,
        in BlockState obstacle,
        in Vector3 wallNormal,
        float gravityMagnitude,
        float autoJumpHorizontalDamping,
        float autoJumpMinDot,
        float autoJumpExtraClearance)
    {
        if (!isGrounded) return false;

        // If the obstacle continues upward, don't try to auto-jump it.
        var above = world.GetBlockByPositionGlobalSafe(obstacle.GlobalPosition.X, obstacle.GlobalPosition.Y + 1, obstacle.GlobalPosition.Z);
        if (above is not null && above.Value.IsSolid)
        {
            return false;
        }

        var horizontalVel = new Vector3(velocity.X, 0, velocity.Z);
        if (horizontalVel.LengthSquared < 1e-6f)
        {
            return false;
        }

        var moveDir = horizontalVel.Normalized();
        var dot = Vector3.Dot(moveDir, -wallNormal);
        if (dot <= autoJumpMinDot)
        {
            return false;
        }

        // Pick a landing point slightly onto the top face (into the obstacle), then validate:
        // - enough headroom for the full collider
        // - some solid support under the collider footprint
        var approachDir = (-wallNormal).LengthSquared > 1e-6f ? (-wallNormal).Normalized() : moveDir;
        var obstacleTop = obstacle.Aabb.Max.Y;

        var landPos = position + approachDir * (collider.Radius + 0.05f);
        landPos.Y = obstacleTop + 0.01f;

        if (IsBoxBlocked(world, landPos, collider))
        {
            return false;
        }

        if (!HasSupportBelow(world, landPos, collider))
        {
            return false;
        }

        var stepHeight = obstacleTop - position.Y;
        var jumpHeight = stepHeight + autoJumpExtraClearance;
        var jumpVel = MathF.Sqrt(2f * MathF.Max(0.01f, gravityMagnitude) * jumpHeight);

        velocity.Y = jumpVel;
        velocity.X *= autoJumpHorizontalDamping;
        velocity.Z *= autoJumpHorizontalDamping;
        isGrounded = false;
        return true;
    }

    private static bool HasSupportBelow(VoxelWorld world, Vector3 position, in KinematicCollider collider)
    {
        // Similar to CheckGround, but only answers "is there any solid support right below".
        var minX = (int)MathF.Floor(position.X - collider.Radius + 0.1f);
        var maxX = (int)MathF.Floor(position.X + collider.Radius - 0.1f);
        var minZ = (int)MathF.Floor(position.Z - collider.Radius + 0.1f);
        var maxZ = (int)MathF.Floor(position.Z + collider.Radius - 0.1f);
        var y = (int)MathF.Floor(position.Y - 0.1f);

        for (var x = minX; x <= maxX; x++)
        {
            for (var z = minZ; z <= maxZ; z++)
            {
                var block = world.GetBlockByPositionGlobalSafe(x, y, z);
                if (block != null && block.Value.IsSolid)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void ResolveCollisionY(VoxelWorld world, ref Vector3 position, ref Vector3 velocity, in KinematicCollider collider)
    {
        // Ceiling check
        if (velocity.Y <= 0) return;

        if (IsHeadHittingCeiling(world, position, collider))
        {
            velocity.Y = 0;
            var ceilingY = MathF.Floor(position.Y + collider.Height);
            position.Y = ceilingY - collider.Height - 0.001f;
        }
    }

    private static void CheckGround(
        VoxelWorld world,
        ref Vector3 position,
        ref Vector3 velocity,
        ref bool isGrounded,
        in KinematicCollider collider)
    {
        var minX = (int)MathF.Floor(position.X - collider.Radius + 0.1f);
        var maxX = (int)MathF.Floor(position.X + collider.Radius - 0.1f);
        var minZ = (int)MathF.Floor(position.Z - collider.Radius + 0.1f);
        var maxZ = (int)MathF.Floor(position.Z + collider.Radius - 0.1f);
        var y = (int)MathF.Floor(position.Y - 0.1f);

        var maxY = -float.MaxValue;
        var foundGround = false;

        for (var x = minX; x <= maxX; x++)
        {
            for (var z = minZ; z <= maxZ; z++)
            {
                var block = world.GetBlockByPositionGlobalSafe(x, y, z);
                if (block != null && block.Value.IsSolid)
                {
                    if (block.Value.Aabb.Max.Y > maxY)
                    {
                        maxY = block.Value.Aabb.Max.Y;
                        foundGround = true;
                    }
                }
            }
        }

        if (!foundGround)
        {
            isGrounded = false;
            return;
        }

        var snapDist = collider.StepHeight;
        if (position.Y <= maxY + snapDist && velocity.Y <= 0)
        {
            position.Y = maxY;
            velocity.Y = 0;
            isGrounded = true;
        }
        else
        {
            isGrounded = false;
        }
    }

    private static bool IsHeadHittingCeiling(VoxelWorld world, Vector3 position, in KinematicCollider collider)
    {
        var topY = position.Y + collider.Height;
        var checkRadius = collider.Radius - 0.05f;

        var offsets = new[]
        {
            new Vector3(-checkRadius, 0, -checkRadius),
            new Vector3(-checkRadius, 0, +checkRadius),
            new Vector3(+checkRadius, 0, -checkRadius),
            new Vector3(+checkRadius, 0, +checkRadius)
        };

        foreach (var off in offsets)
        {
            var p = position + off;
            var block = world.GetBlockByPositionGlobalSafe((int)p.X, (int)topY, (int)p.Z);
            if (block != null && block.Value.IsSolid)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBoxBlocked(VoxelWorld world, Vector3 position, in KinematicCollider collider)
    {
        var minX = (int)MathF.Floor(position.X - collider.Radius + 0.1f);
        var maxX = (int)MathF.Floor(position.X + collider.Radius - 0.1f);
        var minZ = (int)MathF.Floor(position.Z - collider.Radius + 0.1f);
        var maxZ = (int)MathF.Floor(position.Z + collider.Radius - 0.1f);
        var minY = (int)MathF.Floor(position.Y + 0.1f);
        var maxY = (int)MathF.Floor(position.Y + collider.Height - 0.1f);

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                for (var z = minZ; z <= maxZ; z++)
                {
                    var block = world.GetBlockByPositionGlobalSafe(x, y, z);
                    if (block != null && block.Value.IsSolid)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}

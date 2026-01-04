using DarkVox.Shared.World;

namespace DarkVox.Server.Mobs;

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
        var grounded = mob.OnGround;
        var enableAutoJump = !mob.Definition.CanFly;
        VoxelKinematicMover.Move(
            world,
            ref mob.Position,
            ref mob.Velocity,
            ref grounded,
            new KinematicCollider(
                Radius: mob.Definition.HitboxWidth * 0.5f,
                Height: mob.Definition.HitboxHeight,
                StepHeight: mob.Definition.StepHeight),
            delta,
            worldFloorY: 1.0f,
            enableAutoJump: enableAutoJump,
            gravityMagnitude: MathF.Abs(Gravity),
            autoJumpHorizontalDamping: 1.0f);
        mob.OnGround = grounded;
    }
}

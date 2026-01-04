using OpenTK.Mathematics;
using DarkVox.Shared.World;

namespace DarkVox.Shared.Gameplay;

/// <summary>
/// Mutable state container for player physics simulation.
/// Used by <see cref="PlayerPhysicsCore"/> to avoid ref-heavy APIs.
/// </summary>
public sealed class PlayerPhysicsState
{
    public Vector3 Position;
    public Vector3 Velocity;
    public Vector3 Direction;
    public bool IsGrounded;
    public bool IsGhostMode;
    public bool IsSprinting;
    public bool IsCrouching;
}

/// <summary>
/// Pure player physics simulation logic.
/// Shared between server (authoritative) and client (prediction/replay).
/// Contains NO rendering, camera, or UI code.
/// </summary>
public static class PlayerPhysicsCore
{
    /// <summary>
    /// Creates a kinematic collider using shared player constants.
    /// </summary>
    public static KinematicCollider CreateCollider() => new(
        Radius: PlayerConstants.HalfWidth,
        Height: PlayerConstants.Height,
        StepHeight: PlayerConstants.StepHeight);

    /// <summary>
    /// Builds a world-space movement vector from input axes and view direction.
    /// </summary>
    /// <param name="direction">Player's view direction (normalized).</param>
    /// <param name="moveAxes">Input axes: X = strafe, Y = forward/back.</param>
    /// <param name="verticalAxis">Vertical input for ghost mode.</param>
    /// <param name="isGhostMode">Whether ghost/fly mode is enabled.</param>
    public static Vector3 BuildMovementVector(Vector3 direction, Vector2 moveAxes, float verticalAxis, bool isGhostMode)
    {
        // Project direction to XZ plane for ground movement
        var forward = new Vector3(direction.X, 0, direction.Z);
        if (forward.LengthSquared < 0.0001f)
        {
            forward = -Vector3.UnitZ;
        }
        else
        {
            forward = forward.Normalized();
        }

        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var move = forward * moveAxes.Y + right * moveAxes.X;

        if (isGhostMode && Math.Abs(verticalAxis) > 0.001f)
        {
            move += Vector3.UnitY * verticalAxis;
        }

        return move;
    }

    /// <summary>
    /// Simulates ghost/fly mode movement (no collision, no gravity).
    /// </summary>
    public static void SimulateGhostMode(PlayerPhysicsState state, Vector3 requestedMovement, double elapsedSeconds)
    {
        state.Velocity = Vector3.Zero;
        var remaining = (float)elapsedSeconds;

        while (remaining > 0f && requestedMovement.LengthSquared > 0f)
        {
            var dt = MathF.Min(remaining, PlayerConstants.MaxPhysicsStepSeconds);
            var dir = requestedMovement.Normalized();
            state.Position += dir * dt * PlayerConstants.MoveSpeed * PlayerConstants.GhostModeMultiplier;
            remaining -= dt;
        }
    }

    /// <summary>
    /// Simulates standard physics movement with gravity and collision.
    /// </summary>
    public static void SimulateMovement(
        PlayerPhysicsState state,
        Vector3 requestedMovement,
        double elapsedSeconds,
        VoxelWorld world)
    {
        var remaining = (float)elapsedSeconds;
        var steps = 0;

        while (remaining > 0f)
        {
            var dt = MathF.Min(remaining, PlayerConstants.MaxPhysicsStepSeconds);
            ApplyMovementStep(state, requestedMovement, dt, world);
            remaining -= dt;
            steps++;

            if (steps > 64) break; // Safety valve
        }
    }

    private static void ApplyMovementStep(
        PlayerPhysicsState state,
        Vector3 moveInput,
        float dt,
        VoxelWorld world)
    {
        // Apply gravity
        state.Velocity.Y += PlayerConstants.Gravity * dt;

        // Calculate wish direction
        var wishDir = Vector3.Zero;
        if (moveInput.LengthSquared > 0.001f)
        {
            wishDir = moveInput.Normalized();
        }

        // Calculate speed with modifiers
        var speed = PlayerConstants.MoveSpeed;
        if (state.IsSprinting) speed *= PlayerConstants.SprintMultiplier;
        if (state.IsCrouching) speed *= PlayerConstants.CrouchMultiplier;

        if (state.IsGrounded)
        {
            // Ground movement: direct control
            state.Velocity.X = wishDir.X * speed;
            state.Velocity.Z = wishDir.Z * speed;
        }
        else if (wishDir.LengthSquared > 0.001f)
        {
            // Air control: allow steering while falling
            Accelerate(ref state.Velocity, wishDir, speed, PlayerConstants.AirControl * 10.0f, dt);
        }

        // Apply movement through collision system
        var collider = CreateCollider();
        VoxelKinematicMover.Move(
            world,
            ref state.Position,
            ref state.Velocity,
            ref state.IsGrounded,
            collider,
            state.Velocity * dt,
            enableAutoJump: true,
            gravityMagnitude: -PlayerConstants.Gravity,
            autoJumpHorizontalDamping: PlayerConstants.JumpHorizontalDamping);

        // Clamp horizontal velocity
        var maxSpeed = state.IsSprinting 
            ? PlayerConstants.MoveSpeed * PlayerConstants.SprintMultiplier 
            : (state.IsCrouching 
                ? PlayerConstants.MoveSpeed * PlayerConstants.CrouchMultiplier 
                : PlayerConstants.MoveSpeed);
                
        var hVel = new Vector2(state.Velocity.X, state.Velocity.Z);
        if (hVel.LengthSquared > maxSpeed * maxSpeed)
        {
            hVel = hVel.Normalized() * maxSpeed;
            state.Velocity.X = hVel.X;
            state.Velocity.Z = hVel.Y;
        }
    }

    private static void Accelerate(ref Vector3 velocity, Vector3 wishDir, float wishSpeed, float accel, float dt)
    {
        var currentSpeed = Vector3.Dot(velocity, wishDir);
        var addSpeed = wishSpeed - currentSpeed;
        if (addSpeed <= 0) return;

        var accelSpeed = accel * dt * wishSpeed;
        if (accelSpeed > addSpeed) accelSpeed = addSpeed;

        velocity.X += accelSpeed * wishDir.X;
        velocity.Z += accelSpeed * wishDir.Z;
    }

    /// <summary>
    /// Attempts to initiate a jump if conditions are met.
    /// </summary>
    /// <param name="state">Player physics state.</param>
    /// <param name="isUpBlocked">Whether there's a block directly above.</param>
    /// <param name="isInLowTunnel">Whether the player is in a low-headroom tunnel.</param>
    /// <returns>True if jump was initiated.</returns>
    public static bool TryJump(PlayerPhysicsState state, bool isUpBlocked, bool isInLowTunnel)
    {
        if (!state.IsGrounded || isUpBlocked || isInLowTunnel)
        {
            return false;
        }

        state.IsGrounded = false;
        state.Velocity.Y = PlayerConstants.JumpForce;
        state.Velocity.X *= PlayerConstants.JumpHorizontalDamping;
        state.Velocity.Z *= PlayerConstants.JumpHorizontalDamping;
        return true;
    }

    /// <summary>
    /// Applies knockback force to the player (from mob attacks, etc).
    /// </summary>
    public static void ApplyKnockback(PlayerPhysicsState state, Vector3 horizontalKnockback, float verticalKnockback)
    {
        state.Velocity.X += horizontalKnockback.X;
        state.Velocity.Z += horizontalKnockback.Z;
        if (state.IsGrounded || state.Velocity.Y < verticalKnockback)
        {
            state.Velocity.Y = verticalKnockback;
        }
    }

    /// <summary>
    /// Applies look rotation to a direction vector (server-side, matching CameraFps coordinate system).
    /// </summary>
    public static Vector3 ApplyLookRotation(Vector3 direction, float yawDelta, float pitchDelta)
    {
        const float degToRad = MathF.PI / 180.0f;

        // CameraFps uses: front = (cosYaw * cosPitch, sinPitch, sinYaw * cosPitch)
        var currentYaw = MathF.Atan2(direction.Z, direction.X);
        var currentPitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));

        var yawRad = yawDelta * PlayerConstants.RotationSpeed * degToRad;
        var pitchRad = pitchDelta * PlayerConstants.RotationSpeed * degToRad;

        currentYaw -= yawRad;
        currentPitch = Math.Clamp(currentPitch + pitchRad, -MathF.PI / 2f + 0.01f, MathF.PI / 2f - 0.01f);

        var cosPitch = MathF.Cos(currentPitch);
        return new Vector3(
            MathF.Cos(currentYaw) * cosPitch,
            MathF.Sin(currentPitch),
            MathF.Sin(currentYaw) * cosPitch
        ).Normalized();
    }
}

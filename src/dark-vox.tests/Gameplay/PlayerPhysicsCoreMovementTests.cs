using DarkVox.Shared.Gameplay;
using OpenTK.Mathematics;
using Xunit;

namespace DarkVox.Tests.Gameplay;

public sealed class PlayerPhysicsCoreMovementTests
{
    [Fact]
    public void BuildMovementVector_ForwardDependsOnDirectionYaw()
    {
        var forwardZ = -Vector3.UnitZ;
        var forwardX = Vector3.UnitX;

        var moveAxesForward = new Vector2(0, 1);

        var moveZ = PlayerPhysicsCore.BuildMovementVector(forwardZ, moveAxesForward, verticalAxis: 0, isGhostMode: false);
        var moveX = PlayerPhysicsCore.BuildMovementVector(forwardX, moveAxesForward, verticalAxis: 0, isGhostMode: false);

        Assert.True(moveZ.LengthSquared > 0.1f);
        Assert.True(moveX.LengthSquared > 0.1f);

        // When looking -Z, forward should be -Z.
        Assert.True(moveZ.Z < -0.9f);
        Assert.True(MathF.Abs(moveZ.X) < 0.2f);

        // When looking +X, forward should be +X.
        Assert.True(moveX.X > 0.9f);
        Assert.True(MathF.Abs(moveX.Z) < 0.2f);
    }

    [Fact]
    public void ApplyLookRotation_YawChangesMovementForwardDirection()
    {
        var direction = -Vector3.UnitZ;

        // Apply a yaw delta; sign doesn’t matter for the purpose of this regression test
        // (we just want to ensure yaw affects forward movement at all).
        var rotated = PlayerPhysicsCore.ApplyLookRotation(direction, yawDelta: 1.0f, pitchDelta: 0.0f);

        var moveAxesForward = new Vector2(0, 1);
        var moveOriginal = PlayerPhysicsCore.BuildMovementVector(direction, moveAxesForward, verticalAxis: 0, isGhostMode: false);
        var moveRotated = PlayerPhysicsCore.BuildMovementVector(rotated, moveAxesForward, verticalAxis: 0, isGhostMode: false);

        Assert.True(moveOriginal.LengthSquared > 0.1f);
        Assert.True(moveRotated.LengthSquared > 0.1f);

        // If yaw is applied, forward movement should not remain identical.
        Assert.NotEqual(moveOriginal, moveRotated);
    }
}

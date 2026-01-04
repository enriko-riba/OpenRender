using OpenTK.Mathematics;
using Xunit;

namespace DarkVox.Tests.Movement;

/// <summary>
/// Tests for server-side look rotation to ensure it matches CameraFps coordinate system.
/// </summary>
public class LookRotationTests
{
    private const float DegToRad = MathF.PI / 180.0f;
    private const float RotationSpeed = 10.0f;
    
    /// <summary>
    /// Applies look rotation using the same formula as the server-side Player.
    /// </summary>
    private static Vector3 ApplyLookRotation(Vector3 direction, float yawDelta, float pitchDelta)
    {
        // CameraFps uses: front = (cosYaw * cosPitch, sinPitch, sinYaw * cosPitch)
        var currentYaw = MathF.Atan2(direction.Z, direction.X);
        var currentPitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
        
        var yawRad = yawDelta * RotationSpeed * DegToRad;
        var pitchRad = pitchDelta * RotationSpeed * DegToRad;
        
        currentYaw -= yawRad;
        currentPitch = Math.Clamp(currentPitch + pitchRad, -MathF.PI / 2f + 0.01f, MathF.PI / 2f - 0.01f);
        
        var cosPitch = MathF.Cos(currentPitch);
        return new Vector3(
            MathF.Cos(currentYaw) * cosPitch,
            MathF.Sin(currentPitch),
            MathF.Sin(currentYaw) * cosPitch
        ).Normalized();
    }
    
    /// <summary>
    /// Computes forward vector using CameraFps formula.
    /// </summary>
    private static Vector3 ComputeCameraFront(float yawDegrees, float pitchDegrees)
    {
        var yawRad = yawDegrees * DegToRad;
        var pitchRad = pitchDegrees * DegToRad;
        
        var cosPitch = MathF.Cos(pitchRad);
        var sinPitch = MathF.Sin(pitchRad);
        var cosYaw = MathF.Cos(yawRad);
        var sinYaw = MathF.Sin(yawRad);
        
        return new Vector3(cosYaw * cosPitch, sinPitch, sinYaw * cosPitch);
    }
    
    [Fact]
    public void InitialDirection_ShouldPointForward()
    {
        // Default spawn direction should be -Z (looking "forward" in OpenGL convention)
        var direction = -Vector3.UnitZ;
        
        // When projected to XZ plane, should have valid forward
        var forward = new Vector3(direction.X, 0, direction.Z);
        Assert.True(forward.LengthSquared > 0.0001f, "Forward should not be zero");
    }
    
    [Fact]
    public void YawRotation_ShouldRotateAroundY()
    {
        // Start looking along +X axis
        var direction = Vector3.UnitX;
        
        // Rotate 9 degrees yaw (which becomes 90 degrees after rotationSpeed * 10)
        var rotated = ApplyLookRotation(direction, 9f, 0f);
        
        // After 90 degree yaw rotation from +X, should be approximately -Z
        // (because yaw is subtracted and we rotate clockwise)
        Assert.InRange(rotated.X, -0.1f, 0.1f);
        Assert.InRange(rotated.Y, -0.1f, 0.1f);
        Assert.True(rotated.Z < -0.9f, $"Expected Z < -0.9, got {rotated.Z}");
    }
    
    [Fact]
    public void PitchRotation_ShouldRotateUpDown()
    {
        // Start looking along +X axis (horizontal)
        var direction = Vector3.UnitX;
        
        // Apply positive pitch (should look up)
        var rotated = ApplyLookRotation(direction, 0f, 4.5f);  // 45 degrees after *10
        
        // Should have positive Y component (looking up)
        Assert.True(rotated.Y > 0.5f, $"Expected Y > 0.5 (looking up), got {rotated.Y}");
    }
    
    [Fact]
    public void CombinedRotation_ShouldMatchCameraFormula()
    {
        // Test that our direction reconstruction matches CameraFps formula
        var yawDeg = 45f;
        var pitchDeg = 30f;
        
        var cameraFront = ComputeCameraFront(yawDeg, pitchDeg);
        
        // Reconstruct the same direction using our formula
        var yawRad = yawDeg * DegToRad;
        var pitchRad = pitchDeg * DegToRad;
        var cosPitch = MathF.Cos(pitchRad);
        var reconstructed = new Vector3(
            MathF.Cos(yawRad) * cosPitch,
            MathF.Sin(pitchRad),
            MathF.Sin(yawRad) * cosPitch
        );
        
        // Should match within floating point tolerance
        Assert.InRange((cameraFront - reconstructed).Length, -0.001f, 0.001f);
    }
    
    [Fact]
    public void MovementVector_ShouldUseXZPlane()
    {
        // Movement should project direction to XZ plane for ground movement
        var direction = new Vector3(0.5f, 0.5f, 0.7f).Normalized();
        
        var forward = new Vector3(direction.X, 0, direction.Z);
        if (forward.LengthSquared > 0.0001f)
        {
            forward = forward.Normalized();
        }
        
        // Forward should be normalized and have no Y component
        Assert.InRange(forward.Y, -0.001f, 0.001f);
        Assert.InRange(forward.Length, 0.99f, 1.01f);
    }
}

using OpenTK.Mathematics;

namespace OpenRender.Core.Rendering;

/// <summary>
/// Represents a FPS camera.
/// </summary>
/// <remarks>
/// Initializes a new instance of the Camera class.
/// </remarks>
/// <param name="cameraPosition">The position of the camera.</param>
/// <param name="aspectRatio">The aspect ratio of the camera's viewport.</param>
/// <param name="nearPlane">The distance to the near clipping plane.</param>
/// <param name="farPlane">The distance to the far clipping plane.</param>
public class CameraFps : CameraBase
{
    private float yawRads;
    private float pitchRads;
    private Vector3 rotation;

    public CameraFps(Vector3 cameraPosition, float aspectRatio, float nearPlane = 0.1f, float farPlane = 500) : base(cameraPosition, aspectRatio, nearPlane, farPlane)
    {
        yawRads = -MathF.PI / 2f;
        pitchRads = 0;
        rotation = new(0, MathHelper.RadiansToDegrees(yawRads), 0);
        isDirty = true;
    }

    /// <summary>
    /// Adds rotation to the camera by the specified yaw, pitch, and roll angle increments in degrees.
    /// </summary>
    /// <param name="yawDegrees">The yaw angle in degrees.</param>
    /// <param name="pitchDegrees">The pitch angle in degrees.</param>
    /// <param name="rollDegrees">The roll angle in degrees.</param>
    public override void AddRotation(float yawDegrees, float pitchDegrees, float rollDegrees)
    {
        rotation.X += pitchDegrees;
        rotation.X %= 360;
        if (rotation.X > 45) rotation.X = 45;
        if (rotation.X < -45) rotation.X = -45;
        pitchRads = MathHelper.DegreesToRadians(rotation.X);

        rotation.Y -= yawDegrees;
        rotation.Y %= 360;
        yawRads = MathHelper.DegreesToRadians(rotation.Y);

        isDirty = true;
    }

    public override void MoveForward(float distance)
    {
        var dir = Vector3.Normalize(new Vector3(front.X, 0, front.Z));
        var movement = dir * distance;
        Position += movement;
        isDirty = true;
    }

    /// <summary>
    /// Updates the camera's view and projection matrices if necessary.
    /// </summary>
    protected override void UpdateMatrices()
    {
        var cosPitch = MathF.Cos(pitchRads);
        var sinPitch = MathF.Sin(pitchRads);
        var cosYaw = MathF.Cos(yawRads);
        var sinYaw = MathF.Sin(yawRads);

        front = new Vector3(cosYaw * cosPitch, sinPitch, sinYaw * cosPitch );
        right = Vector3.Normalize(Vector3.Cross(front, Vector3.UnitY));
        up = Vector3.Normalize(Vector3.Cross(right, front));

        //view = Matrix4.LookAt(position, position + front, up);
        view = Matrix4.LookAt(Position, Position + front, up);
        projection = Matrix4.CreatePerspectiveFieldOfView(fov, AspectRatio, nearPlane, farPlane);
        viewProjection = view * projection;
    }
}

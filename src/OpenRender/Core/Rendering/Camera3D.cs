using OpenTK.Mathematics;

namespace OpenRender.Core.Rendering;

/// <summary>
/// Represents a 3D camera.
/// </summary>
public class Camera3D : CameraBase
{
    /// <summary>
    /// Initializes a new instance of the Camera class.
    /// </summary>
    /// <param name="position">The position of the camera.</param>
    /// <param name="aspectRatio">The aspect ratio of the camera's viewport.</param>
    /// <param name="nearPlane">The distance to the near clipping plane.</param>
    /// <param name="farPlane">The distance to the far clipping plane.</param>
    public Camera3D(Vector3 position, float aspectRatio, float nearPlane = 0.0001f, float farPlane = 5000) : base(position, aspectRatio, nearPlane, farPlane) { }

    /// <summary>
    /// Moves the camera forward by the specified distance.
    /// </summary>
    /// <param name="distance">The distance to move the camera forward.</param>
    public void MoveForward(float distance)
    {
        Position -= Front * distance;
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
        var yawR = MathHelper.DegreesToRadians(yawDegrees);
        var pitchR = MathHelper.DegreesToRadians(pitchDegrees);
        var rollR = MathHelper.DegreesToRadians(rollDegrees);
        if (yawR != 0)
        {
            orientation *= Quaternion.FromEulerAngles(0, yawR, 0);
        }
        if (pitchR != 0)
        {
            orientation *= Quaternion.FromEulerAngles(pitchR, 0, 0);
        }
        if (rollR != 0)
        {
            orientation *= Quaternion.FromEulerAngles(0, 0, rollR);
        }
        orientation.Normalize();
        isDirty = true;
    }

    /// <summary>
    /// Updates the camera's view and projection matrices if necessary.
    /// </summary>
    protected override void UpdateMatrices()
    {
        front = Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, orientation));
        right = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, orientation));
        up = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, orientation));
        view = Matrix4.LookAt(Position, Position + front, up);
        projection = Matrix4.CreatePerspectiveFieldOfView(fov, AspectRatio, nearPlane, farPlane);
        viewProjection = view * projection;
    }
}

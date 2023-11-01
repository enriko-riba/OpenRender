using OpenTK.Mathematics;

namespace OpenRender.Core.Rendering;

/// <summary>
/// Represents a 3D camera.
/// </summary>
public class Camera2D : CameraBase
{
    private float clientWidth;
    private float clientHeight;

    /// <summary>
    /// Initializes a new instance of the Camera class.
    /// </summary>
    /// <param name="position">The position of the camera.</param>
    /// <param name="clientWidth">The viewport width.</param>
    /// <param name="clientHeight">The viewport height.</param>
    public Camera2D(Vector3 position, float clientWidth, float clientHeight) : base(position, 1, 0, 1) 
    {
        this.clientWidth = clientWidth;
        this.clientHeight = clientHeight;
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
        if (pitchR != 0)
        {
            orientation *= Quaternion.FromEulerAngles(pitchR, 0, 0);
        }
        if (yawR != 0)
        {
            orientation *= Quaternion.FromEulerAngles(0, yawR, 0);
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
        front = -Vector3.UnitZ;
        right = Vector3.UnitX;
        up = Vector3.UnitY;
        view = Matrix4.LookAt(Position, Position + front, up);
        projection = Matrix4.CreateOrthographicOffCenter(0, clientWidth, clientHeight, 0, -1, 1);
        viewProjection = view * projection;
    }
}

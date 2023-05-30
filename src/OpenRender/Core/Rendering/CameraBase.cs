using OpenRender.SceneManagement;
using OpenTK.Mathematics;

namespace OpenRender.Core.Rendering;

public abstract class CameraBase : ICamera
{
    protected Quaternion orientation = Quaternion.Identity;
    protected Matrix4 projection;
    protected Matrix4 view;
    protected Matrix4 viewProjection;

    protected Vector3 front = -Vector3.UnitZ;
    protected Vector3 right = Vector3.UnitX;
    protected Vector3 up = Vector3.UnitY;
    protected float fov = MathHelper.PiOver6;

    private Vector3 position;
    private float aspectRatio;
    
    protected bool isDirty;
    protected readonly float nearPlane;
    protected readonly float farPlane;

    /// <summary>
    /// Initializes a new instance of the Camera class.
    /// </summary>
    /// <param name="position">The position of the camera.</param>
    /// <param name="aspectRatio">The aspect ratio of the camera's viewport.</param>
    /// <param name="nearPlane">The distance to the near clipping plane.</param>
    /// <param name="farPlane">The distance to the far clipping plane.</param>
    public CameraBase(Vector3 position, float aspectRatio, float nearPlane, float farPlane)
    {
        Position = position;
        AspectRatio = aspectRatio;
        this.nearPlane = nearPlane;
        this.farPlane = farPlane;
        UpdateMatrices();
    }

    /// <summary>
    /// Gets the orientation of the camera.
    /// </summary>
    public Quaternion Orientation => orientation;

    /// <summary>
    /// Gets the projection matrix of the camera.
    /// </summary>
    public Matrix4 ProjectionMatrix => projection;

    /// <summary>
    /// Gets the view matrix of the camera.
    /// </summary>
    public Matrix4 ViewMatrix => view;

    /// <summary>
    /// Gets the view projection matrix of the camera.
    /// </summary>
    public Matrix4 ViewProjectionMatrix => viewProjection;

    /// <summary>
    /// Gets the front vector of the camera.
    /// </summary>
    public Vector3 Front => front;

    /// <summary>
    /// Gets the right vector of the camera.
    /// </summary>
    public Vector3 Right => right;

    /// <summary>
    /// Gets the up vector of the camera.
    /// </summary>
    public Vector3 Up => up;

    /// <summary>
    /// Gets the yaw angle of the camera in degrees.
    /// </summary>
    public float Yaw =>
        MathHelper.RadiansToDegrees((float)Math.Atan2(2 * orientation.Y * orientation.W - 2 * orientation.X * orientation.Z,
            1 - 2 * orientation.Y * orientation.Y - 2 * orientation.Z * orientation.Z));

    /// <summary>
    /// Gets the pitch angle of the camera in degrees.
    /// </summary>
    public float Pitch =>
        MathHelper.RadiansToDegrees((float)Math.Asin(2 * orientation.X * orientation.Y + 2 * orientation.Z * orientation.W));

    /// <summary>
    /// Gets the roll angle of the camera in degrees.
    /// </summary>
    public float Roll =>
        MathHelper.RadiansToDegrees((float)Math.Atan2(2 * orientation.X * orientation.W - 2 * orientation.Y * orientation.Z,
            1 - 2 * orientation.X * orientation.X - 2 * orientation.Z * orientation.Z));

    /// <summary>
    /// Gets or sets the field of view (FOV) of the camera in degrees.
    /// </summary>
    public float Fov
    {
        get => MathHelper.RadiansToDegrees(fov);
        set
        {
            var angle = MathHelper.Clamp(value, 1f, 85f);
            fov = MathHelper.DegreesToRadians(angle);
            isDirty = true;
        }
    }

    /// <summary>
    /// Gets or sets the position of the camera.
    /// </summary>
    public Vector3 Position
    {
        get => position;
        set
        {
            position = value;
            isDirty = true;
        }
    }

    /// <summary>
    /// Gets or sets the aspect ratio of the camera's viewport.
    /// </summary>
    public float AspectRatio
    {
        get => aspectRatio;
        set
        {
            aspectRatio = value;
            isDirty = true;
        }
    }    

    /// <summary>
    /// Adds rotation to the camera by the specified yaw, pitch, and roll angle increments in degrees.
    /// </summary>
    /// <param name="yawDegrees">The yaw angle in degrees.</param>
    /// <param name="pitchDegrees">The pitch angle in degrees.</param>
    /// <param name="rollDegrees">The roll angle in degrees.</param>
    public abstract void AddRotation(float yawDegrees, float pitchDegrees, float rollDegrees);

    /// <summary>
    /// Updates the camera's view and projection matrices if necessary.
    /// </summary>
    protected abstract void UpdateMatrices();

    /// <summary>
    /// Updates the camera. Recalculates the matrices if necessary and resets the dirty flag.
    /// </summary>
    public bool Update()
    {
        if (!isDirty) return false;

        UpdateMatrices();
        isDirty = false;
        return true;
    }
}
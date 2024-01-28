using OpenRender.Core.Culling;
using OpenTK.Mathematics;

namespace OpenRender.Core.Rendering;
public interface ICamera
{
    public event EventHandler CameraChanged;

    Matrix4 ProjectionMatrix { get; }
    Matrix4 ViewMatrix { get; }
    Matrix4 ViewProjectionMatrix { get; }
    Vector3 Front { get; }
    Vector3 Up { get; }
    Vector3 Right { get; }
    Vector3 Position { get; set; }
    Quaternion Orientation { get; set; }
    bool IsDirty { get; }
    public float MaxFov { get; set; }
    float Fov { get; set; }
    float AspectRatio { get; set; }
    float NearPlaneDistance { get; }
    float FarPlaneDistance { get; }
    void AddRotation(float yawDegrees, float pitchDegrees, float rollDegrees);
    void MoveForward(float distance);
    void Invalidate();

    Frustum Frustum { get; }

    /// <summary>
    /// If dirty, updates camera matrices.
    /// </summary>
    /// <returns>true if camera has been updated else false</returns>
    bool Update();
}
using OpenTK.Mathematics;

namespace OpenRender.Core.Rendering;
public interface ICamera
{
    Matrix4 ProjectionMatrix { get; }
    Matrix4 ViewMatrix { get; }
    Vector3 Front { get; }
    Vector3 Up { get; }
    Vector3 Right { get; }
    Vector3 Position { get; set; }
    float Fov { get; set; }
    float AspectRatio { get; set; }
    void AddRotation(float yawDegrees, float pitchDegrees, float rollDegrees);
    void Update();
}
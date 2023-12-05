using OpenRender.Core.Rendering;
using OpenTK.Mathematics;

namespace OpenRender.Core.Culling;

public class Frustum
{
    private readonly Vector4[] planes = new Vector4[6];

    public Vector4[] Planes => planes;

    public void Update(in ICamera camera) => ExtractPlanes(camera.ViewProjectionMatrix);

    private void ExtractPlanes(in Matrix4 m)
    {
        // Left plane
        planes[0] = new Vector4(
            m.M14 - m.M11,
            m.M24 - m.M21,
            m.M34 - m.M31,
            m.M44 - m.M41
        );
        NormalizePlane(ref planes[0]);

        // Right plane
        planes[1] = new Vector4(
            m.M14 + m.M11,
            m.M24 + m.M21,
            m.M34 + m.M31,
            m.M44 + m.M41
        );
        NormalizePlane(ref planes[1]);

        // Bottom plane
        planes[2] = new Vector4(
            m.M14 + m.M12,
            m.M24 + m.M22,
            m.M34 + m.M32,
            m.M44 + m.M42
        );
        NormalizePlane(ref planes[2]);

        // Top plane
        planes[3] = new Vector4(
            m.M14 - m.M12,
            m.M24 - m.M22,
            m.M34 - m.M32,
            m.M44 - m.M42
        );
        NormalizePlane(ref planes[3]);

        // Near plane
        planes[4] = new Vector4(
           m.M14 + m.M13,
           m.M24 + m.M23,
           m.M34 + m.M33,
           m.M44 + m.M43
        );
        NormalizePlane(ref planes[4]);

        // Far plane
        planes[5] = new Vector4(
            m.M14 - m.M13,
            m.M24 - m.M23,
            m.M34 - m.M33,
            m.M44 - m.M43
        );
        NormalizePlane(ref planes[5]);
    }

    private static void NormalizePlane(ref Vector4 plane)
    {
        var mag = 1 / MathF.Sqrt(plane.X * plane.X + plane.Y * plane.Y + plane.Z * plane.Z);
        plane.X *= mag;
        plane.Y *= mag;
        plane.Z *= mag;
        plane.W *= mag;
    }
}
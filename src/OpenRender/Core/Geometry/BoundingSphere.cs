
using OpenTK.Mathematics;

namespace OpenRender.Core.Geometry;

public struct BoundingSphere
{
    public Vector3 LocalCenter { get; set; }
    public float LocalRadius { get; set; }
    public Vector3 Center { get; set; }
    public float Radius { get; set; }

    public void Update(in Vector3 scale, in Matrix4 worldMatrix)
    {
        Radius = LocalRadius * MathF.MaxMagnitude(MathF.MaxMagnitude(scale.X, scale.Y), scale.Z);
        Center = Vector3.TransformPosition(LocalCenter, worldMatrix);
    }

    /// <summary>
    /// Returns the default BoundingSphere with radius1 and canter at origin.
    /// </summary>
    public static BoundingSphere Default => new()
    {
        LocalCenter = Vector3.Zero,
        LocalRadius = 0.5f,
        Center = Vector3.Zero,
        Radius = 0.5f,
    };
};
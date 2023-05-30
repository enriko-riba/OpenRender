
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
};
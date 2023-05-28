
using OpenTK.Mathematics;

namespace OpenRender.Core.Geometry;

public record BoundingSphere(Vector3 LocalCenter, float LocalRadius)
{
    public Vector3 Center { get; set; }
    public float Radius { get; set; }
};
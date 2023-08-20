using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;

namespace OpenRender.Core.Culling;

internal sealed class CullingHelper
{
    public static void CullNodes(Frustum frustum, IEnumerable<SceneNode> allNodes)
    {
        var planes = frustum.Planes;
        foreach (var node in allNodes)
        {
            if (node.IsVisible && !node.DisableCulling)
            {
                if (!IsSphereInFrustum(planes, node.BoundingSphere.Center, node.BoundingSphere.Radius))
                {
                    node.FrameBits.SetFlag(FrameBitsFlags.FrustumCulled);
                }
                else
                {
                    node.FrameBits.ClearFlag(FrameBitsFlags.FrustumCulled);
                }
            }
        }
    }

    public static bool IsSphereInFrustum(in Vector4[] planes, in Vector3 sphereCenter, float sphereRadius)
    {
        for (var i = 0; i < 6; ++i)
            if (planes[i].X * sphereCenter.X +
                planes[i].Y * sphereCenter.Y +
                planes[i].Z * sphereCenter.Z +
                planes[i].W <= -sphereRadius)
                return false;

        // If the sphere passed all frustum plane tests, it is inside the frustum
        return true;
    }

    public static BoundingSphere CalculateBoundingSphere(VertexBuffer vb)
    {
        if (vb == null) return new BoundingSphere();
        List<Vector3> positions = new();
        var strideInFloats = vb.Stride / sizeof(float);
        if (vb.Indices != null && vb.Indices.Length > 0)
        {
            for (var i = 0; i < vb.Indices.Length; i++)
            {
                var idx = vb.Indices[i] * strideInFloats;
                var x = vb.Vertices[idx];
                var y = vb.Vertices[idx + 1];
                var z = vb.Vertices[idx + 2];
                positions.Add(new Vector3(x, y, z));
            }
        }
        else
        {
            var positionOffset = (int)AttributeLocation.Position;
            for (var i = 0; i < vb.Vertices.Length; i += strideInFloats)
            {
                var x = vb.Vertices[i + positionOffset];
                var y = vb.Vertices[i + positionOffset + 1];
                var z = vb.Vertices[i + positionOffset + 2];
                positions.Add(new Vector3(x, y, z));
            }
        }

        // Calculate the center of the bounding sphere
        var center = CalculateBoundingSphereCenter(positions);

        // Calculate the radius of the bounding sphere
        var radius = CalculateBoundingSphereRadius(center, positions);
        return new BoundingSphere { LocalCenter = center, LocalRadius = radius, Center = center, Radius = radius };
    }

    private static Vector3 CalculateBoundingSphereCenter(IReadOnlyList<Vector3> positions)
    {
        var center = Vector3.Zero;
        foreach (var position in positions)
        {
            center += position;
        }
        center /= positions.Count;
        return center;
    }

    private static float CalculateBoundingSphereRadius(in Vector3 center, IReadOnlyList<Vector3> positions)
    {
        var radius = 0f;
        foreach (var position in positions)
        {
            var distance = Vector3.Distance(center, position);
            radius = Math.Max(radius, distance);
        }
        return radius;
    }
}

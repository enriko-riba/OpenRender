using OpenRender.Core.Geometry;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;

namespace OpenRender.Core.Rendering;

internal class CullingHelper
{
    private Vector4[] frustumPlanes = new Vector4[6];

    public void CullNodes(IEnumerable<SceneNode> allNodes, List<SceneNode> renderList)
    {
        renderList.Clear();
        foreach (var node in allNodes)
        {
            if (IsSphereInFrustum(node.BoundingSphere.Center, node.BoundingSphere.Radius))
            {
                renderList.Add(node);
            }
        }
    }

    public bool IsSphereInFrustum(in Vector3 sphereCenter, float sphereRadius)
    {
        for (var i = 0; i < 6; ++i)
            if (frustumPlanes[i].X * sphereCenter.X +
                frustumPlanes[i].Y * sphereCenter.Y +
                frustumPlanes[i].Z * sphereCenter.Z +
                frustumPlanes[i].W <= -sphereRadius)
                return false;

        // If the sphere passed all frustum plane tests, it is inside the frustum
        return true;
    }

    public void ExtractFrustumPlanes(in Matrix4 viewProjection)
    {
        // Left plane
        frustumPlanes[0] = new Vector4(
            viewProjection.M14 - viewProjection.M11,
            viewProjection.M24 - viewProjection.M21,
            viewProjection.M34 - viewProjection.M31,
            viewProjection.M44 - viewProjection.M41
        );

        // Right plane
        frustumPlanes[1] = new Vector4(
            viewProjection.M14 + viewProjection.M11,
            viewProjection.M24 + viewProjection.M21,
            viewProjection.M34 + viewProjection.M31,
            viewProjection.M44 + viewProjection.M41
        );

        // Bottom plane
        frustumPlanes[2] = new Vector4(
            viewProjection.M14 + viewProjection.M12,
            viewProjection.M24 + viewProjection.M22,
            viewProjection.M34 + viewProjection.M32,
            viewProjection.M44 + viewProjection.M42
        );

        // Top plane
        frustumPlanes[3] = new Vector4(
            viewProjection.M14 - viewProjection.M12,
            viewProjection.M24 - viewProjection.M22,
            viewProjection.M34 - viewProjection.M32,
            viewProjection.M44 - viewProjection.M42
        );

        // Near plane
        frustumPlanes[4] = new Vector4(
            viewProjection.M14 - viewProjection.M13,
            viewProjection.M24 - viewProjection.M23,
            viewProjection.M34 - viewProjection.M33,
            viewProjection.M44 - viewProjection.M43
        );

        // Far plane
        frustumPlanes[5] = new Vector4(
            viewProjection.M14 + viewProjection.M13,
            viewProjection.M24 + viewProjection.M23,
            viewProjection.M34 + viewProjection.M33,
            viewProjection.M44 + viewProjection.M43
        );

        // Normalize the plane normals
        for (var i = 0; i < 6; i++)
        {
            frustumPlanes[i].Normalize();
        }
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
            for (var i = 0; i < vb.Vertices.Length; i += vb.Stride)
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

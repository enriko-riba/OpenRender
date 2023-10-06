using OpenRender.Core.Geometry;
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

    /// <summary>
    /// Calculates the bounding sphere of a vertex buffer.
    /// </summary>
    /// <param name="data">the vertex buffer data with vertex positions</param>
    /// <param name="vertexPositionAttributeOffset">the offset of the vertex positions in floats</param>
    /// <param name="strideInFloats">the stride in floats of the vertex buffer</param>
    /// <returns></returns>
    public static unsafe BoundingSphere CalculateBoundingSphere(float[] data, int vertexPositionAttributeOffset, int strideInFloats)
    {
        if (data.Length == 0) return new BoundingSphere();
        List<Vector3> positions = new();
        for (var i = 0; i < data.Length; i += strideInFloats)
        {
            var x = data[i + vertexPositionAttributeOffset];
            var y = data[i + vertexPositionAttributeOffset + 1];
            var z = data[i + vertexPositionAttributeOffset + 2];
            positions.Add(new Vector3(x, y, z));
        }
        //var vbFloat = (VertexBuffer)vb;
        //var data = vb.Data;
        //if (vb.Indices != null && vb.Indices.Length > 0)
        //{
        //    for (var i = 0; i < vb.Indices.Length; i++)
        //    {
        //        var idx = vb.Indices[i] * strideInFloats;
        //        var x = data[idx + vertexPositionAttributeOffset];
        //        var y = data[idx + vertexPositionAttributeOffset + 1];
        //        var z = data[idx + vertexPositionAttributeOffset + 2];
        //        positions.Add(new Vector3(x, y, z));
        //    }
        //}
        //else
        //{
        //    for (var i = 0; i < data.Length; i += strideInFloats)
        //    {
        //        var x = data[i + vertexPositionAttributeOffset];
        //        var y = data[i + vertexPositionAttributeOffset + 1];
        //        var z = data[i + vertexPositionAttributeOffset + 2];
        //        positions.Add(new Vector3(x, y, z));
        //    }
        //}

        // Calculate the center of the bounding sphere
        var center = CalculateBoundingSphereCenter(positions);

        // Calculate the radius of the bounding sphere
        var radius = CalculateBoundingSphereRadius(center, positions);
        return new BoundingSphere { LocalCenter = center, LocalRadius = radius, Center = center, Radius = radius };
    }

    private static Vector3 CalculateBoundingSphereCenter(IEnumerable<Vector3> positions)
    {
        var center = Vector3.Zero;
        foreach (var position in positions)
        {
            center += position;
        }
        center /= positions.Count();
        return center;
    }

    private static float CalculateBoundingSphereRadius(in Vector3 center, IEnumerable<Vector3> positions)
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

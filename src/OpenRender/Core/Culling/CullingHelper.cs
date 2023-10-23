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
    /// <param name="strideInFloats">the stride in floats of the vertex buffer</param>
    /// <param name="vertices">the vertex buffer data with vertex positions</param>
    /// <returns></returns>
    public static unsafe BoundingSphere CalculateBoundingSphere(int strideInFloats, float[] vertices)
    {
        var center = CalculateBoundingSphereCenter(strideInFloats, vertices);
        var radius = CalculateBoundingSphereRadius(center, strideInFloats, vertices);
        return new BoundingSphere { LocalCenter = center, LocalRadius = radius, Center = center, Radius = radius };
    }

    private static Vector3 CalculateBoundingSphereCenter(int strideInFloats, float[] vertices)
    {
        var center = Vector3.Zero;
        for (var i = 0; i < vertices.Length;)
        {
            var vec3 = new Vector3(vertices[i], vertices[i + 1], vertices[i + 2]);
            center += vec3;
            i += strideInFloats;
        }
        center /= vertices.Length;
        return center;
    }

    private static float CalculateBoundingSphereRadius(in Vector3 center, int strideInFloats, float[] vertices)
    {
        var radius = 0f;
        for (var i = 0; i < vertices.Length;)
        {
            var vec3 = new Vector3(vertices[i], vertices[i + 1], vertices[i + 2]);
            var distance = Vector3.Distance(center, vec3);
            radius = Math.Max(radius, distance);
            i += strideInFloats;
        }
        return radius;
    }
}

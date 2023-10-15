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

    /// <summary>
    /// Calculates the bounding sphere of a vertex buffer.
    /// </summary>
    /// <param name="vertices">the vertex buffer data with vertex positions</param>
    /// <returns></returns>
    public static unsafe BoundingSphere CalculateBoundingSphere(Vertex[] vertices)
    {
        var center = CalculateBoundingSphereCenter(vertices);
        var radius = CalculateBoundingSphereRadius(center, vertices);
        return new BoundingSphere { LocalCenter = center, LocalRadius = radius, Center = center, Radius = radius };
    }

    private static Vector3 CalculateBoundingSphereCenter(IEnumerable<Vertex> vertices)
    {
        var center = Vector3.Zero;
        foreach (var vertex in vertices)
        {
            center += vertex.Position;
        }
        center /= vertices.Count();
        return center;
    }

    private static float CalculateBoundingSphereRadius(in Vector3 center, IEnumerable<Vertex> vertices)
    {
        var radius = 0f;
        foreach (var vertex in vertices)
        {
            var distance = Vector3.Distance(center, vertex.Position);
            radius = Math.Max(radius, distance);
        }
        return radius;
    }
}

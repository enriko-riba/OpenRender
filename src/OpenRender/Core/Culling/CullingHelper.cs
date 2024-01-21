global using AABB = (OpenTK.Mathematics.Vector3 min, OpenTK.Mathematics.Vector3 max);

using OpenRender.Core.Geometry;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using System.Drawing;

namespace OpenRender.Core.Culling;

public sealed class CullingHelper
{
    public static void FrustumCull(Frustum frustum, IEnumerable<SceneNode> allNodes)
    {
        //var planes = frustum.Planes;
        foreach (var node in allNodes)
        {
            if (node.IsVisible && !node.DisableCulling)
            {
                if (!node.CullAction(frustum))
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

    public static bool IsPointInsideAabb(Vector3 point, AABB box) => (
          point.X >= box.min.X &&
          point.X <= box.max.X &&
          point.Y >= box.min.Y &&
          point.Y <= box.max.Y &&
          point.Z >= box.min.Z &&
          point.Z <= box.max.Z
    );

    private static readonly Vector3[] corners = new Vector3[8];

    public static bool IsAabbInFrustum(in AABB aabb, in Vector4[] frustumPlanes)
    {
        //var corners = new Vector3[8];
        var min = aabb.min;
        var max = aabb.max;

        // Calculate the eight corners of the AABB
        corners[0].X = min.X;
        corners[0].Y = min.Y;
        corners[0].Z = min.Z;

        corners[1].X = max.X;
        corners[1].Y = min.Y;
        corners[1].Z = min.Z;
        
        corners[2].X = min.X;
        corners[2].Y = max.Y;
        corners[2].Z = min.Z;
        
        corners[3].X = max.X;
        corners[3].Y = max.Y;
        corners[3].Z = min.Z;
        
        corners[4].X = min.X;
        corners[4].Y = min.Y;
        corners[4].Z = max.Z;
        
        corners[5].X = max.X;
        corners[5].Y = min.Y;
        corners[5].Z = max.Z;
        
        corners[6].X = min.X;
        corners[6].Y = max.Y;
        corners[6].Z = max.Z;
        
        corners[7].X = max.X;
        corners[7].Y = max.Y;
        corners[7].Z = max.Z;

        // Check each frustum plane
        for (var i = 0; i < 6; i++)
        {
            var insideCount = 0;

            for (var j = 0; j < 8; j++)
            {
                // Check the distance from the point to the plane
                var distance = Vector4.Dot(frustumPlanes[i], new Vector4(corners[j], 1.0f));

                // If the distance is positive, the point is in front of the plane
                if (distance >= 0.0f)
                {
                    insideCount++;
                    break; // Early exit
                }
            }

            // If no points are in front of the plane, the AABB is outside the frustum
            if (insideCount == 0)
            {
                return false;
            }
        }

        // If the AABB is not outside any frustum plane, it is inside the frustum
        return true;
    }

    public static ContainmentType GetAabbFrustumContainment(in AABB aabb, in Vector4[] frustumPlanes)
    {
        var min = aabb.min;
        var max = aabb.max;

        // Calculate the eight corners of the AABB
        corners[0].X = min.X;
        corners[0].Y = min.Y;
        corners[0].Z = min.Z;

        corners[1].X = max.X;
        corners[1].Y = min.Y;
        corners[1].Z = min.Z;

        corners[2].X = min.X;
        corners[2].Y = max.Y;
        corners[2].Z = min.Z;

        corners[3].X = max.X;
        corners[3].Y = max.Y;
        corners[3].Z = min.Z;

        corners[4].X = min.X;
        corners[4].Y = min.Y;
        corners[4].Z = max.Z;

        corners[5].X = max.X;
        corners[5].Y = min.Y;
        corners[5].Z = max.Z;

        corners[6].X = min.X;
        corners[6].Y = max.Y;
        corners[6].Z = max.Z;

        corners[7].X = max.X;
        corners[7].Y = max.Y;
        corners[7].Z = max.Z;

        var totalInsideCount = 0;

        // Check each frustum plane
        for (var i = 0; i < 6; i++)
        {
            var insideCount = 0;

            for (var j = 0; j < 8; j++)
            {
                // Check the distance from the point to the plane
                var distance = Vector4.Dot(frustumPlanes[i], new Vector4(corners[j], 1.0f));

                // If the distance is positive, the point is in front of the plane
                if (distance >= 0.0f)
                {
                    insideCount++;
                }
            }

            // If all points are in front of the plane, the AABB is inside the frustum
            if (insideCount == 8)
            {
                totalInsideCount++;
            }
            else if (insideCount > 0)
            {
                return ContainmentType.Intersects;
            }
        }

        // If the AABB is inside all frustum planes, it is contained by the frustum
        return totalInsideCount == 6 ? ContainmentType.Contains : ContainmentType.Disjoint;
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

    public static AABB CalculateAabb(int strideInFloats, float[] vertices, Matrix4 transformationMatrix)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (var i = 0; i < vertices.Length;)
        {
            var vertex = new Vector3(vertices[i], vertices[i + 1], vertices[i + 2]);
            var transformedVertex = Vector3.TransformPosition(vertex, transformationMatrix);

            // Update min and max values for each axis
            min.X = Math.Min(min.X, transformedVertex.X);
            min.Y = Math.Min(min.Y, transformedVertex.Y);
            min.Z = Math.Min(min.Z, transformedVertex.Z);

            max.X = Math.Max(max.X, transformedVertex.X);
            max.Y = Math.Max(max.Y, transformedVertex.Y);
            max.Z = Math.Max(max.Z, transformedVertex.Z);

            i += strideInFloats;
        }

        return (min, max);
    }
}

public enum ContainmentType
{
    /// <summary>
    /// The two bounding volumes don't intersect at all.
    /// </summary>
    Disjoint,

    /// <summary>
    /// One bounding volume completely contains the other.
    /// </summary>
    Contains,

    /// <summary>
    /// The two bounding volumes overlap.
    /// </summary>
    Intersects
}

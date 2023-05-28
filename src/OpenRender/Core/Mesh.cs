using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenTK.Mathematics;

namespace OpenRender.Core;

/// <summary>
/// Mesh is just a container for a vertex buffer, draw mode and material.
/// </summary>
public struct Mesh
{
    private readonly DrawMode drawMode;

    public Mesh(VertexBuffer vertexBuffer, Material material)
        : this(vertexBuffer,
               vertexBuffer.Indices == null ? DrawMode.Primitive : DrawMode.Indexed,
               material) { }

    public Mesh(VertexBuffer vertexBuffer, DrawMode drawMode, Material material)
    {
        VertexBuffer = vertexBuffer;
        Material = material;
        this.drawMode = drawMode;
    }

    public readonly DrawMode DrawMode => drawMode;
    public Material Material;
    public readonly VertexBuffer VertexBuffer;

    public readonly BoundingSphere CalculateBoundingSphere()
    {
        if(VertexBuffer == null) return new BoundingSphere(Vector3.Zero, 0);
        var positionOffset = (int)AttributeLocation.Position;
        List<Vector3> positions = new();
        for (var i = 0; i < VertexBuffer.Vertices.Length; i += VertexBuffer.Stride)
        {
            var x = VertexBuffer.Vertices[i + positionOffset];
            var y = VertexBuffer.Vertices[i + positionOffset + 1];
            var z = VertexBuffer.Vertices[i + positionOffset + 2];
            positions.Add(new Vector3(x, y, z));
        }

        // Calculate the center of the bounding sphere
        var center = CalculateBoundingSphereCenter(positions);

        // Calculate the radius of the bounding sphere
        var radius = CalculateBoundingSphereRadius(center, positions);
        return new BoundingSphere(center, radius) { Center = center, Radius = radius };
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

    private static float CalculateBoundingSphereRadius(Vector3 center, IEnumerable<Vector3> positions)
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

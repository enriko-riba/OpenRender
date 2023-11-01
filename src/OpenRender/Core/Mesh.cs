using OpenRender.Core.Buffers;
using OpenRender.Core.Culling;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;

namespace OpenRender.Core;

/// <summary>
/// Mesh is just a container for a geometry data.
/// </summary>
public class Mesh
{
    private readonly float[] vertices;
    private readonly uint[] indices;

    public VertexDeclaration VertexDeclaration { get; private set; }

    public Mesh(VertexDeclaration vertexDeclaration, Vertex[] vertices, uint[] indices)
    {
        VertexDeclaration = vertexDeclaration;
        this.indices = indices;
        this.vertices = GetVertices(vertexDeclaration, vertices);
        BoundingSphere = CullingHelper.CalculateBoundingSphere(VertexDeclaration.StrideInFloats, this.vertices);
    }

    public Mesh(VertexDeclaration vertexDeclaration, Vertex2D[] vertices, uint[] indices)
    {       
        VertexDeclaration = vertexDeclaration;
        this.indices = indices;
        this.vertices = GetVertices(vertexDeclaration, vertices);
        BoundingSphere = CullingHelper.CalculateBoundingSphere(VertexDeclaration.StrideInFloats, this.vertices);
    }

    public Mesh(VertexDeclaration vertexDeclaration, float[] vertices, uint[] indices)
    {
        VertexDeclaration = vertexDeclaration;
        this.vertices = vertices;
        this.indices = indices;
        BoundingSphere = CullingHelper.CalculateBoundingSphere(VertexDeclaration.StrideInFloats, vertices);
    }
       
    public BoundingSphere BoundingSphere;

    public uint[] Indices => indices;
    public float[] Vertices => vertices;
   
    public VertexArrayObject BuildVao()
    {
        if (vertices != null)
        {
            var vao = new VertexArrayObject();
            vao.AddBuffer(VertexDeclaration, new Buffer<float>(vertices));
            vao.AddIndexBuffer(new IndexBuffer(indices));
            return vao;
        }
        throw new ArgumentNullException(nameof(vertices));
    }

    public static float[] GetVertices<T>(VertexDeclaration vertexDeclaration, T[] vertices) where T: IVertexData
    {
        var length = vertices.Length * vertexDeclaration.StrideInFloats;
        var floats = new float[length];
        var offset = 0;
        foreach (var vertex in vertices)
        {
            var destination = floats.AsSpan(offset);
            vertex.Data.CopyTo(destination);
            offset += vertexDeclaration.StrideInFloats;
        }
        return floats;
    }
}

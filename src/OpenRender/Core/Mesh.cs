using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;

namespace OpenRender.Core;

/// <summary>
/// Mesh is just a container for a vertex buffer and draw mode.
/// </summary>
public class Mesh
{
    protected readonly float[] vertices;
    private readonly uint[] indices;

    public VertexDeclaration VertexDeclaration { get; private set; }

    public Mesh(VertexDeclaration vertexDeclaration, Vertex[] vertices, uint[] indices)
    {
        VertexDeclaration = vertexDeclaration;
        this.indices = indices;
        this.vertices = GetVertices(vertexDeclaration, vertices);
    }


    public Mesh(VertexDeclaration vertexDeclaration, Vertex2D[] vertices, uint[] indices)
    {       
        VertexDeclaration = vertexDeclaration;
        this.indices = indices;
        this.vertices = GetVertices(vertexDeclaration, vertices);
    }

    public Mesh(VertexDeclaration vertexDeclaration, float[] vertices, uint[] indices)
    {
        VertexDeclaration = vertexDeclaration;
        this.vertices = vertices;
        this.indices = indices;

        Vao = new VertexArrayObject();
        Vao.AddBuffer(vertexDeclaration, new Buffer<float>(vertices));
        Vao.AddIndexBuffer(new IndexBuffer(indices));
    }
       
    public VertexArrayObject? Vao;
    public BoundingSphere BoundingSphere = new();
    public uint[] Indices => indices;
    public float[] Vertices => vertices;

    /// <summary>
    /// Creates the VAO and buffer objects.
    /// </summary>
    public void Build()
    {
        if (vertices != null)
        {
            Vao = new VertexArrayObject();
            Vao.AddBuffer(VertexDeclaration, new Buffer<float>(vertices));
            Vao.AddIndexBuffer(new IndexBuffer(indices));
        }
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

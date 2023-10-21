using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using System.Runtime.CompilerServices;

namespace OpenRender.Core;

/// <summary>
/// Mesh is just a container for a vertex buffer and draw mode.
/// </summary>
public struct Mesh
{
    private IVertex[] vertices;
    private uint[] indices;

    
    public VertexDeclaration VertexDeclaration { get; private set; }

    public Mesh(VertexDeclaration vertexDeclaration, Vertex[] vertices, uint[] indices)
    {
        Vao = new VertexArrayObject();
        Vao.AddVertexBuffer(vertexDeclaration, new VertexBuffer(vertices));
        Vao.AddIndexBuffer(new IndexBuffer(indices));
        VertexDeclaration = vertexDeclaration;
        this.vertices = Unsafe.As<IVertex[]>(vertices);
        this.indices = indices;
    }

    public Mesh(VertexDeclaration vertexDeclaration, Vertex2D[] vertices, uint[] indices)
    {
        Vao = new VertexArrayObject();
        Vao.AddBuffer(vertexDeclaration, new Buffer<Vertex2D>(vertices));
        Vao.AddIndexBuffer(new IndexBuffer(indices));
        VertexDeclaration = vertexDeclaration;
        this.vertices = Unsafe.As<IVertex[]>(vertices);
        this.indices = indices;
    }

    public Mesh(VertexDeclaration vertexDeclaration, IVertex[] vertices, uint[] indices)
    {
        VertexDeclaration = vertexDeclaration;
        this.vertices = vertices;
        this.indices = indices;

        var length = vertices.Length * vertexDeclaration.StrideInFloats;
        var floats = new float[length];
        var offset = 0;
        foreach (var vertex in vertices)
        {
            var destination = floats.AsSpan(offset);
            vertex.Data.CopyTo(destination);
            offset += vertexDeclaration.StrideInFloats;
        }

        Vao = new VertexArrayObject();
        Vao.AddBuffer(vertexDeclaration, new Buffer<float>(floats));
        Vao.AddIndexBuffer(new IndexBuffer(indices));
    }

    public readonly void GetGeometry(out IVertex[] vertices, out uint[] indices)
    {
        vertices = this.vertices;
        indices = this.indices;
    }

    public readonly VertexArrayObject Vao;
    public BoundingSphere BoundingSphere = new();
    public readonly uint[] Indices => indices;
    public readonly IVertex[] Vertices => vertices;
    //  TODO: this struct makes only sense if multiple sub-meshes will be supported.
}

using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

public class VertexBuffer
{
    private readonly int vao;
    private readonly int vbo;
    private readonly int ibo;
    private readonly VertexDeclaration vertexDeclaration;
    private float[] vertices;

    public VertexBuffer(VertexDeclaration vertexDeclaration, float[] vertices) : this(vertexDeclaration, vertices, null) { }

    public VertexBuffer(VertexDeclaration vertexDeclaration, float[] vertices, uint[]? indices)
    {
        this.vertexDeclaration = vertexDeclaration;
        this.vertices = vertices;
        Indices = indices;

        GL.CreateVertexArrays(1, out vao);
        if (indices != null)
        {
            GL.CreateBuffers(1, out ibo);
            GL.VertexArrayElementBuffer(vao, ibo);
            GL.NamedBufferData(ibo, indices.Length * sizeof(uint), indices, BufferUsageHint.DynamicDraw);
        }

        GL.CreateBuffers(1, out vbo);
        vertexDeclaration.Apply(this);
        GL.VertexArrayVertexBuffer(vao, 0, vbo, 0, vertexDeclaration.Stride);
        GL.NamedBufferData(vbo, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
    }

    /// <summary>
    /// Adds GL labels for debugging.
    /// </summary>
    /// <param name="name"></param>
    public void SetName(string name)
    {
        GL.ObjectLabel(ObjectLabelIdentifier.VertexArray, vao, -1, $"VAO {name}");
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, vbo, -1, $"VB {name}");
        if (Indices != null)
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, ibo, -1, $"IB {name}");
    }

    public float[] Vertices
    {
        get => vertices;
        set
        {
            vertices = value;
            GL.NamedBufferSubData(vbo, 0, vertices.Length * sizeof(float), vertices);   //  TODO: test if updating works
        }
    }

    public uint[]? Indices { get; init; }
    public int Vao => vao;
    public int Stride => vertexDeclaration.Stride;
    public VertexDeclaration VertexDeclaration => vertexDeclaration;
}

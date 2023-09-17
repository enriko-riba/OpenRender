using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

public class VertexBuffer
{
    private readonly int vao;
    private readonly int vbo;
    private readonly int ibo;
    private readonly int stride;
    private float[] vertices;

    public VertexBuffer(VertexDeclaration vertexDeclaration, float[] vertices, uint[]? indices)
    {
        this.vertices = vertices;
        Indices = indices;

        GL.CreateVertexArrays(1, out vao);
        if (indices != null)
        {
            GL.CreateBuffers(1, out ibo);
            GL.NamedBufferStorage(ibo, indices.Length * sizeof(uint), indices, BufferStorageFlags.DynamicStorageBit);
            GL.VertexArrayElementBuffer(vao, ibo);
        }

        GL.CreateBuffers(1, out vbo);
        vertexDeclaration.Apply(this);
        GL.VertexArrayVertexBuffer(vao, 0, vbo, 0, vertexDeclaration.Stride);
        GL.NamedBufferStorage(vbo, vertices.Length * sizeof(float), vertices, BufferStorageFlags.DynamicStorageBit);
    }

    public float[] Vertices
    {
        get => vertices;
        set
        {
            vertices = value;
            GL.NamedBufferStorage(vbo, vertices.Length * sizeof(float), vertices, BufferStorageFlags.DynamicStorageBit);
        }
    }

    public uint[]? Indices { get; init; }
    public int Vao => vao;
    public int Stride => stride;
}

using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

public class VertexBuffer
{
    private readonly int vao;
    private readonly int vbo;
    private readonly int ebo;
    private readonly int stride;
    private float[] vertices;

    public VertexBuffer(VertexDeclaration vertexDeclaration, float[] vertices, uint[]? indices)
    {
        this.vertices = vertices;
        Indices = indices;

        GL.CreateVertexArrays(1, out vao);
        if (indices != null)
        {
            GL.CreateBuffers(1, out ebo);
            GL.NamedBufferStorage(ebo, indices.Length * sizeof(uint), indices, BufferStorageFlags.DynamicStorageBit);
            GL.VertexArrayElementBuffer(vao, ebo);
        }

        GL.CreateBuffers(1, out vbo);
        stride = vertexDeclaration.Invoke(vao);
        GL.VertexArrayVertexBuffer(vao, 0, vbo, 0, stride);
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

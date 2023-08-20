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

        vao = GL.GenVertexArray();
        GL.BindVertexArray(vao);

        if (indices != null)
        {
            ebo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices!.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);
        }

        vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

        stride = vertexDeclaration.Invoke();
        GL.BindVertexArray(0);

        //  TODO: refactor to use DSA
        //GL.CreateBuffers(1, out vbo);
        //GL.NamedBufferStorage(vbo, vertices.Length * sizeof(float), vertices, BufferStorageFlags.DynamicStorageBit);
        
        //GL.CreateVertexArrays(1, out vao);
        //stride = vertexDeclaration.Invoke(vao);
        //GL.VertexArrayVertexBuffer(vao, 0, vbo, 0, stride);

        //if (indices != null)
        //{
        //    GL.CreateBuffers(1, out ebo);
        //    GL.NamedBufferStorage(ebo, indices.Length * sizeof(uint), indices, BufferStorageFlags.DynamicStorageBit);
        //    GL.VertexArrayElementBuffer(vao, ebo);
        //}

    }

    public float[] Vertices 
    { 
        get => vertices;
        set
        {
           vertices = value;
           GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
           GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
        }
    }

    public uint[]? Indices { get; init; }
    public int Vao => vao;
    public int Stride => stride;
}

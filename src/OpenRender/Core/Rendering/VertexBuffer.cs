using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

public class VertexBuffer
{
    private readonly int vao;
    private readonly int vbo;
    private readonly int ebo;
   
    public VertexBuffer(VertexDeclaration vertexDeclaration, float[] vertices, uint[]? indices)
    {
        Vertices = vertices;
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

        //GL.BindVertexArray(vao);
        vertexDeclaration.Invoke();
        GL.BindVertexArray(0);
    }

    public float[] Vertices { get; init; }
    public uint[]? Indices { get; init; }
    public int Vao => vao;
}

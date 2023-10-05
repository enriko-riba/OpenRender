using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

public class Buffer<T> : IDisposable where T : unmanaged
{
    private readonly uint vbo;
    private readonly VertexDeclaration? vertexDeclaration;
    private readonly T[] data;

    public unsafe Buffer(T[] data, VertexDeclaration? vertexDeclaration = null, BufferUsageHint usageHint = BufferUsageHint.StaticDraw)
    {
        this.vertexDeclaration = vertexDeclaration;
        this.data = data;
        GL.CreateBuffers(1, out vbo);
        fixed (void* d = data)
        {
            GL.NamedBufferData(vbo, data.Length * sizeof(T), (nint)d, usageHint);
        }
    }

    public uint Vbo => vbo;

    public VertexDeclaration? VertexDeclaration => vertexDeclaration;

    public T[] Data => data;

    public void SetLabel(string name) => GL.ObjectLabel(ObjectLabelIdentifier.Buffer, vbo, -1, $"Buffer {name}");

    public void Dispose()
    {
        GL.DeleteBuffer(vbo);
        GC.SuppressFinalize(this);
    }
}

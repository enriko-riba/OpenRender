using OpenTK.Graphics.OpenGL4;
using System.Runtime.CompilerServices;

namespace OpenRender.Core.Rendering;

public class Buffer<T> : IDisposable where T : unmanaged
{
    private readonly uint vbo;
    private readonly VertexDeclaration? vertexDeclaration;
    private T[] data;

    public Buffer(T[] data, VertexDeclaration? vertexDeclaration = null)
    {
        this.vertexDeclaration = vertexDeclaration;
        this.data = data;
        GL.CreateBuffers(1, out vbo);
        GL.NamedBufferStorage(vbo, data.Length * Unsafe.SizeOf<T>(), data, BufferStorageFlags.MapWriteBit | BufferStorageFlags.MapPersistentBit | BufferStorageFlags.MapCoherentBit);
    }

    public uint Vbo => vbo;

    public VertexDeclaration? VertexDeclaration => vertexDeclaration;

    public ref T[] Data => ref data;

    public void SetLabel(string name) => GL.ObjectLabel(ObjectLabelIdentifier.Buffer, vbo, -1, name);

    public void Dispose()
    {
        GL.DeleteBuffer(vbo);
        GC.SuppressFinalize(this);
    }
}

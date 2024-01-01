using OpenTK.Graphics.OpenGL4;
using System.Runtime.CompilerServices;

namespace OpenRender.Core.Buffers;

/// <summary>
/// VBO wrapper that allows for easy data upload and retrieval.
/// </summary>
/// <typeparam name="T"></typeparam>
public class Buffer<T> : IDisposable where T : struct
{
    private readonly uint vbo;
    private T[] data;

    /// <summary>
    /// Creates a new VBO and immediately uploads the data to the GPU.
    /// </summary>
    /// <param name="data"></param>
    /// <param name="flags"></param>
    public Buffer(T[] data, BufferStorageFlags flags)
    {
        this.data = data;
        GL.CreateBuffers(1, out vbo);
        if(data.Length > 0)
            GL.NamedBufferStorage(vbo, data.Length * Unsafe.SizeOf<T>(), data, flags);
    }

    /// <summary>
    /// Creates a new VBO and immediately uploads the data to the GPU.
    /// </summary>
    /// <param name="data"></param>
    public Buffer(T[] data) : this(data, BufferStorageFlags.MapWriteBit | BufferStorageFlags.MapPersistentBit | BufferStorageFlags.MapCoherentBit) { }

    public uint Vbo => vbo;

    public ref T[] Data => ref data;

    public void SetLabel(string name) => GL.ObjectLabel(ObjectLabelIdentifier.Buffer, vbo, -1, name);

    public void Dispose()
    {
        GL.DeleteBuffer(vbo);
        GC.SuppressFinalize(this);
    }
}

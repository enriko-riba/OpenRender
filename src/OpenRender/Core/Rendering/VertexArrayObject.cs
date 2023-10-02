using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

public class VertexArrayObject
{
    private readonly uint vao;
    private uint lastBindingPoint = 0;
    private uint ibo = 0;
    private int dataLength;

    public VertexArrayObject()
    {
        GL.CreateVertexArrays(1, out vao);
    }

    public unsafe void AddBuffer<T>(VertexDeclaration vertexDeclaration, T[] data, string? name = null) where T : unmanaged
    {
        var buffer = new Buffer<T>(data, vertexDeclaration);
        AddBuffer(buffer, name);
    }

    public void AddBuffer<T>(Buffer<T> buffer, string? name = null) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(buffer.VertexDeclaration, nameof(buffer.VertexDeclaration));

        foreach (var attribute in buffer.VertexDeclaration.Attributes)
        {
            GL.EnableVertexArrayAttrib(vao, attribute.Location);
            GL.VertexArrayAttribFormat(vao, attribute.Location, attribute.Size, attribute.Type, attribute.Normalized, attribute.Offset);
            GL.VertexArrayAttribBinding(vao, attribute.Location, lastBindingPoint);
            GL.VertexArrayBindingDivisor(vao, attribute.Location, attribute.Divisor);
        }
        GL.VertexArrayVertexBuffer(vao, lastBindingPoint, buffer.Vbo, 0, buffer.VertexDeclaration.Stride);

        if (DataLength == 0) dataLength = buffer.DataLength;
        if (!string.IsNullOrEmpty(name)) buffer.SetLabel(name);
        lastBindingPoint++;
    }

    public void AddIndexBuffer(IndexBuffer buffer, string? name = null)
    {
        GL.VertexArrayElementBuffer(vao, buffer.Vbo);
        ibo = buffer.Vbo;
        dataLength = buffer.DataLength;
        if (!string.IsNullOrEmpty(name)) buffer.SetLabel(name);
    }


    //public void AddBuffer(VertexDeclaration vertexDeclaration, float[] data, string? name = null)
    //{
    //    name ??= $"Buffer {lastBindingPoint}";
    //    var vb = new VertexBuffer(vertexDeclaration, data);
    //    buffers.Add(vb);
    //    vertexDeclaration.Apply(vao, lastBindingPoint++);
    //}

    //public void AddIndices(uint[] indices, string? name = null)
    //{
    //    if (indices.Length < 3)
    //        throw new ArgumentException("Indices array must contain at least 3 elements!", nameof(indices));

    //    if (ibo > 0)
    //        throw new InvalidOperationException("Indices buffer already set!");

    //    GL.CreateBuffers(1, out ibo);
    //    GL.VertexArrayElementBuffer(vao, ibo);
    //    GL.NamedBufferData(ibo, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);
    //    if (name != null)
    //        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, ibo, -1, $"IBO {name}");
    //}

    public DrawMode DrawMode => ibo == 0 ? DrawMode.Primitive : DrawMode.Indexed;

    public int DataLength => dataLength;
    //public uint Handle => vao;

    //public int DataLength => buffers.Count > 0 ? buffers[0].Length : 0;

    public static implicit operator uint(VertexArrayObject vao) => vao.vao;  //  allow using the vertex array instead of the handle
}

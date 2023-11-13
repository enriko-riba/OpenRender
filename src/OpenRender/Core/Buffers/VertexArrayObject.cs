using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Buffers;

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

    public unsafe void AddBuffer<T>(VertexDeclaration vertexDeclaration, T[] data, string? name = null) where T : struct
    {
        var buffer = new Buffer<T>(data);
        AddBuffer(vertexDeclaration, buffer, name: name);
    }

    public void AddBuffer<T>(VertexDeclaration vertexDeclaration, Buffer<T> buffer, string? name = null) where T : struct
    {
        foreach (var attribute in vertexDeclaration.Attributes)
        {
            GL.EnableVertexArrayAttrib(vao, attribute.Location);
            GL.VertexArrayAttribFormat(vao, attribute.Location, attribute.Size, attribute.Type, attribute.Normalized, attribute.Offset);
            GL.VertexArrayAttribBinding(vao, attribute.Location, lastBindingPoint);
            GL.VertexArrayBindingDivisor(vao, attribute.Location, attribute.Divisor);
        }
        GL.VertexArrayVertexBuffer(vao, lastBindingPoint, buffer.Vbo, 0, vertexDeclaration.Stride);
        Log.CheckGlError();

        if (DataLength == 0) dataLength = buffer.Data.Length;
        if (!string.IsNullOrEmpty(name)) buffer.SetLabel(name);
        lastBindingPoint++;

        //  this is a hack to get the vertex buffer with positions for the mesh
        if (buffer is Buffer<float> && VertexBuffer is null)
        {
            VertexBuffer = buffer as Buffer<float>;
            if (string.IsNullOrEmpty(name)) VertexBuffer?.SetLabel("VBO");
        }
    }

    public void AddIndexBuffer(IndexBuffer buffer, string? name = "IBO")
    {
        GL.VertexArrayElementBuffer(vao, buffer.Vbo);
        ibo = buffer.Vbo;
        IndexBuffer = buffer;
        dataLength = buffer.Data.Length;
        if (!string.IsNullOrEmpty(name)) buffer.SetLabel(name);
    }

    public DrawMode DrawMode => ibo == 0 ? DrawMode.Primitive : DrawMode.Indexed;

    public int DataLength => dataLength;

    public Buffer<float>? VertexBuffer { get; set; }

    public Buffer<uint>? IndexBuffer { get; set; }

    public static implicit operator uint(VertexArrayObject vao) => vao.vao;  //  allow using the vertex array instead of the handle
}

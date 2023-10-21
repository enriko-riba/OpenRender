using OpenTK.Graphics.OpenGL4;
using static OpenTK.Graphics.OpenGL.GL;

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
        var buffer = new Buffer<T>(data);
        AddBuffer(vertexDeclaration, buffer, name: name);
    }

    public void AddBuffer<T>(VertexDeclaration vertexDeclaration, Buffer<T> buffer, uint? bindingDivisor = 0, string? name = null) where T : unmanaged
    {
        foreach (var attribute in vertexDeclaration.Attributes)
        {
            GL.EnableVertexArrayAttrib(vao, attribute.Location);
            GL.VertexArrayAttribFormat(vao, attribute.Location, attribute.Size, attribute.Type, attribute.Normalized, attribute.Offset);
            GL.VertexArrayAttribBinding(vao, attribute.Location, lastBindingPoint);
            GL.VertexArrayBindingDivisor(vao, attribute.Location, attribute.Divisor);
        }
        GL.VertexArrayVertexBuffer(vao, lastBindingPoint, buffer.Vbo, 0, vertexDeclaration.Stride);
        GL.VertexArrayBindingDivisor(vao, lastBindingPoint, bindingDivisor ?? 0);
        Log.CheckGlError();

        if (DataLength == 0) dataLength = buffer.Data.Length;
        if (!string.IsNullOrEmpty(name)) buffer.SetLabel(name);
        lastBindingPoint++;
        
        //  this is a hack to get the vertex buffer with positions for the mesh
        if (buffer is VertexBuffer && VertexBuffer is null) VertexBuffer = buffer as VertexBuffer;
    }

    public void AddVertexBuffer(VertexDeclaration vertexDeclaration, VertexBuffer buffer)
    {
        AddBuffer(vertexDeclaration, buffer, 0);
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

    public VertexBuffer? VertexBuffer { get; set; }

    public Buffer<uint>? IndexBuffer { get; set; }

    public static implicit operator uint(VertexArrayObject vao) => vao.vao;  //  allow using the vertex array instead of the handle
}

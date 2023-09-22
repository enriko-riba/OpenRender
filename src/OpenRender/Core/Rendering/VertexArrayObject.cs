using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

public class VertexArrayObject
{
    private readonly int vao;
    private readonly List<IVertexBuffer> buffers = new();
    private int lastBindingPoint = 0;
    private int ibo = -1;

    public VertexArrayObject()
    {
        GL.CreateVertexArrays(1, out vao);
    }

    public void AddBuffer(VertexDeclaration vertexDeclaration, float[] data, string? name = null)
    {
        name ??= $"Buffer {lastBindingPoint}";
        var vb = new VertexBuffer(vertexDeclaration, data);
        buffers.Add(vb);
        vertexDeclaration.Apply(vao, lastBindingPoint++);
    }

    public void AddIndices(uint[] indices, string? name = null)
    {
        if(indices.Length < 3)
            throw new ArgumentException("Indices array must contain at least 3 elements!", nameof(indices));

        if (ibo != -1)
            throw new InvalidOperationException("Indices buffer already set!");

        GL.CreateBuffers(1, out ibo);
        GL.VertexArrayElementBuffer(vao, ibo);
        GL.NamedBufferData(ibo, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);
        if(name != null)
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, ibo, -1, $"IBO {name}");
    }

    public DrawMode DrawMode => ibo == -1 ? DrawMode.Primitive : DrawMode.Indexed;

    public int Handle => vao;

    public int DataLength => buffers.Count > 0 ? buffers[0].Length : 0;

    public static implicit operator int(VertexArrayObject vao) => vao.vao;  //  allow using the vertex array instead of the handle
}

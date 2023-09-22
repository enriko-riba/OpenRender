using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

public record VertexBufferResource(int Vbo, int BindingIndex, VertexDeclaration VertexDeclaration, int DataLength);

public class VertexArrayObject
{
    private readonly int vao;
    private readonly List<IVertexBuffer> buffers = new();
    private readonly List<VertexBufferResource> vboList = new();
    private int ibo = -1;
    private uint[]? indices;
    private int lastBindingPoint = 0;

    public VertexArrayObject()
    {
        GL.CreateVertexArrays(1, out vao);
        Log.CheckGlError("Create VAO");
    }

    public void AddBuffer(VertexDeclaration vertexDeclaration, float[] data, string? name = null)
    {                
        GL.CreateBuffers(1, out int vbo);
        name ??= $"VBO {lastBindingPoint}";
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, vbo, -1, name);

        vertexDeclaration.Apply(vao, lastBindingPoint);
        GL.VertexArrayVertexBuffer(vao, lastBindingPoint, vbo, 0, vertexDeclaration.Stride);
        GL.NamedBufferData(vbo, data.Length * sizeof(float), data, BufferUsageHint.DynamicDraw);
        Log.CheckGlError();

        var vb = new VertexBufferResource(vbo, lastBindingPoint, vertexDeclaration, data.Length);
        vboList.Add(vb);
        lastBindingPoint++;
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
        
        Log.CheckGlError();

        this.indices = indices;
    }

    public DrawMode DrawMode => ibo == -1 ? DrawMode.Primitive : DrawMode.Indexed;

    public int Handle => vao;

    public int DataLength => DrawMode == DrawMode.Primitive ?
                                vboList.Count > 0 ? vboList[0].DataLength : 0
                                : indices?.Length??0;

    public static implicit operator int(VertexArrayObject vao) => vao.vao;  //  allow using the vertex array instead of the handle
}

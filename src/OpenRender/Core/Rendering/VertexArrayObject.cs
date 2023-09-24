using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

/// <summary>
/// Holds vertex array buffer declaration and data.
/// </summary>
/// <param name="VertexDeclaration"></param>
/// <param name="Data"></param>
/// <param name="Indices"></param>
/// <param name="Name"></param>
/// <summary>
public class VertexBuffer
{
    private string? name;
    private int ibo;

    public VertexBuffer(VertexDeclaration VertexDeclaration, float[] Data, uint[]? Indices = null, string? Name = null)
    {
        name = Name ?? $"VBO";
        this.VertexDeclaration = VertexDeclaration;
        this.Data = Data;
        this.Indices = Indices;

        //  create VBO
        GL.CreateBuffers(1, out int vbo);
        GL.NamedBufferData(vbo, Data.Length * sizeof(float), Data, BufferUsageHint.DynamicDraw);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, vbo, -1, Name);
        Log.CheckGlError();
        Vbo = vbo;

        if(Indices != null)
        {
            var name = $"IBO {Name}".Trim();
            CreateIbo(name);
        }
    }

    public int BindingIndex { get; internal set; }

    public VertexDeclaration VertexDeclaration { get; private set; }
    public int Vbo { get; private set; }
    public float[] Data { get; private set; }
    public int Ibo => ibo;
    public uint[]? Indices { get; private set; }
    public string? Name
    {
        get => name;
        set
        {
            name = value;
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, Vbo, -1, Name);
        }
    }

    private void CreateIbo(string name)
    {
        if (Indices!.Length < 3)
            throw new ArgumentException("Indices array must contain at least 3 elements!");

        if (Ibo > 0)
            throw new InvalidOperationException("Indices buffer already set!");

        GL.CreateBuffers(1, out ibo);        
        GL.NamedBufferData(ibo, Indices.Length * sizeof(uint), Indices, BufferUsageHint.StaticDraw);
        name ??= $"IBO";
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, ibo, -1, $"IBO {name}");

        Log.CheckGlError();
    }
}

/// <summary>
/// Vertex array object with buffers.
/// </summary>
public struct VertexArrayObject
{
    private readonly int vao;
    private readonly List<VertexBuffer> vboList = new();

    private int nextBindingIndex = 0;
    private VertexBuffer? firstVBO;

    public VertexArrayObject()
    {
        GL.CreateVertexArrays(1, out vao);
        Log.CheckGlError("Create VAO");
    }

    public VertexArrayObject(VertexBuffer vertexBuffer) : this()
    {
        AddBuffer(vertexBuffer);
    }

    /// <summary>
    /// Returns the last (max) binding index used to bind buffers to the VAO.
    /// </summary>
    public readonly int LastBindingIndex => nextBindingIndex - 1;

    /// <summary>
    /// Gets the <see cref="VertexBuffer"/> containing the vertex positions"/>
    /// </summary>
    public readonly VertexBuffer? VertexArrayBuffer => firstVBO;

    /// <summary>
    /// Creates a new VBO from the supplied declaration and binds it to the VAO.
    /// Note: by convention the first buffer added should contain vertex positions.
    /// </summary>
    /// <param name="vertexBuffer"></param>
    public void AddBuffer(VertexBuffer vertexBuffer)
    {
        if (firstVBO == null)
        {
            firstVBO = vertexBuffer;
            if (vertexBuffer.Indices != null)
            {
                GL.VertexArrayElementBuffer(vao, vertexBuffer.Ibo);
                Log.CheckGlError("Bind IBO to VAO");
            }
        }

        vertexBuffer.Name ??= $"VBO {nextBindingIndex}";        
        vertexBuffer.BindingIndex = nextBindingIndex;

        //  bind VBO to VAO
        GL.VertexArrayVertexBuffer(vao, nextBindingIndex, vertexBuffer.Vbo, 0, vertexBuffer.VertexDeclaration.Stride);

        //  set up vertex attributes
        foreach (var attribute in vertexBuffer.VertexDeclaration.Attributes)
        {
            GL.EnableVertexArrayAttrib(vao, attribute.Location);
            GL.VertexArrayAttribFormat(vao, attribute.Location, attribute.Size, attribute.Type, attribute.Normalized, attribute.Offset);
            GL.VertexArrayAttribBinding(vao, attribute.Location, nextBindingIndex);
            GL.VertexArrayBindingDivisor(vao, attribute.Location, attribute.Divisor);
        }
        Log.CheckGlError();
        vboList.Add(vertexBuffer);
        nextBindingIndex++;
    }

    /// <summary>
    /// Creates a new VBO from the supplied declaration and binds it to the VAO.
    /// Note: by convention the first buffer added should contain vertex positions.
    /// </summary>
    /// <param name="vertexDeclaration"></param>
    /// <param name="data"></param>
    /// <param name="indices"></param>
    /// <param name="name"></param>
    public void AddBuffer(VertexDeclaration vertexDeclaration, float[] data, uint[]? indices = null, string? name = null)
    {
        var vb = new VertexBuffer(vertexDeclaration, data, indices, name);
        AddBuffer(vb);
    }

    public readonly uint[]? Indices => firstVBO?.Indices;

    public readonly DrawMode DrawMode => firstVBO?.Ibo > 0 ? DrawMode.Indexed: DrawMode.Primitive;

    public readonly int Handle => vao;

    public readonly int DataLength => DrawMode == DrawMode.Primitive ?
                                firstVBO?.Data?.Length ?? 0 :
                                firstVBO?.Indices?.Length ?? 0;

    public override string ToString() => $"{vao}";

    public static implicit operator int(VertexArrayObject vao) => vao.vao;  //  allow using the instance instead of the VAO name
}

using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

/// <summary>
/// Well known attribute locations.
/// </summary>
public enum AttributeLocation
{
    Position,
    Normal,
    Color,
    TextureCoord,
}

/// <summary>
/// Holds information about a vertex attribute layout.
/// </summary>
public struct AttributeLayout
{
    public AttributeLayout(AttributeLocation location, int size, VertexAttribType type) :
        this(location.ToString(), (int)location, size, type, -1, 0)
    { }
    public AttributeLayout(AttributeLocation location, int size, VertexAttribType type, int divisor) :
        this(location.ToString(), (int)location, size, type, -1, divisor)
    { }

    public AttributeLayout(string debugName, AttributeLocation location, int size, VertexAttribType type, int offset, int divisor) :
        this(debugName, (int)location, size, type, offset, divisor)
    { }

    public AttributeLayout(string debugName, int location, int size, VertexAttribType type, int divisor) :
        this(debugName, location, size, type, -1, divisor)
    { }

    public AttributeLayout(string debugName, int location, int size, VertexAttribType type, int offset, int divisor)
    {
        DebugName = debugName;
        Location = location;
        Size = size;
        Type = type;
        Offset = offset;
        Divisor = divisor;
    }

    public string DebugName;
    public int Location;
    public int Size;
    public VertexAttribType Type;
    public int Offset;
    public int Divisor;
    public bool Normalized;

    public override readonly string ToString() => $"{DebugName} location:{Location}, size:{Size}, type:{Type}, offset:{Offset}, divisor:{Divisor}";
}

/// <summary>
/// Describes the layout of a vertex buffer.
/// </summary>
public class VertexDeclaration
{
    public VertexDeclaration(AttributeLayout attributeLayout)
    {
        AddAttribute(attributeLayout);
    }
    public VertexDeclaration(IEnumerable<AttributeLayout> attributeLayout)
    {
        foreach (var attribute in attributeLayout)
            AddAttribute(attribute);
    }

    public List<AttributeLayout> Attributes { get; } = new();

    /// <summary>
    /// Adds a vertex attribute layout to the vertex declaration.
    /// </summary>
    /// <param name="attributeLayout"></param>
    /// <exception cref="ArgumentException"></exception>
    public void AddAttribute(AttributeLayout attributeLayout)
    {
        if (attributeLayout.Location < 0)
            throw new ArgumentException($"Layout location must be greater than or equal to 0!", nameof(attributeLayout));

        if (Attributes.Any(l => l.Location == attributeLayout.Location))
            throw new ArgumentException($"Layout with location {attributeLayout.Location} already exists in this vertex description!", nameof(attributeLayout));

        Attributes.Add(attributeLayout);
        Attributes.Sort((a, b) => a.Location.CompareTo(b.Location));

        var size = 0;
        for (var i = 0; i < Attributes.Count; i++)
        {
            var l = Attributes[i];
            if (l.Offset > 0)  //  offset is already set in layout, check if it's valid
            {
                if (l.Offset < size)
                    throw new ArgumentException($"Layout offset {l.Offset} is less than the current stride {size}!", nameof(attributeLayout));
            }
            else
            {
                l.Offset = size;
            }
            Attributes[i] = l;
            size += l.Size * l.Type switch
            {
                VertexAttribType.Float => sizeof(float),
                VertexAttribType.Int => sizeof(int),
                VertexAttribType.UnsignedInt => sizeof(uint),
                VertexAttribType.Short => sizeof(short),
                VertexAttribType.UnsignedShort => sizeof(ushort),
                VertexAttribType.Byte => sizeof(byte),
                VertexAttribType.UnsignedByte => sizeof(byte),
                _ => throw new ArgumentException($"Unsupported vertex attribute type {l.Type}!", nameof(attributeLayout))
            };
        }
        Stride = size;
    }

    /// <summary>
    /// Gets the stride of the vertex buffer.
    /// </summary>
    public int Stride { get; private set; }

    /// <summary>
    /// Sets the vertex attribute pointers for the given <see cref="VertexBuffer"/>.
    /// </summary>
    /// <param name="vb"></param>
    public void Apply(VertexBuffer vb) => Apply(vb.Vao);

    /// <summary>
    /// Sets the vertex attribute pointers for the named vertex array object.
    /// </summary>
    /// <param name="vao"></param>
    public void Apply(int vao)
    {
        for (var i = 0; i < Attributes.Count; i++)
        {
            var attribute = Attributes[i];
            GL.EnableVertexArrayAttrib(vao, attribute.Location);
            GL.VertexArrayAttribFormat(vao, attribute.Location, attribute.Size, attribute.Type, attribute.Normalized, attribute.Offset);
            GL.VertexArrayAttribBinding(vao, attribute.Location, 0);
            GL.VertexArrayBindingDivisor(vao, attribute.Location, attribute.Divisor);
        }
    }
}
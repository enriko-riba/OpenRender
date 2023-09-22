using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

/// <summary>
/// Describes the layout of a vertex buffer.
/// </summary>
public class VertexDeclaration
{
    public VertexDeclaration(VertexAttribLayout attributeLayout)
    {
        AddAttribute(attributeLayout);
    }
    public VertexDeclaration(IEnumerable<VertexAttribLayout> attributeLayout)
    {
        foreach (var attribute in attributeLayout)
            AddAttribute(attribute);
    }

    private readonly List<VertexAttribLayout> attributes = new();

    /// <summary>
    /// Adds a vertex attribute layout to the vertex declaration.
    /// </summary>
    /// <param name="attributeLayout"></param>
    /// <exception cref="ArgumentException"></exception>
    public void AddAttribute(VertexAttribLayout attributeLayout)
    {
        if (attributeLayout.Location < 0)
            throw new ArgumentException($"Layout location must be greater than or equal to 0!", nameof(attributeLayout));

        if (attributes.Any(l => l.Location == attributeLayout.Location))
            throw new ArgumentException($"Layout with location {attributeLayout.Location} already exists in this vertex description!", nameof(attributeLayout));

        attributes.Add(attributeLayout);
        attributes.Sort((a, b) => a.Location.CompareTo(b.Location));

        var size = 0;
        for (var i = 0; i < attributes.Count; i++)
        {
            var attrib = attributes[i];
            if (attrib.Offset > 0)  //  offset is already set in layout, check if it's valid
            {
                if (attrib.Offset < size)
                    throw new ArgumentException($"Layout offset {attrib.Offset} is less than the current stride {size}!", nameof(attributeLayout));
            }
            else
            {
                attrib.Offset = size;
            }
            attributes[i] = attrib;
            size += attrib.Size * GetAttributeTypeSize(attrib.Type);
        }
        Stride = size;
    }

    /// <summary>
    /// Gets the stride of the vertex buffer in bytes.
    /// </summary>
    public int Stride { get; private set; }

    /// <summary>
    /// Sets the vertex attribute pointers for the given <see cref="VertexBuffer"/>.
    /// </summary>
    /// <param name="vb"></param>
    //public void Apply(VertexBuffer vb) => Apply(vb.Vao);

    /// <summary>
    /// Sets the vertex attribute pointers for the named vertex array object.
    /// </summary>
    /// <param name="vao"></param>
    public void Apply(int vao, int? bindingIndex = 0)
    {
        for (var i = 0; i < attributes.Count; i++)
        {
            var attribute = attributes[i];
            GL.EnableVertexArrayAttrib(vao, attribute.Location);
            GL.VertexArrayAttribFormat(vao, attribute.Location, attribute.Size, attribute.Type, attribute.Normalized, attribute.Offset);
            GL.VertexArrayAttribBinding(vao, attribute.Location, bindingIndex??0);
            GL.VertexArrayBindingDivisor(vao, attribute.Location, attribute.Divisor);
        }
    }

    /// <summary>
    /// Gets the vertex attribute layout for the given location.
    /// </summary>
    /// <param name="location"></param>
    /// <returns></returns>
    public VertexAttribLayout GetAttribute(VertexAttribLocation location) => attributes.First(a => a.Location == (int)location);

    /// <summary>
    /// Returns the size in bytes of the given vertex attribute type.
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public static int GetAttributeTypeSize(VertexAttribType type) => type switch
    {
        VertexAttribType.Float => sizeof(float),
        VertexAttribType.Int => sizeof(int),
        VertexAttribType.UnsignedInt => sizeof(uint),
        VertexAttribType.Short => sizeof(short),
        VertexAttribType.UnsignedShort => sizeof(ushort),
        VertexAttribType.Byte => sizeof(byte),
        VertexAttribType.UnsignedByte => sizeof(byte),
        _ => throw new ArgumentException($"Unsupported vertex attribute type {type}!", nameof(type))
    };
}
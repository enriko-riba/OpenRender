using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

/// <summary>
/// Describes the layout of a vertex buffer.
/// </summary>
public class VertexDeclaration
{
    public VertexDeclaration(VertexAttribLayout attributeLayout)
    {
        if (attributeLayout.Location < 0)
            throw new ArgumentException($"Layout location must be greater than or equal to 0!", nameof(attributeLayout));
        if (Attributes.Any(l => l.Location == attributeLayout.Location))
            throw new ArgumentException($"Layout with location {attributeLayout.Location} already exists in this vertex description!", nameof(attributeLayout));

        Attributes.Add(attributeLayout);
        CalculateOffsets();
    }

    public VertexDeclaration(VertexAttribLayout[] attributeLayout)
    {
        if(attributeLayout.Any(a => a.Location < 0))
            throw new ArgumentException($"Layout location must be greater than or equal to 0!", nameof(attributeLayout));
        if(attributeLayout.Any(a => Attributes.Any(l => l.Location == a.Location)))
            throw new ArgumentException($"Layout with location {attributeLayout} already exists in this vertex description!", nameof(attributeLayout));
        Attributes.AddRange(attributeLayout);
        CalculateOffsets();
    }

    private readonly List<VertexAttribLayout> attributes = new();

    
    /// <summary>
    /// Recalculates the offsets based on the location.
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
    private void CalculateOffsets()
    {        
        Attributes.Sort((a, b) => a.Location.CompareTo(b.Location));

        var size = 0;
        for (var i = 0; i < Attributes.Count; i++)
        {
            var attrib = Attributes[i];
            if (attrib.Offset > 0)  //  offset is already set in layout, check if it's valid
            {
                if (attrib.Offset < size)
                    throw new ArgumentException($"Layout offset for attribute {attrib} is less than the current stride {size}!");
            }
            else
            {
                attrib.Offset = size;
            }
            Attributes[i] = attrib;
            size += attrib.Size * GetAttributeTypeSize(attrib.Type);
        }
        Stride = size;
    }

    /// <summary>
    /// Gets the stride of the vertex buffer in bytes.
    /// </summary>
    public int Stride { get; private set; }

    public List<VertexAttribLayout> Attributes => attributes;

    /// <summary>
    /// Sets the vertex attribute pointers for the named vertex array object.
    /// </summary>
    /// <param name="vao"></param>
    public void Apply(int vao, int? bindingIndex = 0)
    {
        for (var i = 0; i < Attributes.Count; i++)
        {
            var attribute = Attributes[i];
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
    public VertexAttribLayout GetAttribute(VertexAttribLocation location) => Attributes.First(a => a.Location == (int)location);

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
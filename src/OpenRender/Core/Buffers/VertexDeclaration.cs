using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Buffers;

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

    private readonly List<VertexAttribLayout> attributes = [];

    /// <summary>
    /// Adds a vertex attribute layout to the vertex declaration.
    /// </summary>
    /// <param name="attributeLayout"></param>
    /// <exception cref="ArgumentException"></exception>
    private void AddAttribute(VertexAttribLayout attributeLayout)
    {
        if (attributeLayout.Location < 0)
            throw new ArgumentException($"Layout location must be greater than or equal to 0!", nameof(attributeLayout));

        if (attributes.Any(l => l.Location == attributeLayout.Location))
            throw new ArgumentException($"Layout with location {attributeLayout.Location} already exists in this vertex description!", nameof(attributeLayout));

        attributes.Add(attributeLayout);
        attributes.Sort((a, b) => a.Location.CompareTo(b.Location));

        uint size = 0;
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
            size += (uint)attrib.Size * GetAttributeTypeSize(attrib.Type);
        }
        Stride = (int)size;
    }

    /// <summary>
    /// Appends the attribute to the end of the vertex declaration.
    /// </summary>
    /// <param name="attributeLayout"></param>
    public VertexDeclaration AppendAttribute(VertexAttribLayout attributeLayout)
    {
        List<VertexAttribLayout> newAttributes = [.. attributes];
        var last = newAttributes[^1];
        var lastOffsetInBytes = last.Offset;
        var size = (uint)last.Size * GetAttributeTypeSize(last.Type);
        attributeLayout.Offset = lastOffsetInBytes + size;
        newAttributes.Add(attributeLayout);
        var vdx = new VertexDeclaration(newAttributes);
        return vdx;
    }

    /// <summary>
    /// Gets the stride of the vertex buffer in bytes.
    /// </summary>
    public int Stride { get; private set; }

    public int StrideInFloats => Stride / sizeof(float);

    public IEnumerable<VertexAttribLayout> Attributes => attributes;

    public bool HasAttribute(VertexAttribLocation attributeLocation) => Attributes.Any(a => a.Location == (uint)attributeLocation);

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
    public static uint GetAttributeTypeSize(VertexAttribType type) => type switch
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
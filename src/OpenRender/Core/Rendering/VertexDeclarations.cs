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
public struct Layout
{
    public Layout(AttributeLocation location, int size, VertexAttribType type) :
        this(location.ToString(), (int)location, size, type, -1, 0)
    { }
    public Layout(AttributeLocation location, int size, VertexAttribType type, int divisor) :
        this(location.ToString(), (int)location, size, type, -1, divisor)
    { }

    public Layout(string debugName, AttributeLocation location, int size, VertexAttribType type, int offset, int divisor) :
        this(debugName, (int)location, size, type, offset, divisor)
    { }

    public Layout(string debugName, int location, int size, VertexAttribType type, int divisor) :
        this(debugName, location, size, type, -1, divisor)
    { }

    public Layout(string debugName, int location, int size, VertexAttribType type, int offset, int divisor)
    {
        DebugName = debugName;
        AttributeLocation = location;
        AttributeSize = size;
        AttributeType = type;
        AttributeOffset = offset;
        AttributeDivisor = divisor;
    }

    public string DebugName;
    public int AttributeLocation;
    public int AttributeSize;
    public VertexAttribType AttributeType;
    public int AttributeOffset;
    public int AttributeDivisor;
    public bool Normalized;
    public int Stride;
}

/// <summary>
/// Describes the layout of a vertex buffer.
/// </summary>
public class VertexDeclaration
{
    public VertexDeclaration(Layout layout)
    {
        AddLayout(layout);
    }
    public VertexDeclaration(IEnumerable<Layout> layouts)
    {
        foreach (var layout in layouts)
            AddLayout(layout);
    }

    public List<Layout> Layouts { get; } = new();

    /// <summary>
    /// Adds a vertex attribute layout to the vertex declaration.
    /// </summary>
    /// <param name="layout"></param>
    /// <exception cref="ArgumentException"></exception>
    public void AddLayout(Layout layout)
    {
        if (layout.AttributeLocation < 0)
            throw new ArgumentException($"Layout location must be greater than or equal to 0!", nameof(layout));

        if (Layouts.Any(l => l.AttributeLocation == layout.AttributeLocation))
            throw new ArgumentException($"Layout with location {layout.AttributeLocation} already exists in this vertex description!", nameof(layout));

        Layouts.Add(layout);
        Layouts.Sort((a, b) => a.AttributeLocation.CompareTo(b.AttributeLocation));

        var size = 0;
        for (var i = 0; i < Layouts.Count; i++)
        {
            var l = Layouts[i];
            if (l.AttributeOffset > 0)  //  offset is already set in layout, check if it's valid
            {
                if (l.AttributeOffset < size)
                    throw new ArgumentException($"Layout offset {l.AttributeOffset} is less than the current stride {size}!", nameof(layout));
            }
            else
            {
                l.AttributeOffset = size;
            }
            Layouts[i] = l;
            size += l.AttributeSize * l.AttributeType switch
            {
                VertexAttribType.Float => sizeof(float),
                VertexAttribType.Int => sizeof(int),
                VertexAttribType.UnsignedInt => sizeof(uint),
                VertexAttribType.Short => sizeof(short),
                VertexAttribType.UnsignedShort => sizeof(ushort),
                VertexAttribType.Byte => sizeof(byte),
                VertexAttribType.UnsignedByte => sizeof(byte),
                _ => throw new ArgumentException($"Unsupported vertex attribute type {l.AttributeType}!", nameof(layout))
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
        for (var i = 0; i < Layouts.Count; i++)
        {
            var l = Layouts[i];
            GL.EnableVertexArrayAttrib(vao, l.AttributeLocation);
            GL.VertexArrayAttribFormat(vao, l.AttributeLocation, l.AttributeSize, l.AttributeType, l.Normalized, l.AttributeOffset);
            GL.VertexArrayAttribBinding(vao, l.AttributeLocation, 0);
            GL.VertexArrayBindingDivisor(vao, l.AttributeLocation, l.AttributeDivisor);
        }
    }
}

/// <summary>
/// Contains common vertex declarations.
/// </summary>
public static class VertexDeclarations
{
    public static readonly VertexDeclaration VertexPosition = new(
        new Layout(AttributeLocation.Position, 3, VertexAttribType.Float)
    );

    public static readonly VertexDeclaration VertexPositionTexture = new(new Layout[] {
        new Layout(AttributeLocation.Position, 3, VertexAttribType.Float),
        new Layout(AttributeLocation.TextureCoord, 2, VertexAttribType.Float),
    });

    public static readonly VertexDeclaration VertexPosition2DTexture = new(new Layout[] {
        new Layout(AttributeLocation.Position, 2, VertexAttribType.Float),
        new Layout(AttributeLocation.TextureCoord, 2, VertexAttribType.Float),
    });

    public static readonly VertexDeclaration VertexPositionNormal = new(new Layout[] {
    new Layout(AttributeLocation.Position, 3, VertexAttribType.Float),
    new Layout(AttributeLocation.Normal, 3, VertexAttribType.Float),
    });

    public static readonly VertexDeclaration VertexPositionNormalTexture = new(new Layout[] {
    new Layout(AttributeLocation.Position, 3, VertexAttribType.Float),
    new Layout(AttributeLocation.Normal, 3, VertexAttribType.Float),
    new Layout(AttributeLocation.TextureCoord, 2, VertexAttribType.Float),
    });

    public static readonly VertexDeclaration VertexPositionColorTexture = new(new Layout[] {
    new Layout(AttributeLocation.Position, 3, VertexAttribType.Float),
    new Layout(AttributeLocation.Color, 3, VertexAttribType.Float),
    new Layout(AttributeLocation.TextureCoord, 2, VertexAttribType.Float),
    });

    public static readonly VertexDeclaration VertexPositionColor = new(new Layout[] {
    new Layout(AttributeLocation.Position, 3, VertexAttribType.Float),
    new Layout(AttributeLocation.Color, 3, VertexAttribType.Float),
    });

    public static readonly VertexDeclaration VertexPositionNormalColor = new(new Layout[] {
     new Layout(AttributeLocation.Position, 3, VertexAttribType.Float),
     new Layout(AttributeLocation.Normal, 3, VertexAttribType.Float),
     new Layout(AttributeLocation.Color, 3, VertexAttribType.Float),
     });

    public static readonly VertexDeclaration VertexPositionNormalColorTexture = new(new Layout[] {
     new Layout(AttributeLocation.Position, 3, VertexAttribType.Float),
     new Layout(AttributeLocation.Normal, 3, VertexAttribType.Float),
     new Layout(AttributeLocation.Color, 3, VertexAttribType.Float),
     new Layout(AttributeLocation.TextureCoord, 2, VertexAttribType.Float),
     });
}
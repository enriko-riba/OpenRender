using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;
/// <summary>
/// Well known attribute locations.
/// </summary>
public enum VertexAttribLocation
{
    Position,
    Normal,
    Color,
    TextureCoord,
    Tangent,
    Bitangent,
    ModelMatrix1,
    ModelMatrix2,
    ModelMatrix3,
    ModelMatrix4,
}

/// <summary>
/// Holds information about a vertex attribute layout.
/// </summary>
public struct VertexAttribLayout
{
    public VertexAttribLayout(VertexAttribLocation location, int size, VertexAttribType type) :
        this(location.ToString(), (uint)location, size, type, 0, 0)
    { }
    public VertexAttribLayout(VertexAttribLocation location, int size, VertexAttribType type, uint divisor) :
        this(location.ToString(), (uint)location, size, type, 0, divisor)
    { }

    public VertexAttribLayout(string debugName, VertexAttribLocation location, int size, VertexAttribType type, uint offset, uint divisor) :
        this(debugName, (uint)location, size, type, offset, divisor)
    { }

    public VertexAttribLayout(string debugName, uint location, int size, VertexAttribType type, uint divisor) :
        this(debugName, location, size, type, 0, divisor)
    { }

    public VertexAttribLayout(string debugName, uint location, int size, VertexAttribType type, uint offset, uint divisor)
    {
        DebugName = debugName;
        Location = location;
        Size = size;
        Type = type;
        Offset = offset;
        Divisor = divisor;
    }

    /// <summary>
    /// Attribute name for debug purposes.
    /// </summary>
    public string DebugName { get; set; }

    /// <summary>
    /// Attribute location.
    /// </summary>
    public uint Location { get; set; }

    /// <summary>
    /// Size in <see cref="Type"/> units.
    /// </summary>
    public int Size { get; set; }

    public VertexAttribType Type { get; set; }

    /// <summary>
    /// Offset in bytes.
    /// </summary>
    public uint Offset { get; set; }

    /// <summary>
    /// Divisor for instanced rendering.
    /// </summary>
    public uint Divisor { get; set; }

    public bool Normalized { get; set; }

    public override readonly string ToString() => $"{DebugName} location:{Location}, size:{Size}, type:{Type}, offset:{Offset}, divisor:{Divisor}";
}
using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Buffers;
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
public struct VertexAttribLayout(string debugName, uint location, int size, VertexAttribType type, uint offset, uint divisor)
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

    /// <summary>
    /// Attribute name for debug purposes.
    /// </summary>
    public string DebugName { get; set; } = debugName;

    /// <summary>
    /// Attribute location.
    /// </summary>
    public uint Location { get; set; } = location;

    /// <summary>
    /// Size in <see cref="Type"/> units.
    /// </summary>
    public int Size { get; set; } = size;

    public VertexAttribType Type { get; set; } = type;

    /// <summary>
    /// Offset in bytes.
    /// </summary>
    public uint Offset { get; set; } = offset;

    /// <summary>
    /// Divisor for instanced rendering.
    /// </summary>
    public uint Divisor { get; set; } = divisor;

    public bool Normalized { get; set; }

    public override readonly string ToString() => $"{DebugName} location:{Location}, size:{Size}, type:{Type}, offset:{Offset}, divisor:{Divisor}";
}
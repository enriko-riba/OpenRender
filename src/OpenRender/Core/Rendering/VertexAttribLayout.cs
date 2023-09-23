using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

/// <summary>
/// Well known attribute locations, defined as layouts in all OpenRenderer shaders.
/// </summary>
public enum VertexAttribLocation
{
    /// <summary>
    /// Attribute streaming vertices.
    /// </summary>
    Position = 0,

    /// <summary>
    /// Attribute streaming texture coordinates.
    /// </summary>
    TextureCoord,

    /// <summary>
    /// Attribute streaming normals.
    /// </summary>
    Normal,

    /// <summary>
    /// Attribute streaming colors.
    /// </summary>
    Color,

}

/// <summary>
/// Holds information about a vertex attribute layout.
/// </summary>
public struct VertexAttribLayout
{
    public VertexAttribLayout(int location, int size, VertexAttribType type, int divisor, string? debugName = null) :
        this(location, size, type, -1, divisor, debugName) { }
    public VertexAttribLayout(VertexAttribLocation location, int size, VertexAttribType type, string? debugName = null) :
        this((int)location, size, type, -1, 0, debugName) { }
    public VertexAttribLayout(VertexAttribLocation location, int size, VertexAttribType type, int divisor, string? debugName = null) :
        this((int)location, size, type, -1, divisor, debugName) { }
    public VertexAttribLayout(VertexAttribLocation location, int size, VertexAttribType type, int offset, int divisor, string? debugName = null) :
        this((int)location, size, type, offset, divisor, debugName) { }


    public VertexAttribLayout(int location, int size, VertexAttribType type, int offset, int divisor, string? debugName = null)
    {
        DebugName = debugName?? (Enum.IsDefined((VertexAttribLocation)location) ? 
                                        ((VertexAttribLocation)location).ToString() : 
                                        location.ToString());
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
    public int Location { get; set; }

    /// <summary>
    /// Size in <see cref="Type"/> units.
    /// </summary>
    public int Size { get; set; }

    public VertexAttribType Type { get; set; }

    /// <summary>
    /// Offset in bytes.
    /// </summary>
    public int Offset { get; set; }

    /// <summary>
    /// Divisor for instanced rendering.
    /// </summary>
    public int Divisor { get; set; }

    public bool Normalized { get; set; }

    public override readonly string ToString() => $"{DebugName} location:{Location}, size:{Size}, type:{Type}, offset:{Offset}, divisor:{Divisor}";
}
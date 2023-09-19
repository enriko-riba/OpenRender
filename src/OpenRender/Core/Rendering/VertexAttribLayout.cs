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
}

/// <summary>
/// Holds information about a vertex attribute layout.
/// </summary>
public struct VertexAttribLayout
{
    public VertexAttribLayout(VertexAttribLocation location, int size, VertexAttribType type) :
        this(location.ToString(), (int)location, size, type, -1, 0)
    { }
    public VertexAttribLayout(VertexAttribLocation location, int size, VertexAttribType type, int divisor) :
        this(location.ToString(), (int)location, size, type, -1, divisor)
    { }

    public VertexAttribLayout(string debugName, VertexAttribLocation location, int size, VertexAttribType type, int offset, int divisor) :
        this(debugName, (int)location, size, type, offset, divisor)
    { }

    public VertexAttribLayout(string debugName, int location, int size, VertexAttribType type, int divisor) :
        this(debugName, location, size, type, -1, divisor)
    { }

    public VertexAttribLayout(string debugName, int location, int size, VertexAttribType type, int offset, int divisor)
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
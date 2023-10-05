using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

public class IndexBuffer : Buffer<uint>
{
    public IndexBuffer(uint[] data, BufferUsageHint usageHint = BufferUsageHint.StaticDraw) : base(data, default, usageHint) { }
}

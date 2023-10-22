namespace OpenRender.Core.Rendering;

public class IndexBuffer : Buffer<uint>
{
    public IndexBuffer(uint[] data) : base(data)
    {
        SetLabel("IBO");
    }
}

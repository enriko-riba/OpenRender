using System.Runtime.InteropServices;

namespace OpenRender.Core.Rendering.Batching;

[StructLayout(LayoutKind.Sequential)]
public struct TextureData
{
    public long Diffuse;
    public long Detail;
    public long Normal;
}

using System.Runtime.InteropServices;

namespace OpenRender.Core.Rendering.Batching;

[StructLayout(LayoutKind.Sequential)]
public struct TextureData
{
    public long Diffuse;
    public long Detail;
    public long Normal;
    public long Specular;
    public long Bump;
    public long T6;
    public long T7;
    public long T8;
}

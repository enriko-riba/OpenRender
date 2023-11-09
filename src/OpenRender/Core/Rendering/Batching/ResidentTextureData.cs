using System.Runtime.InteropServices;

namespace OpenRender.Core.Rendering.Batching;

[StructLayout(LayoutKind.Sequential)]
public struct ResidentTextureData
{
    public ulong Diffuse;
    public ulong Detail;
    public ulong Normal;
    public ulong Specular;
    public ulong Bump;
    public ulong T6;
    public ulong T7;
    public ulong T8;
}

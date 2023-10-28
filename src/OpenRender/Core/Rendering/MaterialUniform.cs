using OpenTK.Mathematics;
using System.Runtime.InteropServices;

namespace OpenRender.Core.Rendering;

[StructLayout(LayoutKind.Explicit)]
public struct MaterialUniform
{
    [FieldOffset(0)]
    public Vector3 Diffuse;
    [FieldOffset(16)]
    public Vector3 Emissive;
    [FieldOffset(32)]
    public Vector3 Specular;
    [FieldOffset(44)]
    public float Shininess;
}
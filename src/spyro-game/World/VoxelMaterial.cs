using OpenTK.Mathematics;
using System.Runtime.InteropServices;

namespace SpyroGame.World;

[StructLayout(LayoutKind.Explicit)]
public struct VoxelMaterial
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
using OpenTK.Mathematics;
using System.Runtime.InteropServices;

namespace OpenRender.Core.Rendering;

[StructLayout(LayoutKind.Explicit)]
public struct LightUniform : ISize
{
    /// <summary>
    /// Direction for directional lights or position for point and spot lights.
    /// </summary>   
    [FieldOffset(0)]
    public Vector3 Position;
    [FieldOffset(0)]
    public Vector3 Direction;
    [FieldOffset(16)]
    public Vector3 Ambient;
    [FieldOffset(32)]
    public Vector3 Diffuse;
    [FieldOffset(48)]
    public Vector3 Specular;
    [FieldOffset(60)]
    public float Falloff;

    public static int Size => 64;
}
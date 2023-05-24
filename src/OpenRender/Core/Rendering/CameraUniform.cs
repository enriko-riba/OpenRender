using OpenTK.Mathematics;
using System.Runtime.InteropServices;

namespace OpenRender.Core.Rendering;
public interface ISize
{
    static abstract int Size { get; }
}

[StructLayout(LayoutKind.Explicit, Pack = 1)]
public struct CameraUniform: ISize
{
    [FieldOffset(0)]
    public Matrix4 view;

    [FieldOffset(64)]
    public Matrix4 projection;

    [FieldOffset(128)]
    public Vector3 position;

    [FieldOffset(144)]
    public Vector3 direction;

    public static int Size => 160;
}
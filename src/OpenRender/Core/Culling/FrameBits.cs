using System.Runtime.CompilerServices;

namespace OpenRender.Core.Culling;

/// <summary>
/// Data holder for per frame state, used as performance optimization. 
/// Note: Any of the flags set will prevent the node from being rendered. This simplifies the rendering condition checks.
/// </summary>
internal struct FrameBits
{
    private uint bitField;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetFlag(FrameBitsFlags flag) => bitField |= (uint)flag;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearFlag(FrameBitsFlags flag)
    {
        var mask = ~(uint)flag;
        bitField &= mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool HasFlag(FrameBitsFlags flag) => (bitField & (uint)flag) != 0;

    public readonly uint Value => bitField;
}

[Flags]
public enum FrameBitsFlags : uint
{
    FrustumCulled = 1 << 0,
    DistanceCulled = 1 << 1,
    NotVisible = 1 << 2,
}

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
    /// <summary>
    /// Node is not inside the camera frustum and should not be rendered.
    /// </summary>
    FrustumCulled = 1 << 0,

    /// <summary>
    /// Node is too far away from the camera and should not be rendered.
    /// </summary>
    DistanceCulled = 1 << 1,

    /// <summary>
    /// Node is not visible and should not be rendered.
    /// </summary>
    NotVisible = 1 << 2,

    /// <summary>
    /// Bitmask used to check if node is allowed to be rendered.
    /// </summary>
    RenderMask = FrustumCulled | DistanceCulled | NotVisible,

    /// <summary>
    /// Node is allowed to be batched.
    /// </summary>
    BatchAllowed = 1 << 3,
}

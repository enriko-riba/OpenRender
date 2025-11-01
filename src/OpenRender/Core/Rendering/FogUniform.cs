using OpenTK.Mathematics;
using System.Runtime.InteropServices;

namespace OpenRender.Core.Rendering;

/// <summary>
/// Fog UBO layout used by shaders under the name `fogBlock` at binding = 4.
/// fogColor4.rgb = fog color; fogParams = (near, far, enabled, unused)
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public struct FogUniform
{
    [FieldOffset(0)]
    public Vector4 FogColor;

    [FieldOffset(16)]
    public Vector4 FogParams;

    // Legacy defaults used by Renderer when no override is provided
    public const float LEGACY_FAR_CUSHION = 0.5f;
    public const float LEGACY_FAR_PLANE = 430.0f;
}


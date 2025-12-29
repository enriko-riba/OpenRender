namespace SpyroGame.World.Registry;

/// <summary>
/// Defines how the GPU should render a block.
/// </summary>
public enum RenderMethod : byte
{
    /// <summary>No rendering (air, markers).</summary>
    None,
    /// <summary>Standard opaque rendering with depth write.</summary>
    Opaque,
    /// <summary>Alpha test (cutout) - binary transparency, writes depth.</summary>
    AlphaTest,
    /// <summary>Alpha blend - smooth transparency, no depth write.</summary>
    Blend,
}

/// <summary>
/// Defines the geometric shape used for mesh generation.
/// </summary>
public enum BlockRenderShape : byte
{
    /// <summary>No geometry (air).</summary>
    None,
    /// <summary>Standard 1×1×1 cube with 6 faces.</summary>
    FullCube,
    /// <summary>Small 1/10th size cube (buttons, small vegetation).</summary>
    Cubelet,
    /// <summary>Two crossed quads (torches, flowers, saplings).</summary>
    CrossBillboard,
}

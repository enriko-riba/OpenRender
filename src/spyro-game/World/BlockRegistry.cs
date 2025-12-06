using System.Collections.Frozen;

namespace SpyroGame.World;

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
    /// <summary>Two crossed quads (torches, flowers, saplings).</summary>
    CrossBillboard,
}

/// <summary>
/// Block property flags for fast bitwise checks.
/// </summary>
[Flags]
public enum BlockFlags : byte
{
    None = 0,
    /// <summary>Blocks movement, needs faces rendered.</summary>
    Solid = 1 << 0,
    /// <summary>Blocks light, culls neighbor faces when adjacent.</summary>
    Opaque = 1 << 1,
    /// <summary>Water/lava flow behavior.</summary>
    Liquid = 1 << 2,
    /// <summary>Partial transparency (water, ice, leaves).</summary>
    Translucent = 1 << 3,
    /// <summary>Can be overwritten by other blocks (air, water, tall grass).</summary>
    Replaceable = 1 << 4,
    /// <summary>Emits light, rendered at full brightness.</summary>
    Emissive = 1 << 5,
}

/// <summary>
/// Immutable properties for a block type.
/// Combines both bitflags and extended properties in a single struct.
/// </summary>
/// <param name="Flags">Combined block property flags.</param>
/// <param name="LightValue">Luminance emitted by the block (0-15).</param>
/// <param name="LightFilter">How much light is diminished when passing through (0-15).</param>
/// <param name="IsFullCube">Whether the block fills a full 1m³ cube.</param>
/// <param name="Render">How the GPU should render this block.</param>
/// <param name="Shape">The geometric shape used for mesh generation.</param>
public readonly record struct BlockProperties(
    BlockFlags Flags,
    byte LightValue,
    byte LightFilter,
    bool IsFullCube,
    RenderMethod Render,
    BlockRenderShape Shape = BlockRenderShape.FullCube)
{
    /// <summary>Default properties for unknown blocks (opaque solid).</summary>
    public static readonly BlockProperties Default = new(
        BlockFlags.Solid | BlockFlags.Opaque,
        0, 15, true, RenderMethod.Opaque, BlockRenderShape.FullCube);

    /// <summary>Properties for air (invisible, no collision).</summary>
    public static readonly BlockProperties Air = new(
        BlockFlags.Replaceable,
        0, 0, false, RenderMethod.None, BlockRenderShape.None);

    // Convenience flag checks
    public bool IsSolid => (Flags & BlockFlags.Solid) != 0;
    public bool IsOpaque => (Flags & BlockFlags.Opaque) != 0;
    public bool IsLiquid => (Flags & BlockFlags.Liquid) != 0;
    public bool IsTranslucent => (Flags & BlockFlags.Translucent) != 0;
    public bool IsReplaceable => (Flags & BlockFlags.Replaceable) != 0;
    public bool IsEmissive => (Flags & BlockFlags.Emissive) != 0;
}

/// <summary>
/// Static registry providing fast O(1) lookup for all block properties.
/// Uses a <see cref="FrozenDictionary{TKey, TValue}"/> for optimal runtime performance.
/// </summary>
public static class BlockRegistry
{
    private static readonly FrozenDictionary<ushort, BlockProperties> Properties;

    static BlockRegistry()
    {
        var builder = new Dictionary<ushort, BlockProperties>();

        // === Special ===
        Register(builder, BlockId.Air, BlockProperties.Air);
        Register(builder, BlockId.Water, new(
            BlockFlags.Liquid | BlockFlags.Translucent | BlockFlags.Replaceable,
            0, 2, false, RenderMethod.Blend));
        Register(builder, BlockId.Lava, new(
            BlockFlags.Liquid | BlockFlags.Emissive | BlockFlags.Replaceable,
            15, 0, false, RenderMethod.Blend));

        // === Stone Types ===
        RegisterOpaqueSolid(builder, BlockId.Stone);
        RegisterOpaqueSolid(builder, BlockId.Bedrock);
        RegisterOpaqueSolid(builder, BlockId.Cobblestone);
        RegisterOpaqueSolid(builder, BlockId.MossyCobblestone);

        // === Dirt Types ===
        RegisterOpaqueSolid(builder, BlockId.Dirt);
        RegisterOpaqueSolid(builder, BlockId.Grass);
        RegisterOpaqueSolid(builder, BlockId.GrassSnowy);
        RegisterOpaqueSolid(builder, BlockId.Podzol);
        RegisterOpaqueSolid(builder, BlockId.Mycelium);
        RegisterOpaqueSolid(builder, BlockId.CoarseDirt);

        // === Sand Types ===
        RegisterOpaqueSolid(builder, BlockId.Sand);
        RegisterOpaqueSolid(builder, BlockId.RedSand);
        RegisterOpaqueSolid(builder, BlockId.Sandstone);
        RegisterOpaqueSolid(builder, BlockId.RedSandstone);

        // === Gravel/Clay ===
        RegisterOpaqueSolid(builder, BlockId.Gravel);
        RegisterOpaqueSolid(builder, BlockId.Clay);

        // === Snow/Ice ===
        Register(builder, BlockId.Snow, new(
            BlockFlags.Solid | BlockFlags.Opaque,
            0, 15, false, RenderMethod.Opaque)); // Not full cube
        RegisterOpaqueSolid(builder, BlockId.SnowDirt);
        Register(builder, BlockId.Ice, new(
            BlockFlags.Solid | BlockFlags.Translucent,
            0, 1, true, RenderMethod.Blend));
        Register(builder, BlockId.PackedIce, new(
            BlockFlags.Solid | BlockFlags.Translucent,
            0, 1, true, RenderMethod.Blend));
        Register(builder, BlockId.BlueIce, new(
            BlockFlags.Solid | BlockFlags.Translucent,
            0, 1, true, RenderMethod.Blend));

        // === Terracotta ===
        RegisterOpaqueSolid(builder, BlockId.Terracotta);
        RegisterOpaqueSolid(builder, BlockId.WhiteTerracotta);
        RegisterOpaqueSolid(builder, BlockId.OrangeTerracotta);
        RegisterOpaqueSolid(builder, BlockId.RedTerracotta);
        RegisterOpaqueSolid(builder, BlockId.BrownTerracotta);
        RegisterOpaqueSolid(builder, BlockId.YellowTerracotta);

        // === Ore Types ===
        RegisterOpaqueSolid(builder, BlockId.CoalOre);
        RegisterOpaqueSolid(builder, BlockId.IronOre);
        RegisterOpaqueSolid(builder, BlockId.GoldOre);
        RegisterOpaqueSolid(builder, BlockId.DiamondOre);
        RegisterOpaqueSolid(builder, BlockId.CopperOre);

        // === Wood Types ===
        RegisterOpaqueSolid(builder, BlockId.OakLog);
        RegisterOpaqueSolid(builder, BlockId.BirchLog);
        RegisterOpaqueSolid(builder, BlockId.SpruceLog);
        RegisterOpaqueSolid(builder, BlockId.JungleLog);

        // === Leaves (translucent, alpha test) ===
        RegisterLeaves(builder, BlockId.OakLeaves);
        RegisterLeaves(builder, BlockId.BirchLeaves);
        RegisterLeaves(builder, BlockId.SpruceLeaves);
        RegisterLeaves(builder, BlockId.JungleLeaves);

        // === Light Sources ===
        Register(builder, BlockId.Torch, new(
            BlockFlags.Emissive,
            14, 0, false, RenderMethod.AlphaTest, BlockRenderShape.CrossBillboard));
        Register(builder, BlockId.WallTorch, new(
            BlockFlags.Emissive,
            14, 0, false, RenderMethod.AlphaTest, BlockRenderShape.CrossBillboard));
        Register(builder, BlockId.SoulTorch, new(
            BlockFlags.Emissive,
            10, 0, false, RenderMethod.AlphaTest, BlockRenderShape.CrossBillboard));
        Register(builder, BlockId.Glowstone, new(
            BlockFlags.Solid | BlockFlags.Opaque | BlockFlags.Emissive,
            15, 15, true, RenderMethod.Opaque));
        Register(builder, BlockId.SeaLantern, new(
            BlockFlags.Solid | BlockFlags.Translucent | BlockFlags.Emissive,
            15, 1, true, RenderMethod.Blend));
        Register(builder, BlockId.Lantern, new(
            BlockFlags.Emissive,
            15, 0, false, RenderMethod.AlphaTest, BlockRenderShape.CrossBillboard));
        Register(builder, BlockId.SoulLantern, new(
            BlockFlags.Emissive,
            10, 0, false, RenderMethod.AlphaTest, BlockRenderShape.CrossBillboard));
        RegisterOpaqueSolid(builder, BlockId.RedstoneLamp);
        Register(builder, BlockId.RedstoneLampOn, new(
            BlockFlags.Solid | BlockFlags.Opaque | BlockFlags.Emissive,
            15, 15, true, RenderMethod.Opaque));
        Register(builder, BlockId.EndRod, new(
            BlockFlags.Emissive,
            14, 0, false, RenderMethod.AlphaTest, BlockRenderShape.CrossBillboard));
        Register(builder, BlockId.Shroomlight, new(
            BlockFlags.Solid | BlockFlags.Opaque | BlockFlags.Emissive,
            15, 15, true, RenderMethod.Opaque));
        Register(builder, BlockId.JackOLantern, new(
            BlockFlags.Solid | BlockFlags.Opaque | BlockFlags.Emissive,
            15, 15, true, RenderMethod.Opaque));
        Register(builder, BlockId.Campfire, new(
            BlockFlags.Emissive,
            15, 0, false, RenderMethod.AlphaTest));
        Register(builder, BlockId.SoulCampfire, new(
            BlockFlags.Emissive,
            10, 0, false, RenderMethod.AlphaTest));

        // === Glass (translucent, filter=0 for light) ===
        RegisterGlass(builder, BlockId.Glass);
        RegisterGlass(builder, BlockId.WhiteStainedGlass);
        RegisterGlass(builder, BlockId.OrangeStainedGlass);
        RegisterGlass(builder, BlockId.MagentaStainedGlass);
        RegisterGlass(builder, BlockId.LightBlueStainedGlass);
        RegisterGlass(builder, BlockId.YellowStainedGlass);
        RegisterGlass(builder, BlockId.LimeStainedGlass);
        RegisterGlass(builder, BlockId.PinkStainedGlass);
        RegisterGlass(builder, BlockId.GrayStainedGlass);
        RegisterGlass(builder, BlockId.LightGrayStainedGlass);
        RegisterGlass(builder, BlockId.CyanStainedGlass);
        RegisterGlass(builder, BlockId.PurpleStainedGlass);
        RegisterGlass(builder, BlockId.BlueStainedGlass);
        RegisterGlass(builder, BlockId.BrownStainedGlass);
        RegisterGlass(builder, BlockId.GreenStainedGlass);
        RegisterGlass(builder, BlockId.RedStainedGlass);
        RegisterGlass(builder, BlockId.BlackStainedGlass);
        // Tinted glass: blocks all light (filter=15) but is see-through
        Register(builder, BlockId.TintedGlass, new(
            BlockFlags.Solid | BlockFlags.Translucent,
            0, 15, true, RenderMethod.Blend));

        Properties = builder.ToFrozenDictionary();
    }

    private static void Register(Dictionary<ushort, BlockProperties> builder, BlockId block, BlockProperties props)
        => builder[(ushort)block] = props;

    private static void RegisterOpaqueSolid(Dictionary<ushort, BlockProperties> builder, BlockId block)
        => Register(builder, block, BlockProperties.Default);

    private static void RegisterLeaves(Dictionary<ushort, BlockProperties> builder, BlockId block)
        => Register(builder, block, new(
            BlockFlags.Solid | BlockFlags.Translucent,
            0, 1, true, RenderMethod.AlphaTest));

    private static void RegisterGlass(Dictionary<ushort, BlockProperties> builder, BlockId block)
        => Register(builder, block, new(
            BlockFlags.Solid | BlockFlags.Translucent,
            0, 0, true, RenderMethod.Blend));

    /// <summary>
    /// Gets all properties for a block type.
    /// </summary>
    public static BlockProperties GetProperties(BlockId block)
        => Properties.TryGetValue((ushort)block, out var props) ? props : BlockProperties.Default;

    // === Fast property accessors ===

    /// <summary>Gets the light value (luminance) emitted by a block (0-15).</summary>
    public static byte GetLightValue(BlockId block) => GetProperties(block).LightValue;

    /// <summary>Gets the light filter (opacity) for a block (0-15).</summary>
    public static byte GetLightFilter(BlockId block) => GetProperties(block).LightFilter;

    /// <summary>Gets the render shape for a block.</summary>
    public static BlockRenderShape GetRenderShape(BlockId block) => GetProperties(block).Shape;

    /// <summary>Gets the effective light decay when light passes through a block.</summary>
    public static int GetLightDecay(BlockId block)
    {
        var filter = GetLightFilter(block);
        return filter >= 15 ? 15 : Math.Max(1, (int)filter);
    }

    /// <summary>Checks if a block completely blocks light (filter = 15).</summary>
    public static bool BlocksLight(BlockId block) => GetLightFilter(block) >= 15;

    /// <summary>Checks if a block emits light (light value > 0).</summary>
    public static bool EmitsLight(BlockId block) => GetLightValue(block) > 0;

    // === Flag accessors (delegate to properties) ===

    /// <summary>Returns true if this block is solid.</summary>
    public static bool IsSolid(BlockId block) => GetProperties(block).IsSolid;

    /// <summary>Returns true if this block is opaque.</summary>
    public static bool IsOpaque(BlockId block) => GetProperties(block).IsOpaque;

    /// <summary>Returns true if this block is a liquid.</summary>
    public static bool IsLiquid(BlockId block) => GetProperties(block).IsLiquid;

    /// <summary>Returns true if this block is translucent.</summary>
    public static bool IsTranslucent(BlockId block) => GetProperties(block).IsTranslucent;

    /// <summary>Returns true if this block is replaceable.</summary>
    public static bool IsReplaceable(BlockId block) => GetProperties(block).IsReplaceable;

    /// <summary>Returns true if this block is emissive.</summary>
    public static bool IsEmissive(BlockId block) => GetProperties(block).IsEmissive;
}

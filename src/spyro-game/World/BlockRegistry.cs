using SpyroGame.World;
using System;

namespace SpyroGame.World;

/// <summary>
/// Defines how a block should be rendered by the GPU.
/// </summary>
public enum RenderMethod : byte
{
    /// <summary>Not rendered (Air, structural blocks).</summary>
    None = 0,
    /// <summary>Standard solid blocks with no transparency.</summary>
    Opaque = 1,
    /// <summary>Binary transparency using alpha testing (leaves, torches, rails).</summary>
    AlphaTest = 2,
    /// <summary>Partial transparency using alpha blending (water, glass, ice).</summary>
    Blend = 3
}

/// <summary>
/// Immutable properties for a block type that cannot be encoded in the <see cref="BlockId"/> bitfield.
/// </summary>
/// <param name="LightValue">Luminance emitted by the block (0-15). 0 = no light, 15 = maximum brightness.</param>
/// <param name="LightFilter">How much light is diminished when passing through (0-15). 0 = fully transparent, 15 = fully opaque.</param>
/// <param name="IsFullCube">Whether the block fills a full 1m³ cube for physics and face culling.</param>
/// <param name="Render">How the GPU should render this block.</param>
public readonly record struct BlockProperties(
    byte LightValue,
    byte LightFilter,
    bool IsFullCube,
    RenderMethod Render)
{
    /// <summary>
    /// Default properties for unknown or unregistered blocks (opaque solid block with no light emission).
    /// </summary>
    public static readonly BlockProperties Default = new(0, 15, true, RenderMethod.Opaque);

    /// <summary>
    /// Properties for air (invisible, no collision, fully transparent to light).
    /// </summary>
    public static readonly BlockProperties Air = new(0, 0, false, RenderMethod.None);
}

/// <summary>
/// Static registry providing fast O(1) lookup for block properties that cannot be encoded in <see cref="BlockId"/>.
/// Used by <see cref="LightingCalculator"/> for light emission and propagation calculations.
/// </summary>
public static class BlockRegistry
{
    /// <summary>
    /// Maximum number of unique block IDs supported (10-bit ID space).
    /// </summary>
    private const int MaxBlockIds = 1024;

    /// <summary>
    /// Direct lookup array for O(1) access. Index = block ID (lower 10 bits of BlockId).
    /// </summary>
    private static readonly BlockProperties[] Properties = new BlockProperties[MaxBlockIds];

    /// <summary>
    /// Static constructor populates all known block properties.
    /// </summary>
    static BlockRegistry()
    {
        // Initialize all slots with default properties
        Array.Fill(Properties, BlockProperties.Default);

        // === Air and Special ===
        Register(BlockId.Air, BlockProperties.Air);

        // === Liquids ===
        // Water: partially transparent, filter=2, not full cube
        Register(BlockId.Water, new BlockProperties(0, 2, false, RenderMethod.Blend));
        // Lava: emits max light (15), filter=0 (light source), not full cube
        Register(BlockId.Lava, new BlockProperties(15, 0, false, RenderMethod.Blend));

        // === Stone Types (opaque, full cube, no light) ===
        RegisterOpaqueSolid(BlockId.Stone);
        RegisterOpaqueSolid(BlockId.Bedrock);
        RegisterOpaqueSolid(BlockId.Cobblestone);
        RegisterOpaqueSolid(BlockId.MossyCobblestone);

        // === Dirt Types ===
        RegisterOpaqueSolid(BlockId.Dirt);
        RegisterOpaqueSolid(BlockId.Grass);
        RegisterOpaqueSolid(BlockId.GrassSnowy);
        RegisterOpaqueSolid(BlockId.Podzol);
        RegisterOpaqueSolid(BlockId.Mycelium);
        RegisterOpaqueSolid(BlockId.CoarseDirt);

        // === Sand Types ===
        RegisterOpaqueSolid(BlockId.Sand);
        RegisterOpaqueSolid(BlockId.RedSand);
        RegisterOpaqueSolid(BlockId.Sandstone);
        RegisterOpaqueSolid(BlockId.RedSandstone);

        // === Gravel/Clay ===
        RegisterOpaqueSolid(BlockId.Gravel);
        RegisterOpaqueSolid(BlockId.Clay);

        // === Snow/Ice ===
        // Snow layer is not a full cube but blocks light
        Register(BlockId.Snow, new BlockProperties(0, 15, false, RenderMethod.Opaque));
        RegisterOpaqueSolid(BlockId.SnowDirt);
        // Ice: translucent, filter=1
        Register(BlockId.Ice, new BlockProperties(0, 1, true, RenderMethod.Blend));
        Register(BlockId.PackedIce, new BlockProperties(0, 1, true, RenderMethod.Blend));
        Register(BlockId.BlueIce, new BlockProperties(0, 1, true, RenderMethod.Blend));

        // === Terracotta ===
        RegisterOpaqueSolid(BlockId.Terracotta);
        RegisterOpaqueSolid(BlockId.WhiteTerracotta);
        RegisterOpaqueSolid(BlockId.OrangeTerracotta);
        RegisterOpaqueSolid(BlockId.RedTerracotta);
        RegisterOpaqueSolid(BlockId.BrownTerracotta);
        RegisterOpaqueSolid(BlockId.YellowTerracotta);

        // === Ore Types ===
        RegisterOpaqueSolid(BlockId.CoalOre);
        RegisterOpaqueSolid(BlockId.IronOre);
        RegisterOpaqueSolid(BlockId.GoldOre);
        RegisterOpaqueSolid(BlockId.DiamondOre);
        RegisterOpaqueSolid(BlockId.CopperOre);

        // === Wood Types (logs are opaque solids) ===
        RegisterOpaqueSolid(BlockId.OakLog);
        RegisterOpaqueSolid(BlockId.BirchLog);
        RegisterOpaqueSolid(BlockId.SpruceLog);
        RegisterOpaqueSolid(BlockId.JungleLog);

        // === Leaves (translucent, filter light by 1, use alpha test) ===
        RegisterLeaves(BlockId.OakLeaves);
        RegisterLeaves(BlockId.BirchLeaves);
        RegisterLeaves(BlockId.SpruceLeaves);
        RegisterLeaves(BlockId.JungleLeaves);

        // === Light Sources ===
        // Torch: light value 14, filter 0 (fully transparent to light), not a full cube
        Register(BlockId.Torch, new BlockProperties(14, 0, false, RenderMethod.AlphaTest));
        Register(BlockId.WallTorch, new BlockProperties(14, 0, false, RenderMethod.AlphaTest));
        // Soul torch: dimmer light value 10
        Register(BlockId.SoulTorch, new BlockProperties(10, 0, false, RenderMethod.AlphaTest));
        // Glowstone: light value 15, opaque, full cube
        Register(BlockId.Glowstone, new BlockProperties(15, 15, true, RenderMethod.Opaque));
        // Sea lantern: light value 15, translucent
        Register(BlockId.SeaLantern, new BlockProperties(15, 1, true, RenderMethod.Blend));
        // Lantern: light value 15, not a full cube
        Register(BlockId.Lantern, new BlockProperties(15, 0, false, RenderMethod.AlphaTest));
        Register(BlockId.SoulLantern, new BlockProperties(10, 0, false, RenderMethod.AlphaTest));
        // Redstone lamp (off): no light
        RegisterOpaqueSolid(BlockId.RedstoneLamp);
        // Redstone lamp (on): light value 15
        Register(BlockId.RedstoneLampOn, new BlockProperties(15, 15, true, RenderMethod.Opaque));
        // End rod: light value 14
        Register(BlockId.EndRod, new BlockProperties(14, 0, false, RenderMethod.AlphaTest));
        // Shroomlight: light value 15
        Register(BlockId.Shroomlight, new BlockProperties(15, 15, true, RenderMethod.Opaque));
        // Jack o'Lantern: light value 15
        Register(BlockId.JackOLantern, new BlockProperties(15, 15, true, RenderMethod.Opaque));
        // Campfire: light value 15
        Register(BlockId.Campfire, new BlockProperties(15, 0, false, RenderMethod.AlphaTest));
        Register(BlockId.SoulCampfire, new BlockProperties(10, 0, false, RenderMethod.AlphaTest));

        // === Glass (fully transparent to light, filter=0) ===
        Register(BlockId.Glass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        Register(BlockId.WhiteStainedGlass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        Register(BlockId.OrangeStainedGlass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        Register(BlockId.MagentaStainedGlass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        Register(BlockId.LightBlueStainedGlass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        Register(BlockId.YellowStainedGlass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        Register(BlockId.LimeStainedGlass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        Register(BlockId.PinkStainedGlass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        Register(BlockId.GrayStainedGlass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        Register(BlockId.LightGrayStainedGlass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        Register(BlockId.CyanStainedGlass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        Register(BlockId.PurpleStainedGlass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        Register(BlockId.BlueStainedGlass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        Register(BlockId.BrownStainedGlass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        Register(BlockId.GreenStainedGlass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        Register(BlockId.RedStainedGlass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        Register(BlockId.BlackStainedGlass, new BlockProperties(0, 0, true, RenderMethod.Blend));
        // Tinted glass: blocks all light (filter=15) but is see-through
        Register(BlockId.TintedGlass, new BlockProperties(0, 15, true, RenderMethod.Blend));
    }

    /// <summary>
    /// Registers a block with the specified properties.
    /// </summary>
    /// <param name="block">The block ID to register.</param>
    /// <param name="properties">The properties to associate with this block.</param>
    private static void Register(BlockId block, BlockProperties properties)
    {
        ushort id = block.GetId();
        if (id < MaxBlockIds)
        {
            Properties[id] = properties;
        }
    }

    /// <summary>
    /// Registers a standard opaque solid block (no light emission, fully opaque, full cube).
    /// </summary>
    /// <param name="block">The block ID to register.</param>
    private static void RegisterOpaqueSolid(BlockId block) => Register(block, BlockProperties.Default);

    /// <summary>
    /// Registers a leaves block (no light emission, filter=1, full cube for culling, alpha test rendering).
    /// </summary>
    /// <param name="block">The block ID to register.</param>
    private static void RegisterLeaves(BlockId block) => Register(block, new BlockProperties(0, 1, true, RenderMethod.AlphaTest));

    /// <summary>
    /// Gets the properties for a block type. Returns <see cref="BlockProperties.Default"/> for unregistered blocks.
    /// </summary>
    /// <param name="block">The block ID to look up.</param>
    /// <returns>The block's properties.</returns>
    public static BlockProperties GetProperties(BlockId block)
    {
        var id = block.GetId();
        return id < MaxBlockIds ? Properties[id] : BlockProperties.Default;
    }

    /// <summary>
    /// Gets the light value (luminance) emitted by a block (0-15).
    /// </summary>
    /// <param name="block">The block ID to look up.</param>
    /// <returns>The block's light emission value.</returns>
    public static byte GetLightValue(BlockId block) => GetProperties(block).LightValue;

    /// <summary>
    /// Gets the light filter (opacity) for a block (0-15).
    /// 0 = fully transparent to light, 15 = fully opaque.
    /// </summary>
    /// <param name="block">The block ID to look up.</param>
    /// <returns>The block's light filter value.</returns>
    public static byte GetLightFilter(BlockId block) => GetProperties(block).LightFilter;

    /// <summary>
    /// Gets the effective light decay when light passes through a block.
    /// Returns the light filter value, with a minimum of 1 (light always decays by at least 1 per block).
    /// </summary>
    /// <param name="block">The block ID to look up.</param>
    /// <returns>The light decay value (1-15).</returns>
    public static int GetLightDecay(BlockId block)
    {
        var filter = GetLightFilter(block);
        return filter >= 15 ? 15 : Math.Max(1, (int)filter);
    }

    /// <summary>
    /// Checks if a block completely blocks light (filter = 15).
    /// </summary>
    /// <param name="block">The block ID to check.</param>
    /// <returns>True if the block is fully opaque to light.</returns>
    public static bool BlocksLight(BlockId block) => GetLightFilter(block) >= 15;

    /// <summary>
    /// Checks if a block emits light (light value > 0).
    /// </summary>
    /// <param name="block">The block ID to check.</param>
    /// <returns>True if the block emits light.</returns>
    public static bool EmitsLight(BlockId block) => GetLightValue(block) > 0;
}

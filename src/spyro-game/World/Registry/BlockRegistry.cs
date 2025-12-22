using System.Collections.Frozen;

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

/// <summary>
/// Static registry providing fast O(1) lookup for all block properties.
/// </summary>
public static class BlockRegistry
{
    public static FrozenDictionary<BlockId, Block> Blocks { get; private set; }

    static BlockRegistry()
    {
        var builder = new Dictionary<BlockId, Block>();
        RegisterBlocks(builder);
        Blocks = builder.ToFrozenDictionary();
    }

    private static void RegisterBlocks(Dictionary<BlockId, Block> builder)
    {
        // === Special ===
        Register(builder, BlockId.Air, new Block(BlockId.Air) { 
            IsSolid = false, IsOpaque = false, IsReplaceable = true, 
            RenderMethod = RenderMethod.None, Shape = BlockRenderShape.None,
            LightFilter = 0 // Fix: Air must not block light!
        });
        Register(builder, BlockId.Water, new Block(BlockId.Water) {
            IsSolid = false, IsOpaque = false, IsLiquid = true, IsTranslucent = true, IsReplaceable = true,
            LightFilter = 2, RenderMethod = RenderMethod.Blend
        });
        Register(builder, BlockId.Lava, new Block(BlockId.Lava) {
            IsSolid = false, IsOpaque = false, IsLiquid = true, IsEmissive = true, IsReplaceable = true,
            LightValue = 15, LightFilter = 0, RenderMethod = RenderMethod.Blend
        });

        // === Stone Types ===
        Register(builder, BlockId.Stone, new Block(BlockId.Stone) { Hardness = 1.5f });
        Register(builder, BlockId.Bedrock, new Block(BlockId.Bedrock) { Hardness = -1.0f });
        Register(builder, BlockId.Cobblestone, new Block(BlockId.Cobblestone) { Hardness = 2.0f });
        Register(builder, BlockId.MossyCobblestone, new Block(BlockId.MossyCobblestone) { Hardness = 2.0f });
        Register(builder, BlockId.Granite, new Block(BlockId.Granite) { Hardness = 1.5f });

        // === Dirt Types ===
        Register(builder, BlockId.Dirt, new Block(BlockId.Dirt) { Hardness = 0.5f });
        Register(builder, BlockId.Grass, new Block(BlockId.Grass) { Hardness = 0.35f });
        Register(builder, BlockId.GrassSnowy, new Block(BlockId.GrassSnowy) { Hardness = 0.6f });
        Register(builder, BlockId.Podzol, new Block(BlockId.Podzol) { Hardness = 0.5f });
        Register(builder, BlockId.Mycelium, new Block(BlockId.Mycelium) { Hardness = 0.6f });
        Register(builder, BlockId.CoarseDirt, new Block(BlockId.CoarseDirt) { Hardness = 0.5f });
        Register(builder, BlockId.GrassH, new Block(BlockId.GrassH) { Hardness = 0.35f });

        // === Sand Types ===
        Register(builder, BlockId.Sand, new Block(BlockId.Sand) { Hardness = 0.4f });
        Register(builder, BlockId.RedSand, new Block(BlockId.RedSand) { Hardness = 0.5f });
        Register(builder, BlockId.Sandstone, new Block(BlockId.Sandstone) { Hardness = 0.8f });
        Register(builder, BlockId.RedSandstone, new Block(BlockId.RedSandstone) { Hardness = 0.8f });

        // === Gravel/Clay ===
        Register(builder, BlockId.Gravel, new Block(BlockId.Gravel) { Hardness = 0.6f });
        Register(builder, BlockId.Clay, new Block(BlockId.Clay) { Hardness = 0.6f });

        // === Snow/Ice ===
        Register(builder, BlockId.Snow, new Block(BlockId.Snow) {
            IsSolid = true, IsOpaque = true,
            LightValue = 0, LightFilter = 15, RenderMethod = RenderMethod.Opaque
        });
        RegisterOpaqueSolid(builder, BlockId.SnowDirt);
        Register(builder, BlockId.Ice, new Block(BlockId.Ice) {
            IsSolid = true, IsOpaque = false, IsTranslucent = true,
            LightFilter = 1, RenderMethod = RenderMethod.Blend
        });
        Register(builder, BlockId.PackedIce, new Block(BlockId.PackedIce) {
            IsSolid = true, IsOpaque = false, IsTranslucent = true,
            LightFilter = 1, RenderMethod = RenderMethod.Blend
        });
        Register(builder, BlockId.BlueIce, new Block(BlockId.BlueIce) {
            IsSolid = true, IsOpaque = false, IsTranslucent = true,
            LightFilter = 1, RenderMethod = RenderMethod.Blend
        });

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
        RegisterTree(builder, BlockId.OakLog);
        RegisterTree(builder, BlockId.BirchLog);
        RegisterTree(builder, BlockId.SpruceLog);
        RegisterTree(builder, BlockId.JungleLog);

        // === Leaves ===
        RegisterLeaves(builder, BlockId.OakLeaves);
        RegisterLeaves(builder, BlockId.BirchLeaves);
        RegisterLeaves(builder, BlockId.SpruceLeaves);
        RegisterLeaves(builder, BlockId.JungleLeaves);

        // === Light Sources ===
        Register(builder, BlockId.Torch, new Block(BlockId.Torch) {
            IsSolid = false, IsOpaque = false, IsEmissive = true,
            LightValue = 14, LightFilter = 0, RenderMethod = RenderMethod.AlphaTest, Shape = BlockRenderShape.CrossBillboard
        });
        Register(builder, BlockId.WallTorch, new Block(BlockId.WallTorch) {
            IsSolid = false, IsOpaque = false, IsEmissive = true,
            LightValue = 14, LightFilter = 0, RenderMethod = RenderMethod.AlphaTest, Shape = BlockRenderShape.CrossBillboard
        });
        Register(builder, BlockId.SoulTorch, new Block(BlockId.SoulTorch) {
            IsSolid = false, IsOpaque = false, IsEmissive = true,
            LightValue = 10, LightFilter = 0, RenderMethod = RenderMethod.AlphaTest, Shape = BlockRenderShape.CrossBillboard
        });
        Register(builder, BlockId.Glowstone, new Block(BlockId.Glowstone) {
            IsSolid = true, IsOpaque = true, IsEmissive = true,
            LightValue = 15, LightFilter = 15, RenderMethod = RenderMethod.Opaque
        });
        Register(builder, BlockId.SeaLantern, new Block(BlockId.SeaLantern) {
            IsSolid = true, IsOpaque = false, IsTranslucent = true, IsEmissive = true,
            LightValue = 15, LightFilter = 1, RenderMethod = RenderMethod.Blend
        });
        Register(builder, BlockId.Lantern, new Block(BlockId.Lantern) {
            IsSolid = false, IsOpaque = false, IsEmissive = true,
            LightValue = 15, LightFilter = 0, RenderMethod = RenderMethod.AlphaTest, Shape = BlockRenderShape.CrossBillboard
        });
        Register(builder, BlockId.SoulLantern, new Block(BlockId.SoulLantern) {
            IsSolid = false, IsOpaque = false, IsEmissive = true,
            LightValue = 10, LightFilter = 0, RenderMethod = RenderMethod.AlphaTest, Shape = BlockRenderShape.CrossBillboard
        });
        RegisterOpaqueSolid(builder, BlockId.RedstoneLamp);
        Register(builder, BlockId.RedstoneLampOn, new Block(BlockId.RedstoneLampOn) {
            IsSolid = true, IsOpaque = true, IsEmissive = true,
            LightValue = 15, LightFilter = 15, RenderMethod = RenderMethod.Opaque
        });
        Register(builder, BlockId.EndRod, new Block(BlockId.EndRod) {
            IsSolid = false, IsOpaque = false, IsEmissive = true,
            LightValue = 14, LightFilter = 0, RenderMethod = RenderMethod.AlphaTest, Shape = BlockRenderShape.CrossBillboard
        });
        Register(builder, BlockId.Shroomlight, new Block(BlockId.Shroomlight) {
            IsSolid = true, IsOpaque = true, IsEmissive = true,
            LightValue = 15, LightFilter = 15, RenderMethod = RenderMethod.Opaque
        });
        Register(builder, BlockId.JackOLantern, new Block(BlockId.JackOLantern) {
            IsSolid = true, IsOpaque = true, IsEmissive = true,
            LightValue = 15, LightFilter = 15, RenderMethod = RenderMethod.Opaque
        });
        Register(builder, BlockId.Campfire, new Block(BlockId.Campfire) {
            IsSolid = false, IsOpaque = false, IsEmissive = true,
            LightValue = 15, LightFilter = 0, RenderMethod = RenderMethod.AlphaTest
        });
        Register(builder, BlockId.SoulCampfire, new Block(BlockId.SoulCampfire) {
            IsSolid = false, IsOpaque = false, IsEmissive = true,
            LightValue = 10, LightFilter = 0, RenderMethod = RenderMethod.AlphaTest
        });

        // === Glass ===
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
        Register(builder, BlockId.TintedGlass, new Block(BlockId.TintedGlass) {
            IsSolid = true, IsOpaque = false, IsTranslucent = true,
            LightFilter = 15, RenderMethod = RenderMethod.Blend, Hardness = 0.2f
        });

        // === Vegetation ===
        RegisterFlower(builder, BlockId.TallGrass);
        RegisterFlower(builder, BlockId.Poppy);
        RegisterFlower(builder, BlockId.Dandelion);
        RegisterFlower(builder, BlockId.BlueOrchid);
        RegisterFlower(builder, BlockId.Allium);
        RegisterFlower(builder, BlockId.AzureBluet);
        RegisterFlower(builder, BlockId.RedTulip);
        RegisterFlower(builder, BlockId.OrangeTulip);
        RegisterFlower(builder, BlockId.WhiteTulip);
        RegisterFlower(builder, BlockId.PinkTulip);
        RegisterFlower(builder, BlockId.OxeyeDaisy);
        RegisterFlower(builder, BlockId.Cornflower);
        RegisterFlower(builder, BlockId.LilyOfTheValley);
        RegisterFlower(builder, BlockId.WitherRose);
        RegisterFlower(builder, BlockId.Sunflower);
        RegisterFlower(builder, BlockId.Lilac);
        RegisterFlower(builder, BlockId.RoseBush);
        RegisterFlower(builder, BlockId.Peony);
        RegisterFlower(builder, BlockId.DeadBush);
        RegisterFlower(builder, BlockId.SugarCane);
        RegisterFlower(builder, BlockId.Bamboo);
        RegisterFlower(builder, BlockId.GrassPatch);

        Register(builder, BlockId.Cactus, new Block(BlockId.Cactus) {
            IsSolid = true, IsOpaque = true,
            RenderMethod = RenderMethod.AlphaTest,
            Hardness = 0.3f
        });
    }

    private static void Register(Dictionary<BlockId, Block> builder, BlockId blockId, Block block)
        => builder[blockId] = block;

    private static void RegisterOpaqueSolid(Dictionary<BlockId, Block> builder, BlockId blockId)
        => Register(builder, blockId, new Block(blockId)); // Default is Opaque Solid

    private static void RegisterTree(Dictionary<BlockId, Block> builder, BlockId blockId)
        => Register(builder, blockId, new Block(blockId) { IsTree = true });

    private static void RegisterLeaves(Dictionary<BlockId, Block> builder, BlockId blockId)
        => Register(builder, blockId, new Block(blockId) {
            IsSolid = true, IsOpaque = false, IsTree = true,
            LightFilter = 2, RenderMethod = RenderMethod.AlphaTest,
            Hardness = 0.1f
        });

    private static void RegisterGlass(Dictionary<BlockId, Block> builder, BlockId blockId)
        => Register(builder, blockId, new Block(blockId) {
            IsSolid = true, IsOpaque = false, IsTranslucent = true,
            LightFilter = 0, RenderMethod = RenderMethod.Blend,
            Hardness = 0.2f
        });

    private static void RegisterFlower(Dictionary<BlockId, Block> builder, BlockId blockId)
        => Register(builder, blockId, new Block(blockId) {
            IsSolid = false, IsOpaque = false, IsReplaceable = true, IsVegetation = true,
            RenderMethod = RenderMethod.AlphaTest, Shape = BlockRenderShape.CrossBillboard,
            Hardness = 0,
            LightFilter = 0 // Vegetation allows light to pass through
        });

    /// <summary>
    /// Gets the block instance for the given ID.
    /// </summary>
    public static Block Get(BlockId block) => Blocks.TryGetValue(block, out var b) ? b : Blocks[BlockId.Air];

    // === Fast property accessors ===

    /// <summary>Gets the light value (luminance) emitted by a block (0-15).</summary>
    public static byte GetLightValue(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.LightValue : (byte)0;

    /// <summary>Gets the light filter (opacity) for a block (0-15).</summary>
    public static byte GetLightFilter(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.LightFilter : (byte)15;

    /// <summary>Gets the render shape for a block.</summary>
    public static BlockRenderShape GetRenderShape(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.Shape : BlockRenderShape.FullCube;

    /// <summary>Gets the render method for a block.</summary>
    public static RenderMethod GetRenderMethod(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.RenderMethod : RenderMethod.Opaque;

    /// <summary>Gets the effective light decay when light passes through a block.</summary>
    public static int GetLightDecay(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.LightDecay : 15;

    /// <summary>Checks if a block completely blocks light (filter = 15).</summary>
    public static bool BlocksLight(BlockId block) => GetLightFilter(block) >= 15;

    /// <summary>Checks if a block emits light (light value > 0).</summary>
    public static bool EmitsLight(BlockId block) => GetLightValue(block) > 0;

    // === Flag accessors ===

    /// <summary>Returns true if this block is solid.</summary>
    public static bool IsSolid(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.IsSolid : true;

    /// <summary>Returns true if this block is opaque.</summary>
    public static bool IsOpaque(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.IsOpaque : true;

    /// <summary>Returns true if this block is a liquid.</summary>
    public static bool IsLiquid(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.IsLiquid : false;

    /// <summary>Returns true if this block is translucent.</summary>
    public static bool IsTranslucent(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.IsTranslucent : false;

    /// <summary>Returns true if this block is replaceable.</summary>
    public static bool IsReplaceable(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.IsReplaceable : false;

    /// <summary>Returns true if this block is emissive.</summary>
    public static bool IsEmissive(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.IsEmissive : false;

    /// <summary>Returns true if this block is tree.</summary>
    public static bool IsTree(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.IsTree : false;

    /// <summary>Returns true if this block is vegetation.</summary>
    public static bool IsVegetation(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.IsVegetation : false;
}

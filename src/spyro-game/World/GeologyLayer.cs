namespace SpyroGame.World;

/// <summary>
/// Minimal geology layers used for biome texture selection. Independent of in-world block types.
/// </summary>
public enum GeologyLayer : byte
{
    Air = 0,
    Water = 1,
    Surface = 2,
    Subsurface = 3,
    DeepSubsurface = 4,
    UnderwaterSurface = 5,
    UnderwaterSubsurface = 6,
    ShoreLine = 7,
}

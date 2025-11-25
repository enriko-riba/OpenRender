namespace SpyroGame.World;

/// <summary>
/// Defines all block types in the game.
/// Note: this is obsolete and is a mix of texture concepts, geology concepts and gameplay concepts.
/// </summary>
public enum BlockType
{
    None,
    WaterLevel, // The surface of water
    Rock,
    Sand,
    Dirt,
    GrassDirt,  // A dirt block that can grow grass
    Grass,
    Snow,
    BedRock,
    Gravel,
}

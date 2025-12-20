namespace SpyroGame.World.Registry;

/// <summary>
/// An item that places a block when used.
/// </summary>
public class BlockItem(ItemId id, BlockId blockId) : Item(id)
{
    public BlockId BlockId { get; } = blockId;
}

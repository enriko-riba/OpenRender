using SpyroGame.World;

namespace SpyroGame.Shared.State;

public readonly record struct InventoryItemSnapshot(BlockId Block, int Count)
{
    public bool IsEmpty => Count <= 0 || Block.IsAir();
}

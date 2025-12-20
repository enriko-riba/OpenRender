using SpyroGame.World;
using SpyroGame.World.Registry;

namespace SpyroGame.Shared.State;

public readonly record struct InventoryItemSnapshot(ItemId Item, int Count)
{
    public bool IsEmpty => Count <= 0 || Item == ItemId.Air;
}

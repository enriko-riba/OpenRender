using SpyroGame.World.Registry;

namespace SpyroGame.Shared.State;

/// <summary>
/// Snapshot of an inventory item for network serialization.
/// </summary>
public readonly record struct InventoryItemSnapshot(GameObjectId Item, int Count)
{
    /// <summary>Returns true if this slot is empty.</summary>
    public bool IsEmpty => Count <= 0 || Item == GameObjectId.Air;
}

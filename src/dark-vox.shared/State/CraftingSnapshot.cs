using DarkVox.Shared.World.Registry;

namespace DarkVox.Shared.State;

/// <summary>
/// Snapshot of the 2x2 crafting grid plus the server-computed result preview.
/// </summary>
public readonly record struct CraftingSnapshot(
    int Version,
    InventoryItemSnapshot[] Slots,
    InventoryItemSnapshot Result);

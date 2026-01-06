using DarkVox.Shared.World.Registry;
using OpenTK.Mathematics;

namespace DarkVox.Shared.Commands;

public readonly record struct BreakBlockCommand(Vector3i GlobalPosition);

public readonly record struct PlaceBlockCommand(Vector3i GlobalPosition, BlockId Block);

/// <summary>
/// Command sent from client to server to consume food from the selected hotbar slot.
/// </summary>
public readonly record struct EatFoodCommand();

/// <summary>
/// Command sent from client to server to request moving an item between inventory slots.
/// Server validates the move and updates its authoritative inventory state.
/// </summary>
/// <param name="SourceSlot">The slot index to move from (0-35).</param>
/// <param name="TargetSlot">The slot index to move to (0-35).</param>
/// <param name="Count">Number of items to move. Use -1 for entire stack.</param>
public readonly record struct InventoryMoveCommand(int SourceSlot, int TargetSlot, int Count = -1);

public enum ItemContainer : byte
{
	Inventory = 0,
	Crafting = 1,
}

/// <summary>
/// Command sent from client to server to request moving an item between the inventory and the 2x2 crafting grid.
/// Slot order for crafting: 0=TL, 1=TR, 2=BL, 3=BR.
/// </summary>
public readonly record struct ContainerMoveCommand(
	ItemContainer SourceContainer,
	int SourceSlot,
	ItemContainer TargetContainer,
	int TargetSlot,
	int Count = -1);

/// <summary>
/// Command sent from client to server when the player clicks the crafting result slot.
/// Server validates recipe + inventory space and, if valid, consumes materials and grants the result.
/// </summary>
public readonly record struct CraftFromGridCommand();

/// <summary>
/// Command sent from client to server to clear the 2x2 crafting grid.
/// Server attempts to return items to inventory storage; any overflow is dropped into the world.
/// </summary>
public readonly record struct ClearCraftingGridCommand();

/// <summary>
/// Command sent from client to server to return an item from a hotbar slot to storage.
/// Server clears the hotbar slot and calls ReturnItemToStorage to redistribute.
/// </summary>
/// <param name="HotbarSlot">The hotbar slot index (0-8).</param>
public readonly record struct ReturnToStorageCommand(int HotbarSlot);

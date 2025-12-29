using OpenTK.Mathematics;
using SpyroGame.World.Registry;

namespace SpyroGame.Shared.Commands;

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

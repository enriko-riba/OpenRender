using OpenTK.Mathematics;
using SpyroGame.World.Registry;

namespace SpyroGame.Shared.Commands;

public readonly record struct BreakBlockCommand(Vector3i GlobalPosition);

public readonly record struct PlaceBlockCommand(Vector3i GlobalPosition, BlockId Block);

/// <summary>
/// Command sent from client to server to consume food from the selected hotbar slot.
/// </summary>
public readonly record struct EatFoodCommand();

using OpenTK.Mathematics;
using SpyroGame.World.Registry;

namespace SpyroGame.Shared.Commands;

public readonly record struct BreakBlockCommand(Vector3i GlobalPosition);

public readonly record struct PlaceBlockCommand(Vector3i GlobalPosition, BlockId Block);

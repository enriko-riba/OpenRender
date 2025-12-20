using OpenTK.Mathematics;
using SpyroGame.World;
using SpyroGame.World.Registry;

namespace SpyroGame.Shared.State;

public readonly record struct DroppedItemSnapshot(
    int Id,
    ItemId Item,
    int Count,
    Vector3 Position);

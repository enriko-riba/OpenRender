using OpenTK.Mathematics;
using SpyroGame.World.Registry;

namespace SpyroGame.Shared.State;

/// <summary>
/// Snapshot of a dropped item for network serialization.
/// </summary>
public readonly record struct DroppedItemSnapshot(
    int Id,
    GameObjectId Item,
    int Count,
    Vector3 Position);

using OpenTK.Mathematics;
using SpyroGame.World.Registry;

namespace SpyroGame.Components;

/// <summary>
/// Interface for entities that can collect dropped loot.
/// </summary>
public interface ILootCollector
{
    /// <summary>Whether this collector is alive and can collect loot.</summary>
    bool IsAlive { get; }
    /// <summary>Current position of the collector.</summary>
    Vector3 Position { get; }
    /// <summary>Adds an item to the collector's inventory.</summary>
    void AddItem(GameObjectId item, int count);
}

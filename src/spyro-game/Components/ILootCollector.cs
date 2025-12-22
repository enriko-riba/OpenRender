using OpenTK.Mathematics;
using SpyroGame.World.Registry;

namespace SpyroGame.Components;

public interface ILootCollector
{
    bool IsAlive { get; }
    Vector3 Position { get; }
    void AddItem(ItemId item, int count);
}

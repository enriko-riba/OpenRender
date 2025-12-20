using OpenTK.Mathematics;
using SpyroGame.World.Registry;

namespace SpyroGame.World;

public interface ILootCollector
{
    bool IsAlive { get; }
    Vector3 Position { get; }
    void AddItem(ItemId item, int count);
}

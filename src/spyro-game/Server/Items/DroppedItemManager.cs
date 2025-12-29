using OpenTK.Mathematics;
using SpyroGame.Components;
using SpyroGame.Shared.State;
using SpyroGame.World;
using SpyroGame.World.Registry;

namespace SpyroGame.Server.Items;

/// <summary>
/// Represents a dropped item entity in the world.
/// </summary>
public class DroppedItemEntity
{
    public int Id;
    public GameObjectId Item;
    public int Count;
    public Vector3 Position;
    public Vector3 Velocity;
    public float AgeSeconds;
    public bool IsGrounded;
    public const float DespawnTimeSeconds = 300.0f; // 5 minutes
}

/// <summary>
/// Manages dropped items in the world, including physics and pickup.
/// </summary>
public class DroppedItemManager
{
    private readonly List<DroppedItemEntity> items = [];
    private int nextId = 1;

    public void Spawn(GameObjectId item, int count, Vector3 position, Vector3 velocity)
    {
        items.Add(new DroppedItemEntity
        {
            Id = nextId++,
            Item = item,
            Count = count,
            Position = position,
            Velocity = velocity
        });
    }

    public void Tick(double elapsedSeconds, Func<Vector3i, BlockState?> blockProvider, IEnumerable<ILootCollector> players)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            item.AgeSeconds += (float)elapsedSeconds;

            // Pickup Logic
            foreach (var player in players)
            {
                if (!player.IsAlive) continue;
                
                var distSq = Vector3.DistanceSquared(item.Position, player.Position);
                if (distSq <= 2.0f * 2.0f) // 2 block radius
                {
                    player.AddItem(item.Item, item.Count);
                    // Log pickup (would send message to client here)
                    OpenRender.Log.Info($"[Loot] Player picked up {item.Count} {item.Item} from ground");
                    items.RemoveAt(i);
                    goto NextItem; // Break out of player loop and continue outer loop
                }
            }
            
            // Physics
            if (!item.IsGrounded)
            {
                item.Velocity.Y -= 20.0f * (float)elapsedSeconds;
                var nextPos = item.Position + item.Velocity * (float)elapsedSeconds;

                // Simple sweep for collision
                if (item.Velocity.Y < 0)
                {
                    var startY = (int)Math.Floor(item.Position.Y);
                    var endY = (int)Math.Floor(nextPos.Y);

                    var hit = false;
                    // Check from start down to end
                    for (var y = startY; y >= endY; y--)
                    {
                        var blockPos = new Vector3i((int)Math.Floor(item.Position.X), y, (int)Math.Floor(item.Position.Z));
                        var block = blockProvider(blockPos);
                        if (block.HasValue && block.Value.Block.IsSolid())
                        {
                            // Hit ground at 'y'
                            item.IsGrounded = true;
                            item.Velocity = Vector3.Zero;
                            item.Position.Y = y + 1.0f + 0.125f;
                            hit = true;
                            break;
                        }
                    }

                    if (!hit)
                    {
                        item.Position = nextPos;
                    }
                }
                else
                {
                    item.Position = nextPos;
                }
            }
            else
            {
                // Check if ground below is still solid (e.g. block broken)
                var blockBelow = blockProvider(new Vector3i((int)item.Position.X, (int)(item.Position.Y - 0.5f), (int)item.Position.Z));
                if (!blockBelow.HasValue || !blockBelow.Value.Block.IsSolid())
                {
                    item.IsGrounded = false;
                }
            }
            
            if (item.AgeSeconds >= DroppedItemEntity.DespawnTimeSeconds)
            {
                items.RemoveAt(i);
            }

            NextItem:;
        }
    }

    public DroppedItemSnapshot[] GetSnapshots()
    {
        if (items.Count == 0) return Array.Empty<DroppedItemSnapshot>();
        
        var snaps = new DroppedItemSnapshot[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            snaps[i] = new DroppedItemSnapshot(items[i].Id, items[i].Item, items[i].Count, items[i].Position);
        }
        return snaps;
    }
}

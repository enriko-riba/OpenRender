using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using SpyroGame.Components;
using SpyroGame.Server.Mobs;
using SpyroGame.Shared.State;

namespace SpyroGame.Client.Mobs;

internal sealed class MobBlockRenderer(Scene scene)
{
    private readonly Dictionary<MobId, MobSceneNode> nodesById = [];

    /// <summary>
    /// Get render scale from MobDefinition, with fallback for unknown kinds.
    /// </summary>
    private static Vector3 GetMobScale(MobKind kind)
    {
        var def = MobRegistry.Get(kind);
        if (def is not null)
            return new Vector3(def.RenderScaleX, def.RenderScaleY, def.RenderScaleZ);
        return Vector3.One;
    }

    /// <summary>
    /// Get model and animation paths from MobDefinition, with fallback for unknown kinds.
    /// </summary>
    private static (string ModelPath, string? AnimationPath) GetModelAndAnimation(MobKind kind)
    {
        var def = MobRegistry.Get(kind);
        if (def is not null && def.ModelPath is not null)
            return (def.ModelPath, def.AnimationPath);

        // Fallback for unknown mob kinds
        return ("Resources/models/entity/simple_block.json", "Resources/animations/simple_walk.json");
    }

    public void ApplySnapshot(in MobStateSnapshot snapshot)
    {
        var seen = new HashSet<MobId>();

        foreach (var mob in snapshot.Mobs)
        {
            seen.Add(mob.Id);

            if (!nodesById.TryGetValue(mob.Id, out var node))
            {
                var (modelPath, animationPath) = GetModelAndAnimation(mob.Kind);
                node = new MobSceneNode(modelPath, animationPath);
                nodesById[mob.Id] = node;
                var nodeToAdd = node;
                scene.AddAction(() => nodeToAdd.AddToScene(scene));
            }

            var scale = GetMobScale(mob.Kind);
            
            var def = MobRegistry.Get(mob.Kind);
            if (def != null)
            {
                node.HitboxWidth = def.HitboxWidth;
                node.HitboxHeight = def.HitboxHeight;
            }

            node.ApplySnapshot(mob, scale);
        }

        if (nodesById.Count == seen.Count)
        {
            return;
        }

        List<MobId>? toRemove = null;
        foreach (var kvp in nodesById)
        {
            if (!seen.Contains(kvp.Key))
            {
                toRemove ??= [];
                toRemove.Add(kvp.Key);
            }
        }

        if (toRemove is null)
        {
            return;
        }

        foreach (var id in toRemove)
        {
            if (!nodesById.Remove(id, out var node))
            {
                continue;
            }

            var nodeToRemove = node;
            scene.AddAction(() => nodeToRemove.RemoveFromScene(scene));
        }
    }

    public bool Pick(Vector3 origin, Vector3 direction, float maxDistance, out MobId hitMobId, out float hitDistance, out MobKind hitMobKind)
    {
        hitMobId = default;
        hitDistance = float.MaxValue;
        hitMobKind = default;
        var hit = false;

        foreach (var kvp in nodesById)
        {
            var id = kvp.Key;
            var node = kvp.Value;
            
            var pos = node.PhysicsPosition;
            
            // AABB Intersection
            var halfW = node.HitboxWidth * 0.5f;
            var min = pos + new Vector3(-halfW, 0, -halfW);
            var max = pos + new Vector3(halfW, node.HitboxHeight, halfW);

            if (CollisionManager.RayAabbIntersect(origin, direction, min, max, out var t))
            {
                if (t >= 0 && t <= maxDistance && t < hitDistance)
                {
                    hitDistance = t;
                    hitMobId = id;
                    hitMobKind = node.Kind; // Need to expose Kind on MobSceneNode
                    hit = true;
                }
            }
        }

        return hit;
    }
}

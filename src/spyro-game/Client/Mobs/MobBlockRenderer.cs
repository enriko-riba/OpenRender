using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using SpyroGame.Server.Mobs;
using SpyroGame.Shared.State;

namespace SpyroGame.Client.Mobs;

internal sealed class MobBlockRenderer
{
    private readonly Scene scene;

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

    public MobBlockRenderer(Scene scene)
    {
        this.scene = scene;
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
}

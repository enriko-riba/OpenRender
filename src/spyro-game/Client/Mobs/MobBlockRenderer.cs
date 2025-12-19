using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SpyroGame.Shared.State;

namespace SpyroGame.Client.Mobs;

internal sealed class MobBlockRenderer
{
    private readonly Scene scene;

    private readonly Dictionary<MobId, MobSceneNode> nodesById = [];

    private static Vector3 GetMobScale(MobKind kind)
        => kind switch
        {
            // Visual scale - independent of collision hitbox.
            // Cow: narrow (X), tall (Y), long (Z) to match Minecraft proportions.
            MobKind.Cow => new Vector3(0.6f, 1.4f, 1.5f),
            MobKind.Pig => new Vector3(0.9f, 0.9f, 0.9f),
            MobKind.Zombie => new Vector3(0.6f, 1.95f, 0.6f),
            MobKind.Skeleton => new Vector3(0.6f, 1.99f, 0.6f),
            _ => Vector3.One
        };

    public MobBlockRenderer(Scene scene)
    {
        this.scene = scene;
    }

    private static (string ModelPath, string? AnimationPath) GetModelAndAnimation(MobKind kind)
        => kind switch
        {
            MobKind.Cow => ("Resources/models/entity/cow.json", "Resources/animations/cow_walk.json"),

            // TODO: move the rest to JSON models as we author them.
            MobKind.Zombie or MobKind.Skeleton
                => ("Resources/models/entity/simple_block_hostile.json", "Resources/animations/simple_walk.json"),

            _ => ("Resources/models/entity/simple_block.json", "Resources/animations/simple_walk.json"),
        };

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

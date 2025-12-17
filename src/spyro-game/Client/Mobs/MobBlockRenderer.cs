using OpenRender.Core;
using OpenRender.Core.Buffers;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SpyroGame.Shared.State;

namespace SpyroGame.Client.Mobs;

internal sealed class MobBlockRenderer
{
    private readonly Scene scene;
    private readonly Material mobMaterial;

    private readonly Vertex[] mobVerts;
    private readonly uint[] mobIndices;

    private readonly Dictionary<MobId, SceneNode> nodesById = [];

    // Slightly larger than a voxel; still a single "block" representation.
    private static readonly Vector3 MobScale = new(1.25f, 1.25f, 1.25f);
    private static readonly float MobHalfHeight = 0.5f * MobScale.Y;

    // The server currently places mobs at Y = groundTopFace + 0.55.
    // Our cube is centered at the node origin and scaled to MobScale, so its half-height is 0.5*scale.
    // Offset the render node upward so the bottom face rests on the ground.
    private const float ServerMobCenterYOffset = 0.55f;
    private static readonly float RenderYOffset = MobHalfHeight - ServerMobCenterYOffset;

    public MobBlockRenderer(Scene scene)
    {
        this.scene = scene;

        var shader = scene.DefaultShader;
        mobMaterial = Material.Create(
            shader,
            [
                new TextureDescriptor(
                    "Resources/Corey.png",
                    TextureType: TextureType.Diffuse,
                    MagFilter: TextureMagFilter.Nearest,
                    MinFilter: TextureMinFilter.Nearest,
                    TextureWrapS: TextureWrapMode.ClampToEdge,
                    TextureWrapT: TextureWrapMode.ClampToEdge,
                    GenerateMipMap: true)
            ],
            diffuseColor: Vector3.One,
            specularColor: Vector3.One,
            shininess: 0.05f);

        var (verts, indices) = MobBlockGeometry.CreateCoreyBlock();
        mobVerts = verts;
        mobIndices = indices;
    }

    public void ApplySnapshot(in MobStateSnapshot snapshot)
    {
        var seen = new HashSet<MobId>();

        foreach (var mob in snapshot.Mobs)
        {
            seen.Add(mob.Id);

            if (!nodesById.TryGetValue(mob.Id, out var node))
            {
                // OpenRender updates the bounding sphere on the Mesh instance during transform invalidation.
                // So each mob needs its own Mesh instance for correct frustum culling.
                var mesh = new Mesh(VertexDeclarations.VertexPositionNormalTexture, mobVerts, mobIndices);
                node = new SceneNode(mesh, mobMaterial);
                node.SetScale(MobScale);

                //node.ShowBoundingSphere = true;
                node.DisableCulling = false;
                node.IsBatchingAllowed = true;
                nodesById[mob.Id] = node;
                var nodeToAdd = node;
                scene.AddAction(() => scene.AddNode(nodeToAdd));
            }

            // Apply transform directly (safe; renderer reads world matrices each frame).
            node.SetPosition(mob.Position + new Vector3(0, RenderYOffset, 0));
            var yawRadians = MathHelper.DegreesToRadians(mob.YawDegrees);
            node.SetRotation(new Vector3(0, yawRadians, 0));
            node.IsVisible = !mob.Flags.HasFlag(MobSnapshotFlags.Dead);
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
            scene.AddAction(() => scene.RemoveNode(nodeToRemove));
        }
    }
}

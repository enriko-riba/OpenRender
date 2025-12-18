using OpenRender.Core;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SpyroGame.Client.Content;
using SpyroGame.Client.Content.Animations;
using SpyroGame.Client.Content.EntityModels;
using SpyroGame.Shared.State;

namespace SpyroGame.Client.Mobs;

internal sealed class MobSceneNode : SceneNode
{
    private static readonly Mesh DummyMesh = CreateDummyMesh();

    private readonly string modelPath;
    private readonly string? animationPath;

    private readonly Dictionary<string, BoneRuntime> bonesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SceneNode> allNodes = [];
    private readonly List<SceneNode> renderNodes = [];

    private EntityModelDefinition? model;
    private AnimationClip? clip;
    private float animationTimeSeconds;
    private float estimatedSpeed;

    private Vector3 modelBoundsMin;
    private Vector3 modelBoundsMax;
    private bool hasModelBounds;

    private const float MaxYawTurnSpeedRadiansPerSecond = MathHelper.Pi * 2.0f; // 360 deg/sec
    private float currentYawRadians;
    private float targetYawRadians;
    private bool hasYaw;

    public MobSceneNode(string modelPath, string? animationPath)
        : base(DummyMesh, Material.Default)
    {
        this.modelPath = modelPath;
        this.animationPath = animationPath;
        IsVisible = false; // root is transform-only
        DisableCulling = true;
    }

    public void AddToScene(Scene scene)
    {
        scene.AddNode(this);
        if (!allNodes.Contains(this))
        {
            allNodes.Add(this);
        }
        EnsureBuilt(scene);
    }

    public void RemoveFromScene(Scene scene)
    {
        for (var i = allNodes.Count - 1; i >= 0; i--)
        {
            scene.RemoveNode(allNodes[i]);
        }

        allNodes.Clear();
        renderNodes.Clear();
        bonesByName.Clear();
        model = null;
        clip = null;
    }

    public void ApplySnapshot(in MobSnapshot mob, Vector3 scale)
    {
        estimatedSpeed = mob.Velocity.Length;

        // The snapshot position is the kinematic collider position; render should align the model's
        // "feet" (lowest Y in bind pose) to the collider bottom.
        // Also scale the model to match hitbox dimensions, based on its authored bounds.
        var renderScale = scale;
        var renderOffset = Vector3.Zero;
        if (hasModelBounds)
        {
            var modelSize = modelBoundsMax - modelBoundsMin;
            if (modelSize.X > 1e-4f && modelSize.Y > 1e-4f && modelSize.Z > 1e-4f)
            {
                renderScale = new Vector3(
                    scale.X / modelSize.X,
                    scale.Y / modelSize.Y,
                    scale.Z / modelSize.Z);

                // Center X/Z on the collider axis; put lowest Y at ground.
                var centerXZ = new Vector2((modelBoundsMin.X + modelBoundsMax.X) * 0.5f, (modelBoundsMin.Z + modelBoundsMax.Z) * 0.5f);
                renderOffset = new Vector3(
                    -centerXZ.X * renderScale.X,
                    -modelBoundsMin.Y * renderScale.Y,
                    -centerXZ.Y * renderScale.Z);
            }
        }

        SetPosition(mob.Position + renderOffset);
        SetScale(renderScale);

        targetYawRadians = MathHelper.DegreesToRadians(mob.YawDegrees);
        if (!hasYaw)
        {
            currentYawRadians = targetYawRadians;
            hasYaw = true;
            SetRotation(new Vector3(0, currentYawRadians, 0));
        }

        var isDead = mob.Flags.HasFlag(MobSnapshotFlags.Dead);
        for (var i = 0; i < renderNodes.Count; i++)
        {
            renderNodes[i].IsVisible = !isDead;
        }
    }

    public override void OnUpdate(Scene scene, double elapsed)
    {
        // Non-recursive: Scene.UpdateFrame already calls OnUpdate on each node.
        animationTimeSeconds += (float)elapsed;

        if (hasYaw)
        {
            var dt = (float)elapsed;
            var maxStep = MaxYawTurnSpeedRadiansPerSecond * dt;
            var delta = WrapAngleRadians(targetYawRadians - currentYawRadians);
            if (MathF.Abs(delta) <= maxStep)
            {
                currentYawRadians = targetYawRadians;
            }
            else
            {
                currentYawRadians += MathF.Sign(delta) * maxStep;
            }

            SetRotation(new Vector3(0, currentYawRadians, 0));
        }

        if (clip is null || model is null)
        {
            return;
        }

        if (estimatedSpeed < 0.05f)
        {
            // Bind pose when idle.
            foreach (var runtime in bonesByName.Values)
            {
                runtime.Node.SetPosition(runtime.BindLocalPosition);
                runtime.Node.SetRotation(runtime.BindLocalRotationRadians);
            }
            return;
        }

        var duration = Math.Max(0.001f, clip.LengthSeconds);
        var t = clip.Loop ? (animationTimeSeconds % duration) : Math.Min(animationTimeSeconds, duration);

        foreach (var (boneName, boneAnim) in clip.Bones)
        {
            if (!bonesByName.TryGetValue(boneName, out var runtime))
            {
                continue;
            }

            var rot = runtime.BindLocalRotationRadians;
            if (TrySampleVec3(boneAnim.RotationDeg, t, out var rotDeg))
            {
                rot += new Vector3(
                    MathHelper.DegreesToRadians(rotDeg.X),
                    MathHelper.DegreesToRadians(rotDeg.Y),
                    MathHelper.DegreesToRadians(rotDeg.Z));
            }

            var pos = runtime.BindLocalPosition;
            if (TrySampleVec3(boneAnim.Position, t, out var posDelta))
            {
                pos += posDelta;
            }

            runtime.Node.SetPosition(pos);
            runtime.Node.SetRotation(rot);
        }
    }

    private void EnsureBuilt(Scene scene)
    {
        if (model is not null)
        {
            return;
        }

        model = EntityModelLoader.Load(modelPath);

        // Compute model-space bounds in bind pose.
        // Cube origins/sizes are authored in model space; rotations are ignored for bounds (good enough for now).
        hasModelBounds = false;
        modelBoundsMin = new Vector3(float.PositiveInfinity);
        modelBoundsMax = new Vector3(float.NegativeInfinity);
        foreach (var bone in model.Bones)
        {
            foreach (var cube in bone.Cubes)
            {
                var min = cube.Origin;
                var max = cube.Origin + cube.Size;

                modelBoundsMin = Vector3.ComponentMin(modelBoundsMin, min);
                modelBoundsMax = Vector3.ComponentMax(modelBoundsMax, max);
                hasModelBounds = true;
            }
        }

        clip = null;
        if (!string.IsNullOrWhiteSpace(animationPath))
        {
            var animFile = JsonContent.LoadFromResources<AnimationFile>(animationPath!);
            clip = animFile.Animations.FirstOrDefault();
        }

        var atlas = model.Texture.Atlas;
        var atlasFaceTiles = atlas?.FaceTiles;

        var shader = scene.DefaultShader;
        var texDesc = new TextureDescriptor(
            model.Texture.Path,
            TextureType: TextureType.Diffuse,
            MagFilter: TextureMagFilter.Nearest,
            MinFilter: TextureMinFilter.Nearest,
            TextureWrapS: TextureWrapMode.ClampToEdge,
            TextureWrapT: TextureWrapMode.ClampToEdge,
            GenerateMipMap: true);

        var material = Material.Create(
            shader,
            [texDesc],
            diffuseColor: Vector3.One,
            specularColor: Vector3.One,
            shininess: 0.05f);

        // Prefer the real loaded texture size over JSON metadata (keeps UVs correct even if the JSON width/height is stale).
        var diffuseTexture = material.Textures[(int)TextureType.Diffuse];
        var texWidth = diffuseTexture?.Width ?? model.Texture.Width;
        var texHeight = diffuseTexture?.Height ?? model.Texture.Height;
        
        // Precompute model-space pivots so we can compute local offsets.
        var pivotByName = model.Bones.ToDictionary(b => b.Name, b => b.Pivot, StringComparer.OrdinalIgnoreCase);

        // Build bone transform nodes.
        foreach (var bone in model.Bones)
        {
            var parentPivot = Vector3.Zero;
            if (!string.IsNullOrWhiteSpace(bone.Parent) && pivotByName.TryGetValue(bone.Parent!, out var p))
            {
                parentPivot = p;
            }

            var localPos = bone.Pivot - parentPivot;
            var localRot = new Vector3(
                MathHelper.DegreesToRadians(bone.Rotation.X),
                MathHelper.DegreesToRadians(bone.Rotation.Y),
                MathHelper.DegreesToRadians(bone.Rotation.Z));

            var boneNode = new NonRecursiveSceneNode(DummyMesh, Material.Default)
            {
                IsVisible = false,
                DisableCulling = true,
                IsBatchingAllowed = false,
            };
            boneNode.SetPosition(localPos);
            boneNode.SetRotation(localRot);

            bonesByName[bone.Name] = new BoneRuntime(boneNode, localPos, localRot, bone.Pivot);
            allNodes.Add(boneNode);
        }

        // Parent bones under root/bones.
        foreach (var bone in model.Bones)
        {
            var boneNode = bonesByName[bone.Name].Node;

            if (string.IsNullOrWhiteSpace(bone.Parent) || !bonesByName.TryGetValue(bone.Parent!, out var parentRuntime))
            {
                AddChild(boneNode);
            }
            else
            {
                parentRuntime.Node.AddChild(boneNode);
            }
        }

        // Build cube nodes.
        foreach (var bone in model.Bones)
        {
            var runtime = bonesByName[bone.Name];

            foreach (var cube in bone.Cubes)
            {
                EntityModelUvFaces? uvs = null;
                if (cube.Uv?.Faces is not null)
                {
                    uvs = cube.Uv.Faces;
                }
                else if (cube.Uv?.Box is not null)
                {
                    uvs = EntityModelMeshBuilder.BuildBoxUvFaces(cube.Uv.Box);
                }

                var faceTiles = cube.FaceTiles ?? atlasFaceTiles;

                // If neither explicit UVs nor atlas tiles exist, there's nothing we can render for this cube.
                if (uvs is null && faceTiles is null)
                {
                    continue;
                }

                // Cube pivot node (handles cube-local rotation around pivot).
                var pivotNode = new NonRecursiveSceneNode(DummyMesh, Material.Default)
                {
                    IsVisible = false,
                    DisableCulling = true,
                    IsBatchingAllowed = false,
                };

                pivotNode.SetPosition(cube.Pivot - runtime.ModelPivot);
                pivotNode.SetRotation(new Vector3(
                    MathHelper.DegreesToRadians(cube.Rotation.X),
                    MathHelper.DegreesToRadians(cube.Rotation.Y),
                    MathHelper.DegreesToRadians(cube.Rotation.Z)));

                runtime.Node.AddChild(pivotNode);
                allNodes.Add(pivotNode);

                // Renderable cube mesh node.
                // NOTE: each node needs its own Mesh instance because SceneNode.Invalidate mutates mesh.BoundingSphere.
                var mesh = uvs is not null
                    ? EntityModelMeshBuilder.BuildCubeMesh(cube.Size, uvs, texWidth, texHeight)
                    : EntityModelMeshBuilder.BuildCubeMesh(cube.Size, faceTiles!, atlas!);
                var cubeNode = new NonRecursiveSceneNode(mesh, material)
                {
                    IsVisible = true,
                    DisableCulling = false,
                    // NOTE: The scene's DefaultShader is `standard.vert/standard.frag`.
                    // The renderer's batching path uses SSBOs + MultiDrawIndirect, which requires
                    // the `*-batching.*` shader variants. If we allow these cubes to batch, they can
                    // be rendered without the correct per-draw model/texture data (often showing black).
                    IsBatchingAllowed = false,
                };

                cubeNode.SetPosition(cube.Origin - cube.Pivot);
                cubeNode.SetRotation(Vector3.Zero);
                cubeNode.SetScale(Vector3.One);

                pivotNode.AddChild(cubeNode);
                allNodes.Add(cubeNode);
                renderNodes.Add(cubeNode);
            }
        }
    }

    private static float WrapAngleRadians(float radians)
    {
        // Wrap to [-pi, +pi] for shortest-path turns.
        while (radians > MathHelper.Pi) radians -= MathHelper.TwoPi;
        while (radians < -MathHelper.Pi) radians += MathHelper.TwoPi;
        return radians;
    }

    private static bool TrySampleVec3(IReadOnlyList<KeyframeVec3>? keys, float t, out Vector3 value)
    {
        value = default;
        if (keys is null || keys.Count == 0)
        {
            return false;
        }

        if (keys.Count == 1)
        {
            value = keys[0].Value;
            return true;
        }

        KeyframeVec3 a = keys[0];
        KeyframeVec3 b = keys[^1];

        for (var i = 0; i < keys.Count - 1; i++)
        {
            var k0 = keys[i];
            var k1 = keys[i + 1];
            if (t >= k0.TimeSeconds && t <= k1.TimeSeconds)
            {
                a = k0;
                b = k1;
                break;
            }
        }

        var denom = Math.Max(1e-6f, b.TimeSeconds - a.TimeSeconds);
        var alpha = Math.Clamp((t - a.TimeSeconds) / denom, 0.0f, 1.0f);
        value = Vector3.Lerp(a.Value, b.Value, alpha);
        return true;
    }

    private static Mesh CreateDummyMesh()
    {
        // Degenerate triangle; never rendered (nodes using it are IsVisible=false).
        var v = new OpenRender.Core.Rendering.Vertex[3]
        {
            new(new Vector3(0, 0, 0), Vector3.UnitY, Vector2.Zero),
            new(new Vector3(0, 0, 0), Vector3.UnitY, Vector2.Zero),
            new(new Vector3(0, 0, 0), Vector3.UnitY, Vector2.Zero),
        };

        var idx = new uint[] { 0, 1, 2 };
        return new Mesh(OpenRender.Core.Buffers.VertexDeclarations.VertexPositionNormalTexture, v, idx);
    }

    private sealed record BoneRuntime(SceneNode Node, Vector3 BindLocalPosition, Vector3 BindLocalRotationRadians, Vector3 ModelPivot);

    private sealed class NonRecursiveSceneNode : SceneNode
    {
        public NonRecursiveSceneNode(Mesh mesh, Material material) : base(mesh, material) { }

        public override void OnUpdate(Scene scene, double elapsed)
        {
            Update?.Invoke(this, elapsed);
        }
    }
}

using OpenRender.Core;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using DarkVox.Client.Content;
using DarkVox.Client.Content.Animations;
using DarkVox.Client.Content.EntityModels;
using DarkVox.Shared.State;

namespace DarkVox.Client.Mobs;

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

    public float HitboxWidth { get; set; } = 0.6f;
    public float HitboxHeight { get; set; } = 1.8f;
    public MobKind Kind { get; private set; }
    public Vector3 PhysicsPosition { get; private set; }

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
        Kind = mob.Kind;
        PhysicsPosition = mob.Position;
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
        var isHurt = mob.Flags.HasFlag(MobSnapshotFlags.Hurt);
        var tint = isHurt ? new Vector3(1.0f, 0.5f, 0.5f) : Vector3.One;

        for (var i = 0; i < renderNodes.Count; i++)
        {
            renderNodes[i].IsVisible = !isDead;
            renderNodes[i].Material.DiffuseColor = tint;
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
                // Use ToGame for Origin (Z-up -> Y-up), but Identity for Size (Y-up -> Y-up).
                var min = ToGame(cube.Origin);
                var max = min + (cube.Size ?? Vector3.One);

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
        // Use the JSON model's texture size for UV normalization, as the UVs are authored relative to that scale.
        // The actual texture might be higher resolution (e.g. x8), but the layout follows the JSON dimensions.
        var texWidth = model.Texture.Width;
        var texHeight = model.Texture.Height;
        
        // Precompute model-space pivots so we can compute local offsets.
        // NOTE: We swap Y/Z for positions (Z-up -> Y-up) to match game,
        // but we leave rotations as-is (assuming they are authored for the resulting axes).
        var pivotByName = model.Bones.ToDictionary(b => b.Name, b => ToGame(b.Pivot), StringComparer.OrdinalIgnoreCase);

        // Build bone transform nodes.
        foreach (var bone in model.Bones)
        {
            var parentPivot = Vector3.Zero;
            if (!string.IsNullOrWhiteSpace(bone.Parent) && pivotByName.TryGetValue(bone.Parent!, out var p))
            {
                parentPivot = p;
            }

            var bonePivot = ToGame(bone.Pivot);
            var localPos = bonePivot - parentPivot;
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

            bonesByName[bone.Name] = new BoneRuntime(boneNode, localPos, localRot, bonePivot);
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
                var uvs = cube.Uv;
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

                var cubePivot = ToGame(cube.Pivot);
                pivotNode.SetPosition(cubePivot - runtime.ModelPivot);
                pivotNode.SetRotation(new Vector3(
                    MathHelper.DegreesToRadians(cube.Rotation.X),
                    MathHelper.DegreesToRadians(cube.Rotation.Y),
                    MathHelper.DegreesToRadians(cube.Rotation.Z)));

                runtime.Node.AddChild(pivotNode);
                allNodes.Add(pivotNode);

                // Renderable cube mesh node.
                // NOTE: each node needs its own Mesh instance because SceneNode.Invalidate mutates mesh.BoundingSphere.
                // Use Identity for Size (Y-up JSON -> Y-up Mesh).
                var cubeSize = cube.Size ?? Vector3.One;
                var mesh = uvs is not null
                    ? EntityModelMeshBuilder.BuildCubeMesh(cubeSize, uvs, texWidth, texHeight)
                    : EntityModelMeshBuilder.BuildCubeMesh(cubeSize, faceTiles!, atlas!);
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

                var cubeOrigin = ToGame(cube.Origin);
                cubeNode.SetPosition(cubeOrigin - cubePivot);
                cubeNode.SetRotation(Vector3.Zero);
                cubeNode.SetScale(Vector3.One);

                pivotNode.AddChild(cubeNode);
                allNodes.Add(cubeNode);
                renderNodes.Add(cubeNode);
            }
        }
    }

    private static Vector3 ToGame(Vector3 v) => v;

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

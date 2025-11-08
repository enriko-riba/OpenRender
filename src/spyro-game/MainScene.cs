global using AABB = (OpenTK.Mathematics.Vector3i Min, OpenTK.Mathematics.Vector3i Max);
using OpenRender.Components;
using OpenRender.Core;
using OpenRender.Core.Buffers;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenRender.Core.Rendering.Text;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenRender.Text;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SpyroGame.Components;
using SpyroGame.Input;
using SpyroGame.World;

namespace SpyroGame;

internal class MainScene(ITextRenderer textRenderer) : Scene
{
    private const int Padding = 70;

    private static readonly Vector3 textColor = new(0.752f, 0.750f, 0);
    private static readonly Vector3 debugColorBluish = new(0.4f, 0.4f, 1f);

    private readonly KeyboardActionMapper kbdActions = new();
    private DayNightCycle dayNightCycle = default!;
    private Sprite crosshair = default!;
    private Vector2 mouseCenter;
    private Vector2 lastMousePosition;
    private LightUniform dirLight;
    private VoxelWorld world = default!;
    private Player player = default!;
    private SkyBoxSun skyBox = default!;
    private WaterNode waterNode = default!;

    public VoxelWorld World
    {
        set => world = value;
    }

    public override void Load()
    {
        base.Load();
        BackgroundColor = Color4.DarkSlateBlue;

        SceneManager.CursorState = CursorState.Hidden;
        mouseCenter = new Vector2(Width, Height) / 2;
        SceneManager.MousePosition = mouseCenter;

        var startPosition = new Vector3(VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f, 100, VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f);
        //var startPosition = new Vector3(0, -1, 0);
        camera = new CameraFps(startPosition, Width / (float)Height, 0.1f, VoxelHelper.FarPlane)
        {
            MaxFov = 70
        };

        player = new Player(camera, startPosition, world);

        dirLight = new LightUniform()
        {
            Direction = new Vector3(0, -1, 0),
            Ambient = new Vector3(0.35f, 0.35f, 0.35f),
            Diffuse = new Vector3(1),
            Specular = new Vector3(1),
        };
        AddLight(dirLight);

        kbdActions.AddActions([
            new KeyboardAction("exit", [Keys.Escape], SceneManager.Close),
            new KeyboardAction("full screen toggle", [Keys.F11], FullScreenToggle),
            new KeyboardAction("up", [Keys.LeftShift], ()=> player.Position += Vector3.UnitY, false),
            new KeyboardAction("down", [Keys.LeftControl], ()=> player.Position -= Vector3.UnitY, false),
            new KeyboardAction("wireframe", [Keys.F1], WireframeToggle),
            new KeyboardAction("rebuild atlas", [Keys.F5], () => {
                try { var arr = world.SurroundingChunkIndices.ToArray(); world.ChunkInitializer?.ProcessChunkData(arr); } catch { }
            }),
        ]);

        //  create 2D crosshair
        var shader = new Shader("Shaders/sprite.vert", "Shaders/sprite.frag");
        var material = Material.Create(shader,
            [
                new TextureDescriptor("Resources/crosshair.png",
                    TextureType: TextureType.Diffuse,
                    MagFilter: TextureMagFilter.Nearest,
                    MinFilter: TextureMinFilter.Nearest,
                    TextureWrapS: TextureWrapMode.ClampToEdge,
                    TextureWrapT: TextureWrapMode.ClampToEdge,
                    GenerateMipMap: true)
            ]
        );
        var (v, i) = GeometryHelper.Create2dQuad();
        var mesh = new Mesh(Vertex2D.VertexDeclaration, v, i);
        crosshair = new Sprite(mesh, material)
        {
            Tint = Color4.LightPink
        };
        AddNode(crosshair);
        
        SetupScene();

        //OpenRender.Log.MinimumLevel = OpenRender.Log.LevelWarn;
    }

    public override void RenderFrame(double elapsedSeconds)
    {
        base.RenderFrame(elapsedSeconds);

        var lineY = 4;
        void writeLine(string text, in Vector3 color)
        {
            textRenderer.Render(text.PadRight(Padding, ' '), 20, 5, lineY, color);
            lineY += 20;
        }
        writeLine("", textColor);

        var fpsText = $"avg frame duration: {SceneManager.AvgFrameDuration:G3} ms, fps: {SceneManager.Fps:N0}";
        writeLine(fpsText, textColor);

        var text = $"World: size {VoxelHelper.WorldChunksXZ:N0}, chunk size {VoxelHelper.ChunkSideSize}, max chunk distance {VoxelHelper.MaxDistanceInChunks}";
        writeLine(text, textColor);

        var surroundingChunks = world.SurroundingChunkIndices.Count;
        text = $"Chunks: {VoxelHelper.TotalChunks:N0}, surrounding {surroundingChunks}, loaded {world.LoadedChunksCount}";
        writeLine(text, textColor);

        var inFrustumGpu = world.ChunkRenderer?.VisibleDraws ?? world.ChunksInFrustum;
        var culled = surroundingChunks - inFrustumGpu;
        text = $"In frustum: {inFrustumGpu:N0}  Culled: {culled:N0}  (of {surroundingChunks:N0})";
        writeLine(text, textColor);

        text = $"Blocks rendered {world.ChunkRenderer.RenderedBlocks:N0}, render data {world.ChunkRenderer.ChunkRenderDataLength}";
        writeLine(text, textColor);

        // Atlas/MDI debug: show mapping for the camera chunk if GPU compaction is active
        if (world.CompactedChunkIndices is not null && world.CompactedBases is not null && world.CompactedCounts is not null && world.CompactedAtlasSSBO != 0)
        {
            var camPosDbg = camera.Position;
            var camCx = (int)((camPosDbg.X + 0.5f) / VoxelHelper.ChunkSideSize);
            var camCz = (int)((camPosDbg.Z + 0.5f) / VoxelHelper.ChunkSideSize);
            var camIdx = camCx + camCz * VoxelHelper.WorldChunksXZ;
            var ind = Array.IndexOf(world.CompactedChunkIndices, camIdx);
            if (ind >= 0)
            {
                var b = world.CompactedBases[ind];
                var c = world.CompactedCounts[ind];
                writeLine($"MDI cameraChunk draw={ind} base={b} count={c}", debugColorBluish);
            }
            else
            {
                writeLine($"MDI cameraChunk not in draw list", debugColorBluish);
            }
        }

        text = $"player position {player.Position.ToString("N2")}{(player.IsGhostMode ? ", ghost mode" : "")}";
        writeLine(text, debugColorBluish);
        if (player.PickedBlock is not null)
        {
            text = $"picked block: {player.PickedBlock}";
            writeLine(text, debugColorBluish);
        }

        //  show chunk index and position
        if (player?.CurrentBlockBellow is not null)
        {
            text = $"block bellow: {player.CurrentBlockBellow}";
            writeLine(text, debugColorBluish);
        }

        // Extra debug: show GPU ground Y at player feet and delta to feet
        if (player?.CurrentChunk is not null)
        {
            var ch = player.CurrentChunk;
            var clx = (int)MathF.Floor(player.ChunkLocalPosition.X);
            var clz = (int)MathF.Floor(player.ChunkLocalPosition.Z);
            clx = Math.Clamp(clx, 0, VoxelHelper.ChunkSideSize - 1);
            clz = Math.Clamp(clz, 0, VoxelHelper.ChunkSideSize - 1);
            var hLocal = ch.GetTerrainHeightAt(clx, clz);
            var hWorld = ch.Position.Y + hLocal;
            var footY = player.Position.Y;
            var delta = footY - hWorld;
            var gpuText = $"GPU groundY: {hWorld:N2} (local {hLocal})  Δfoot-ground: {delta:N2}  HasGpuCols: {ch.HasGpuColumns}";
            writeLine(gpuText, debugColorBluish);
        }

        // Collision/terrain probe logging is handled inside VoxelWorld.GetBlockByPositionGlobalSafe()

        text = $"time: {dayNightCycle.TimeOfDay:hh\\:mm}";
        writeLine(text, Vector3.UnitY);
        writeLine("", textColor);

        text = $"isJumping: {player.IsJumping}, isGrounded: {player.IsGrounded}, velocity: {player.VelocityY}";
        writeLine(text, Vector3.UnitY);


        writeLine("", textColor);
        //text = $"sun direction: {dayNightCycle.DirLight.Direction}, ambient: {dayNightCycle.DirLight.Ambient}";
        //textRenderer.Render(text, 20, 5, 120, new(0.5f));

        //text = $"camera direction: {camera!.Front}";
        //textRenderer.Render(text, 20, 5, 140, new(0.5f));
        var chunk = player.CurrentChunk;
        if (chunk is not null)
        {
            var idx = (int)player.ChunkLocalPosition.X + (int)player.ChunkLocalPosition.Z * VoxelHelper.ChunkSideSize;
            var info = chunk.Columns[idx];
            var ciText = $"Biome: {info.Biome}";
            writeLine(ciText, debugColorBluish);
            ciText = $"C:{info.Continentalness:F2}  E:{info.Erosion:F2}";
            writeLine(ciText, debugColorBluish);
            ciText = $"T:{info.Temperature:F2}  H:{info.Humidity:F2}";
            writeLine(ciText, debugColorBluish);
            ciText = $"Height:{info.Height01:F3}";
            writeLine(ciText, debugColorBluish);
        }
    }

    public override void UpdateFrame(double elapsedSeconds)
    {
        base.UpdateFrame(elapsedSeconds);

        dayNightCycle.Tick(elapsedSeconds);
        // Drive terrain streaming based on camera movement and complete GPU batches
        try { world.UpdateStreamingFromCamera(); } catch { }
        try { world.ProcessGpuStreamingOnGlThread(); } catch { }
        // Per-frame visibility update using the current camera frustum (authoritative culling)
        world.UpdateVisibilityFromCamera(camera!);
        player.Update(elapsedSeconds, SceneManager.KeyboardState);
        kbdActions.Update(SceneManager.KeyboardState);

        if (SceneManager.IsFocused)
        {
            //  mouse movement
            var mousePos = SceneManager.MouseState.Position;
            var delta = lastMousePosition - mousePos;
            lastMousePosition = mousePos;
            if (delta.LengthSquared > 0)
            {
                player.AddRotation(delta.X, delta.Y, 0);

                if (mousePos.X < 100 ||
                    mousePos.Y < 100 ||
                    mousePos.X > Width - 100 ||
                    mousePos.Y > Height - 100)
                {
                    SceneManager.MousePosition = mouseCenter;
                    lastMousePosition = mouseCenter;
                }
            }

            if (SceneManager.MouseState.IsButtonPressed(MouseButton.Left))
            {
                player.BreakBlock();
            }
        }
        else
        {
            // When not focused, just update the last position to prevent a jump when focus is regained.
            lastMousePosition = SceneManager.MouseState.Position;
        }
    }


    public override void OnMouseWheel(MouseWheelEventArgs e)
    {
        const float Sensitivity = 3.5f;
        camera!.Fov -= e.OffsetY * Sensitivity;
    }

    public override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        textRenderer.Projection = TextRenderer.CreateTextRenderingProjection(Width, Height);
        var clientSize = new Vector2(Width, Height);
        crosshair.SetScale(1);
        crosshair.SetPosition((clientSize - crosshair.Size) / 2);
        crosshair.Pivot = new(0.0f, 1.0f);

        mouseCenter = clientSize / 2;
    }

    public override void Close() => world.Close();

    private void FullScreenToggle() => SceneManager.WindowState = SceneManager.WindowState == WindowState.Fullscreen ? WindowState.Normal : WindowState.Fullscreen;

    private void WireframeToggle() => ShowBoundingSphere = !ShowBoundingSphere;

    private void SetupScene()
    {
        dayNightCycle = new(this);
        dayNightCycle.Tick(0);

        skyBox = SkyBoxSun.Create(dayNightCycle);
        AddNode(skyBox);

        AddNode(world.ChunkRenderer);
        world.Camera = camera!;
        camera!.Invalidate();

        // Ensure initial GPU publish so terrain is visible immediately when entering MainScene
        try
        {
            world.EnsureChunkInitializer();
            // Build full surrounding set around current camera tile
            var camPos = camera.Position;
            var cameraChunkX = (int)((camPos.X + 0.5f) / VoxelHelper.ChunkSideSize);
            var cameraChunkZ = (int)((camPos.Z + 0.5f) / VoxelHelper.ChunkSideSize);
            var side = VoxelHelper.WorldChunksXZ;
            var r = VoxelHelper.MaxDistanceInChunks;
            var minX = Math.Max(0, cameraChunkX - r);
            var maxX = Math.Min(side - 1, cameraChunkX + r);
            var minZ = Math.Max(0, cameraChunkZ - r);
            var maxZ = Math.Min(side - 1, cameraChunkZ + r);
            var list = new System.Collections.Generic.List<int>((2 * r + 1) * (2 * r + 1));
            for (int z = minZ; z <= maxZ; z++)
                for (int x = minX; x <= maxX; x++)
                    list.Add(x + z * side);
            world.ChunkInitializer?.ProcessChunkData(list.ToArray());
        }
        catch { }

        waterNode = WaterNode.Create(dayNightCycle);
        AddNode(waterNode);
    }
}

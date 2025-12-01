using OpenRender;
using OpenRender.Components;
using OpenRender.Core;
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
using SpyroGame.World;

namespace SpyroGame;

/// <summary>
/// Production game scene with procedural voxel terrain.
/// Features full physics, collision detection, and chunk streaming.
/// </summary>
internal class GameScene : Scene
{
    private static readonly Vector3 textColor = new(1, 1, 1); // White for better readability
    private static readonly Vector3 highlightColor = new(0.3f, 0.8f, 0.3f);

    private readonly ITextRenderer textRenderer;
    private VoxelWorld world = default!;
    private Player player = default!;

    private ChunkStreamingManager? streamingManager;
    private VoxelTerrainRenderer? terrainRenderer;
    private BlockPickingService? blockPickingService;

    // Frustum culling throttling
    private double lastCullingTime = -1.0;
    private const double CullingIntervalSeconds = 0.166; // ~6 times per second (166ms)
    private const int GameplayPrefetchMarginChunks = 2;

    private Vector2 mouseCenter;
    private Vector2 lastMousePosition;
    private bool wasLeftButtonDown;
    private Sprite crosshair = default!;
    private DayNightCycle dayNightCycle = default!;
    private SkyBoxSun skyBox = default!;
    //private WaterNode waterNode = default!;

    public GameScene(ITextRenderer textRenderer)
    {
        this.textRenderer = textRenderer;
        Name = "GameScene";
    }

    public VoxelWorld World
    {
        set => world = value;
    }

    /// <summary>
    /// Called by TerrainLoadingScene to pass initialized terrain streaming components.
    /// </summary>
    public void SetupTerrainSystem(ChunkStreamingManager streamingMgr, VoxelTerrainRenderer renderer, Vector3? spawnPosition = null)
    {
        streamingManager = streamingMgr;
        terrainRenderer = renderer;
        world = streamingManager.World; // Sync world reference

        // Add renderer to scene
        AddNode(terrainRenderer);

        // Ensure camera is initialized before creating player
        EnsureCameraInitialized();

        // Calculate center of world for spawn if not provided
        var centerPos = spawnPosition ?? new Vector3(
            VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f,
            230,
            VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f
        );

        // Initialize player with world at center position
        player = new Player(camera!, centerPos, world, streamingManager);

        // Initialize block picking service
        blockPickingService = new BlockPickingService(streamingManager);

        // Assign service to player
        player.BlockPickingService = blockPickingService;

        // Restore full load distance for gameplay
        streamingManager.LoadDistance = VoxelHelper.MaxDistanceInChunks;
        streamingManager.SetPrefetchMargin(GameplayPrefetchMarginChunks);

        Log.Info("GameScene: terrain components configured");
    }

    private void EnsureCameraInitialized()
    {
        if (camera != null) return;

        // Setup camera - FPS camera
        // CRITICAL: Must match TerrainLoadingScene spawn position!
        // Both must use the same calculation: WorldChunksXZ * ChunkSideSize / 2
        var startPos = new Vector3(
            VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f,  // = 16 * 600 / 2 = 4800
            100,
            VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f   // = 16 * 600 / 2 = 4800
        );

        camera = new CameraFps(startPos, Width / (float)Height, 0.1f, VoxelHelper.FarPlane)
        {
            MaxFov = 70
        };
    }

    public override void Load()
    {
        base.Load();
        BackgroundColor = Color4.CornflowerBlue;

        // CRITICAL: Disable polygon/line smoothing to prevent visible triangle edges
        // The base Scene.Load() enables these, but they cause visible lines on voxel terrain
        GL.Disable(EnableCap.PolygonSmooth);
        GL.Disable(EnableCap.LineSmooth);

        // Initialize block picking service NOW (after world and terrainRenderer are set)
        if (terrainRenderer != null && world != null && blockPickingService == null)
        {
            blockPickingService = new BlockPickingService(terrainRenderer);
            Log.Info("GameScene: Block picking service initialized");
        }

        // Hide mouse cursor
        SceneManager.CursorState = CursorState.Hidden;

        // Enable backface culling
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        GL.FrontFace(FrontFaceDirection.Ccw);

        EnsureCameraInitialized();

        // Player is initialized in SetupCpuTerrain if coming from loading screen
        // If not (e.g. direct load), initialize here
        if (player == null)
        {
            // Re-calculate startPos since it's local to EnsureCameraInitialized now
            var startPos = new Vector3(
                VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f,
                100,
                VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f
            );
            player = new Player(camera!, startPos, world, streamingManager);
        }
        else if (streamingManager != null)
        {
            // Ensure existing player has the streaming manager
            player.StreamingManager = streamingManager;
        }

        if (player != null && blockPickingService != null)
        {
            player.BlockPickingService = blockPickingService;
        }

        //player.IsGhostMode = true; // Start in ghost mode for easy exploration

        // Mouse centering for FPS controls
        mouseCenter = new Vector2(Width, Height) / 2;
        SceneManager.MousePosition = mouseCenter;
        lastMousePosition = mouseCenter;

        // Create crosshair
        var crosshairShader = new Shader("Shaders/sprite.vert", "Shaders/sprite.frag");
        var crosshairMaterial = Material.Create(crosshairShader,
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
        var (crosshairVerts, crosshairIndices) = GeometryHelper.Create2dQuad();
        var crosshairMesh = new Mesh(Vertex2D.VertexDeclaration, crosshairVerts, crosshairIndices);
        crosshair = new Sprite(crosshairMesh, crosshairMaterial)
        {
            Tint = Color4.LightPink
        };
        AddNode(crosshair);

        var clientSize = new Vector2(Width, Height);
        crosshair.SetScale(1);
        crosshair.SetPosition((clientSize - crosshair.Size) / 2);
        crosshair.Pivot = new(0.0f, 1.0f);

        // Setup day/night cycle and lighting
        dayNightCycle = new DayNightCycle(this);
        dayNightCycle.Tick(0);

        // dirLight field no longer needed - managed by DayNightCycle
        // (Remove the manual AddLight call below)

        // Create skybox
        skyBox = SkyBoxSun.Create(dayNightCycle);
        AddNode(skyBox);

        // Add terrain renderer
        if (terrainRenderer != null)
        {
            // Use GPU terrain renderer from loading scene
            // Note: Already added in SetupCpuTerrain, but check just in case
            if (terrainRenderer.Scene == null)
            {
                AddNode(terrainRenderer);
            }
            Log.Info("GameScene: Using GPU terrain renderer");
        }
        else
        {
            // Fallback to old renderer (shouldn't happen in normal flow)
            // AddNode(world.ChunkRenderer);
            Log.Warn("GameScene: No GPU terrain renderer, using fallback");
        }

        world.Camera = camera!;
        camera!.Invalidate();

        Log.Info($"GameScene: Loaded with procedural terrain");
    }

    public override void UpdateFrame(double elapsedSeconds)
    {
        if (!SceneManager.IsFocused)
        {
            lastMousePosition = SceneManager.MouseState.Position;
            base.UpdateFrame(elapsedSeconds);
            return;
        }

        // Exit on Esc key
        if (SceneManager.KeyboardState.IsKeyDown(Keys.Escape))
        {
            SceneManager.Close();
            return;
        }

        // Toggle Biome Debug (F3)
        if (SceneManager.KeyboardState.IsKeyPressed(Keys.F3))
        {
            if (terrainRenderer != null)
            {
                terrainRenderer.ShowBiomes = !terrainRenderer.ShowBiomes;
                Log.Info($"Biome Debug Mode: {terrainRenderer.ShowBiomes}");
            }
        }

        // Toggle Debug Wireframe (F5)
        if (SceneManager.KeyboardState.IsKeyPressed(Keys.F5))
        {
            if (terrainRenderer != null)
            {
                terrainRenderer.DebugWireframe = !terrainRenderer.DebugWireframe;
                Log.Info($"Debug Wireframe: {(terrainRenderer.DebugWireframe ? "ENABLED" : "DISABLED")}");
            }
        }

        if (SceneManager.KeyboardState.IsKeyPressed(Keys.F6) && streamingManager != null)
        {
            streamingManager.FlushVoxelCache("F6 hotkey");
        }

        // Update day/night cycle
        dayNightCycle.Tick(elapsedSeconds);

        // Handle block breaking
        var mouseState = SceneManager.MouseState;
        var isLeftButtonDown = mouseState.IsButtonDown(MouseButton.Left);
        if (isLeftButtonDown && !wasLeftButtonDown)
        {
            // Log.Debug("Left mouse button clicked");
            // player.BreakBlock(); // Handled by Player.Update
        }
        wasLeftButtonDown = isLeftButtonDown;

        // Execute GPU frustum culling (throttled to ~6 times per second)
        if (streamingManager != null && terrainRenderer != null && camera != null)
        {
            var currentTime = SceneManager.Time;
            if ((currentTime - lastCullingTime) >= CullingIntervalSeconds)
            {
                lastCullingTime = currentTime;

                // Generate surrounding chunk indices based on camera position
                var chunkIndices = GenerateSurroundingChunkIndices();

                // Execute GPU frustum culling
                var visibilityFlags = streamingManager.ExecuteFrustumCulling(camera, chunkIndices);
                terrainRenderer.SetVisibilityFlags(visibilityFlags, chunkIndices);
            }
        }

        // Update terrain streaming (if using NEW GPU system)
        if (streamingManager != null && camera != null)
        {
            // Call streaming manager every frame to handle chunk loading/unloading
            streamingManager.Update(camera.Position);
        }


        // Update visibility
        // world.UpdateVisibilityFromCamera(camera!);

        // Check if camera is underwater (for visual effects)
        if (camera != null)
        {
            var camPos = camera.Position;
            var blockAtCam = world.GetBlockByPositionGlobalSafe((int)camPos.X, (int)camPos.Y, (int)camPos.Z);
            var isUnderwater = blockAtCam.HasValue && blockAtCam.Value.Block.IsWater();

            terrainRenderer?.IsCameraUnderwater = isUnderwater;
            skyBox?.IsCameraUnderwater = isUnderwater;
        }

        // Update player (handles physics, collision, and WASD movement input)
        player.Update(elapsedSeconds, SceneManager.KeyboardState, SceneManager.MouseState);

        // Update block below player - find highest solid block at player X/Z regardless of mode
        UpdateBlockBelow();

        // Update block picking service (decoupled from rendering)
        // CRITICAL: Update picking AFTER player movement/camera update but BEFORE interaction
        blockPickingService?.Update(
            currentTime: SceneManager.Time,
            camera: camera!,
            maxDistance: 5.0f
        );

        // Handle interactions (Break/Place) with fresh picking data
        if (SceneManager.MouseState.IsButtonPressed(MouseButton.Left))
        {
            player.BreakBlock();
        }
        if (SceneManager.MouseState.IsButtonPressed(MouseButton.Right))
        {
            player.PlaceBlock();
        }

        // Update underwater state
        if (terrainRenderer != null)
        {
            // Hardcoded water level matching terrain-common.glsl (35) + 1 for surface
            const float waterSurfaceLevel = 36.0f;
            terrainRenderer.IsCameraUnderwater = camera!.Position.Y < waterSurfaceLevel;
        }

        // Manual vertical movement (Shift=up, Ctrl=down) - resets accumulated Y velocity
        const float verticalSpeed = 25.0f; // blocks per second (increased from 15.0)
        if (SceneManager.KeyboardState.IsKeyDown(Keys.LeftShift) || SceneManager.KeyboardState.IsKeyDown(Keys.RightShift))
        {
            var pos = player.Position;
            pos.Y += verticalSpeed * (float)elapsedSeconds;
            player.Position = pos;
            // Reset Y velocity when manually moving vertically
            typeof(Player).GetField("velocityY", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(player, 0f);
        }
        if (SceneManager.KeyboardState.IsKeyDown(Keys.LeftControl) || SceneManager.KeyboardState.IsKeyDown(Keys.RightControl))
        {
            var pos = player.Position;
            pos.Y -= verticalSpeed * (float)elapsedSeconds;
            player.Position = pos;
            // Reset Y velocity when manually moving vertically
            typeof(Player).GetField("velocityY", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(player, 0f);
        }

        // Handle mouse rotation
        var mousePos = SceneManager.MouseState.Position;
        var delta = lastMousePosition - mousePos;
        lastMousePosition = mousePos;
        if (delta.LengthSquared > 0)
        {
            const float mouseSensitivity = 0.05f;
            player.AddRotation(delta.X * mouseSensitivity, delta.Y * mouseSensitivity, 0);

            // Re-center mouse when near edge
            if (mousePos.X < 100 || mousePos.Y < 100 ||
                mousePos.X > Width - 100 || mousePos.Y > Height - 100)
            {
                SceneManager.MousePosition = mouseCenter;
                lastMousePosition = mouseCenter;
            }
        }

        // Call base which updates all nodes
        base.UpdateFrame(elapsedSeconds);
    }

    public override void RenderFrame(double elapsedSeconds)
    {
        base.RenderFrame(elapsedSeconds);

        RenderUI();
    }

    /// <summary>
    /// Updates the block below the player by finding the highest solid block at the player's X/Z coordinates.
    /// Works in both ghost mode and physics mode.
    /// </summary>
    private void UpdateBlockBelow()
    {
        if (streamingManager == null) return;

        var playerPos = player.Position;
        var blockX = (int)playerPos.X;
        var blockZ = (int)playerPos.Z;

        // Search downward from player position to find highest solid block
        BlockState? highestSolid = null;
        for (var y = (int)playerPos.Y; y >= 0; y--)
        {
            var block = world.GetBlockByPositionGlobalSafe(blockX, y, blockZ);
            if (block.HasValue && block.Value.IsSolid)
            {
                highestSolid = block.Value;
                break;
            }
        }

        player.CurrentBlockBellow = highestSolid;
    }

    private void RenderUI()
    {
        var lineY = 20;
        const int lineHeight = 25;

        void WriteLine(string text, Vector3 color)
        {
            textRenderer.Render(text, 22, 20, lineY, color);
            lineY += lineHeight;
        }

        // Header
        WriteLine("SPYRO GAME - PROCEDURAL TERRAIN", highlightColor);
        WriteLine("", textColor);

        // Performance
        WriteLine($"FPS: {SceneManager.Fps:F0} ({SceneManager.AvgFrameDuration:F2}ms)", textColor);
        WriteLine($"View Distance: {VoxelHelper.MaxDistanceInChunks} chunks", textColor);
        WriteLine("", textColor);

        // Time
        WriteLine($"Time: {dayNightCycle.TimeOfDay:hh\\:mm\\:ss}", new Vector3(1, 1, 0));
        WriteLine("", textColor);

        // Chunk Stats (FIXED - Show actual generated chunks, not theoretical surrounding)
        int readyChunks;
        int queuedChunks;
        int targetChunks;

        if (streamingManager != null)
        {
            var (_, pending, generating, ready) = streamingManager.GetStats();
            readyChunks = ready;
            queuedChunks = pending + generating;

            var (target, _, _, _, _) = streamingManager.GetStreamingProgress();
            targetChunks = target;
        }
        else
        {
            // Fallback to VoxelWorld (old system)
            readyChunks = world.LoadedChunksCount;
            queuedChunks = 0;
            targetChunks = world.LoadedChunksCount;
        }

        WriteLine("Chunks:", highlightColor);
        WriteLine($"  GPU Ready: {readyChunks:N0} | Queued: {queuedChunks:N0} | Target: {targetChunks:N0}", textColor);

        // Get visibility stats from terrain renderer (GPU-based)
        if (terrainRenderer != null && streamingManager != null)
        {
            WriteLine($"  Visible: {streamingManager.StatVisibleChunks:N0}", textColor);
            WriteLine($"  Frustum Culled: {streamingManager.StatFrustumCulledChunks:N0}", textColor);
            WriteLine($"  Indices: {streamingManager.StatVisibleIndices:N0} / {streamingManager.StatTotalIndices:N0}", textColor);
        }
        WriteLine("", textColor);

        // Rendering Stats
        WriteLine("Rendering:", highlightColor);
        if (terrainRenderer != null)
        {
            WriteLine($"  Visible Chunks: {terrainRenderer.VisibleDraws:N0}", textColor);
            WriteLine($"  Capacity: {terrainRenderer.RenderedBlocks:N0}", textColor);
            WriteLine($"  Draw Calls: {terrainRenderer.DrawCallCount}", textColor);
        }
        WriteLine("", textColor);

        // Player Stats
        WriteLine("Player:", highlightColor);

        var pLocal = player.ChunkLocalPosition;
        var pChunk = player.CurrentChunk?.Index ?? -1;
        var pGlobal = player.Position;

        WriteLine($"  Position: ({(int)pLocal.X},{(int)pLocal.Y},{(int)pLocal.Z})@{pChunk} : ({pGlobal.X:F1},{pGlobal.Y:F1},{pGlobal.Z:F1})", textColor);

        // Block Below - Always displayed, shows highest solid block at player X/Z
        if (player.CurrentBlockBellow.HasValue)
        {
            var bb = player.CurrentBlockBellow.Value;
            var bbGlobal = bb.GlobalPosition;
            var bbChunk = bb.ChunkIndex;
            var bbChunkOrigin = VoxelHelper.GetChunkPositionGlobal(bbChunk);
            var bbLocal = new Vector3(bbGlobal.X - bbChunkOrigin.X, bbGlobal.Y - bbChunkOrigin.Y, bbGlobal.Z - bbChunkOrigin.Z);

            // Get biome name for this block
            var biomeName = GetBiomeNameForBlock(bb);

            WriteLine($"  Block Below: ({(int)bbLocal.X}, {(int)bbLocal.Y}, {(int)bbLocal.Z})@{bbChunk} {bb.Block} | {biomeName}", textColor);
        }
        else
        {
            WriteLine($"  Block Below: n/a", textColor);
        }

        var modeStr = player.IsGhostMode ? "Ghost" : "Phys";
        var groundedStr = player.IsGrounded ? "Grnd" : "Air";
        var jumpStr = player.IsJumping ? "Jump" : "";
        WriteLine($"  {modeStr} | {groundedStr} {jumpStr} | VelY: {player.VelocityY:F2}", textColor);

        // Climate data for block below player - show RAW CELL values (not interpolated)
        // This matches what biome selection actually uses
        if (player.CurrentBlockBellow.HasValue && streamingManager != null)
        {
            var bb = player.CurrentBlockBellow.Value;
            var worldX = (int)bb.GlobalPosition.X;
            var worldZ = (int)bb.GlobalPosition.Z;
            var climate = streamingManager.GetCellClimateAtWorldPos(worldX, worldZ);
            if (climate.HasValue)
            {
                var c = climate.Value;
                // Show cell coordinates (4x4 grid per chunk)
                var localX = ((worldX % VoxelHelper.ChunkSideSize) + VoxelHelper.ChunkSideSize) % VoxelHelper.ChunkSideSize;
                var localZ = ((worldZ % VoxelHelper.ChunkSideSize) + VoxelHelper.ChunkSideSize) % VoxelHelper.ChunkSideSize;
                var cellX = localX / ChunkBiomeData.BlocksPerCell;
                var cellZ = localZ / ChunkBiomeData.BlocksPerCell;
                WriteLine($"  C:{c.C:F3} T:{c.T:F3} H:{c.H:F2} E:{c.E:F2} PV:{c.PV:F2} Cell:({cellX},{cellZ})", textColor);
            }
        }
        WriteLine("", textColor);

        // Picked Block (highlighted section)
        {
            var block = blockPickingService?.PickedBlock;
            WriteLine("Picked Block:", new Vector3(1.0f, 1.0f, 0.0f)); // Yellow

            if (block.HasValue)
            {
                var b = block.Value;
                var globalPos = b.GlobalPosition;
                var chunkOrigin = VoxelHelper.GetChunkPositionGlobal(b.ChunkIndex);
                var localPos = new Vector3i(
                    globalPos.X - chunkOrigin.X,
                    globalPos.Y - chunkOrigin.Y,
                    globalPos.Z - chunkOrigin.Z
                );

                // Get biome name for picked block
                var biomeName = GetBiomeNameForBlock(b);

                WriteLine($"  ({localPos.X},{localPos.Y},{localPos.Z})@{b.ChunkIndex} {b.Block} | {biomeName}", textColor);
            }
            else
            {
                WriteLine("  n/a", textColor);
            }
            WriteLine("", textColor);
        }

        // Controls
        WriteLine("Controls:", highlightColor);
        WriteLine("  WASD - Move", textColor);
        WriteLine("  Shift/Ctrl - Up/Down", textColor);
        WriteLine("  Mouse - Look", textColor);
        WriteLine("  F - Toggle Ghost/Physics", textColor);
        WriteLine("  F3 - Toggle Biome Debug", textColor);
        WriteLine("  F5 - Toggle Wireframe Debug", textColor);
        WriteLine("  Left Click - Break Block", textColor);
        WriteLine("  Esc - Exit", textColor);

        // Inventory Display
        // Render stacked on right side
        var invSlotHeight = 30;
        var invTotalHeight = Inventory.HotbarSize * invSlotHeight;
        var startX = Width - 200;
        var startY = (Height - invTotalHeight) / 2;

        for (var i = 0; i < Inventory.HotbarSize; i++)
        {
            var item = player.Inventory.GetItem(i);
            var isSelected = i == player.Inventory.SelectedSlot;
            var color = isSelected ? new Vector3(1, 1, 0) : new Vector3(0.7f, 0.7f, 0.7f);

            string content = item.IsEmpty ? "Empty" : $"{item.Block} x{item.Count}";
            if (isSelected) content = $"> {content}";

            // Simple text rendering for now
            textRenderer.Render(content, 22, startX, startY + i * invSlotHeight, color);
        }
    }

    /// <summary>
    /// Gets the biome name for a given block by querying the cached biome data.
    /// </summary>
    private string GetBiomeNameForBlock(BlockState block)
    {
        if (streamingManager == null)
            return "Unknown";

        // Query the actual biome from the cached chunk biome data
        var worldX = (int)block.GlobalPosition.X;
        var worldZ = (int)block.GlobalPosition.Z;

        // Try to get biome from the chunk cache
        var biomeId = streamingManager.GetBiomeAtWorldPos(worldX, worldZ);

        return biomeId.ToString();
    }

    public override void Close()
    {
        world?.Close();
        base.Close();
    }

    /// <summary>
    /// Generate surrounding chunk indices based on camera position and view distance.
    /// IMPORTANT: Only returns chunks that have actually been generated/loaded.
    /// </summary>
    private int[] GenerateSurroundingChunkIndices()
    {
        if (camera == null || streamingManager == null) return [];

        // Get only the chunks that have actually been generated
        var (total, pending, generating, ready) = streamingManager.GetStats();

        if (ready == 0)
        {
            return []; // No chunks ready yet
        }

        // Get chunk indices from streaming manager (only loaded chunks)
        // This avoids testing 2,601 theoretical chunks when only 81 exist
        var readyChunks = streamingManager.GetReadyChunks();
        var indices = readyChunks.Select(c => c.ChunkIndex).ToArray();

        return indices;
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
}

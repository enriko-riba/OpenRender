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
    public void SetupTerrainSystem(ChunkStreamingManager streamingMgr, VoxelTerrainRenderer renderer, Vector3 spawnPosition)
    {
        streamingManager = streamingMgr;
        terrainRenderer = renderer;
        world = streamingManager.World; // Sync world reference

        // Add renderer to scene
        AddNode(terrainRenderer);

        // Ensure camera is initialized before creating player
        EnsureCameraInitialized(spawnPosition);

        // Initialize player with world at center position
        player = new Player(camera!, spawnPosition, world, streamingManager);

        // Initialize block picking service
        blockPickingService = new BlockPickingService(streamingManager);

        // Assign service to player
        player.BlockPickingService = blockPickingService;

        // Restore full load distance for gameplay
        streamingManager.LoadDistance = VoxelHelper.MaxDistanceInChunks;
        streamingManager.SetPrefetchMargin(GameplayPrefetchMarginChunks);

        Log.Info("GameScene: terrain components configured");
    }

    private void EnsureCameraInitialized(Vector3 startPos)
    {
        if (camera != null) return;

        // Setup camera - FPS camera
        // CRITICAL: Must match TerrainLoadingScene spawn position!
        // Both must use the same calculation: WorldChunksXZ * ChunkSideSize / 2
        //var startPos = new Vector3(
        //    VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f,  // = 16 * 600 / 2 = 4800
        //    100,
        //    VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f   // = 16 * 600 / 2 = 4800
        //);

        // Near plane increased to 0.5 to improve depth buffer precision at distance
        // and reduce Z-fighting artifacts on distant horizontal surfaces.
        // Ratio of 1200:1 (far/near) is much better than 6000:1 for 24-bit depth buffers.
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

        //EnsureCameraInitialized();

        if (streamingManager != null)
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

        world!.Camera = camera!;
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

        // Execute GPU frustum culling (every frame for smooth rotation)
        if (streamingManager != null && terrainRenderer != null && camera != null)
        {
            // Generate surrounding chunk indices based on camera position
            var chunkIndices = GenerateSurroundingChunkIndices();

            // Execute GPU frustum culling
            var visibilityFlags = streamingManager.ExecuteFrustumCulling(camera, chunkIndices);
            terrainRenderer.SetVisibilityFlags(visibilityFlags, chunkIndices);
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

        // Performance - FPS at very top (no title)
        WriteLine($"FPS: {SceneManager.Fps:F0} ({SceneManager.AvgFrameDuration:F2}ms)", textColor);
        WriteLine($"View Distance: {VoxelHelper.MaxDistanceInChunks} chunks", textColor);
        
        // Show biome debug mode indicator
        if (terrainRenderer?.ShowBiomes == true)
        {
            WriteLine("[F3] BIOME DEBUG MODE", new Vector3(0.0f, 1.0f, 1.0f)); // Cyan
        }
        WriteLine("", textColor);

        // Time
        WriteLine($"Time: {dayNightCycle.TimeOfDay:hh\\:mm\\:ss}", new Vector3(1, 1, 0));
        WriteLine("", textColor);

        // Rendering Stats (Merged Chunks + Rendering)
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
            readyChunks = world.LoadedChunksCount;
            queuedChunks = 0;
            targetChunks = world.LoadedChunksCount;
        }

        WriteLine("Rendering:", highlightColor);
        WriteLine($"  GPU Ready: {readyChunks:N0} | Queued: {queuedChunks:N0} | Target: {targetChunks:N0}", textColor);

        // Get visibility stats from terrain renderer (GPU-based)
        if (terrainRenderer != null && streamingManager != null)
        {
            WriteLine($"  Visible: {streamingManager.StatVisibleChunks:N0} | Culled: {streamingManager.StatFrustumCulledChunks:N0}", textColor);
            WriteLine($"  Indices: {streamingManager.StatVisibleIndices:N0} / {streamingManager.StatTotalIndices:N0}", textColor);
        }
        
        if (terrainRenderer != null)
        {
            WriteLine($"  Visible Chunks: {terrainRenderer.VisibleDraws:N0} | Draw Calls: {terrainRenderer.DrawCallCount}", textColor);
        }
        WriteLine("", textColor);

        // Processing Metrics with terrain breakdown
        if (streamingManager != null)
        {
            var m = streamingManager.Metrics;
            WriteLine("Processing:", highlightColor);
            WriteLine($"  Terrain Gen: {m.AvgTerrainGenerationMs:F1}ms | Light Calc: {m.AvgLightCalculationMs:F1}ms", textColor);
            WriteLine($"  Light Prop: {m.AvgLightPropagationMs:F1}ms | Mesh Build: {m.AvgMeshBuildMs:F1}ms", textColor);
            WriteLine("", textColor);
            
            // NEW: Terrain generation breakdown
            WriteLine("Terrain Breakdown:", highlightColor);
            WriteLine($"  Climate: {m.AvgClimateMs:F2}ms | 3D Noise: {m.AvgNoise3DMs:F2}ms", textColor);
            WriteLine($"  Biome: {m.AvgBiomeMs:F2}ms | Blocks: {m.AvgBlockGenMs:F2}ms", textColor);
            WriteLine("", textColor);
        }

        // Player Stats
        WriteLine("Player:", highlightColor);

        var modeStr = player.IsGhostMode ? "Ghost" : "Phys";
        var groundedStr = player.IsGrounded ? "Grnd" : "Air";
        var jumpStr = player.IsJumping ? "Jump" : "";
        WriteLine($"  {modeStr} | {groundedStr} {jumpStr} | VelY: {player.VelocityY:F2}", textColor);

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

        // Removed duplicate player mode line (moved to top of section)

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
                var (C, T, H, E, PV) = climate.Value;
                // Show cell coordinates (4x4 grid per chunk)
                var localX = ((worldX % VoxelHelper.ChunkSideSize) + VoxelHelper.ChunkSideSize) % VoxelHelper.ChunkSideSize;
                var localZ = ((worldZ % VoxelHelper.ChunkSideSize) + VoxelHelper.ChunkSideSize) % VoxelHelper.ChunkSideSize;
                var cellX = localX / ChunkBiomeData.BlocksPerCell;
                var cellZ = localZ / ChunkBiomeData.BlocksPerCell;
                
                // Compact format for normal display
                WriteLine($"  C:{C:F3} T:{T:F3} H:{H:F2} E:{E:F2} PV:{PV:F2} Cell:({cellX},{cellZ})", textColor);
                
                // Extended climate info when F3 biome debug is active
                if (terrainRenderer?.ShowBiomes == true)
                {
                    // Interpret climate values
                    // NOTE: c.C and c.E are RAW [-1, 1]; convert to [0, 1] for display thresholds
                    var cont01 = C * 0.5f + 0.5f;
                    var erosion01 = E * 0.5f + 0.5f;
                    // Erosion: low = dramatic terrain, high = flat
                    var terrainType = erosion01 < 0.25f ? "Dramatic" : erosion01 < 0.6f ? "Hills" : "Flat";
                    var tempZone = T < 0.3f ? "Cold" : T > 0.7f ? "Hot" : "Temperate";
                    var moistZone = H < 0.3f ? "Dry" : H > 0.7f ? "Humid" : "Moderate";
                    // Use actual terrain config thresholds for consistency
                    var config = streamingManager?.Config;
                    string landType;

                    // FIXED: First check actual biome ID for Lake - it takes priority over terrain classification
                    var actualBiome = streamingManager.GetBiomeAtWorldPos(worldX, worldZ);
                    
                    if (actualBiome == BiomeId.Lake)
                    {
                        landType = "Lake";
                    }
                    else if (actualBiome == BiomeId.Ocean || actualBiome == BiomeId.DeepOcean)
                    {
                        landType = "Ocean";
                    }
                    else if (actualBiome == BiomeId.Beach)
                    {
                        landType = "Coast";
                    }
                    else if (actualBiome == BiomeId.Alpine)
                    {
                        landType = "Alpine";
                    }
                    else if (config != null)
                    {
                        var height = bb.GlobalPosition.Y;
                        var isUnderwater = height < VoxelHelper.WaterLevel;
                        
                        if (isUnderwater) landType = "Underwater";
                        else landType = cont01 < config.MountainThreshold ? "Inland" : "Mountain";
                    }
                    else
                    {
                        landType = cont01 < 0.30f ? "Ocean" : cont01 < 0.40f ? "Coast" : cont01 < 0.65f ? "Inland" : "Mountain";
                    }
                    
                    WriteLine($"  {landType} | {terrainType} | {tempZone} | {moistZone}", new Vector3(0.8f, 1.0f, 0.8f)); // Light green
                }
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

        // === RIGHT SIDE: Inventory at center, Controls below ===
        const int rightMargin = 200;
        var rightX = Width - rightMargin;
        const int invSlotHeight = 30;
        var invTotalHeight = Inventory.HotbarSize * invSlotHeight;
        var invStartY = (Height - invTotalHeight) / 2;

        // Inventory Display - centered vertically on right side
        for (var i = 0; i < Inventory.HotbarSize; i++)
        {
            var item = player.Inventory.GetItem(i);
            var isSelected = i == player.Inventory.SelectedSlot;
            var color = isSelected ? new Vector3(1, 1, 0) : new Vector3(0.7f, 0.7f, 0.7f);

            var content = item.IsEmpty ? "Empty" : $"{item.Block} x{item.Count}";
            if (isSelected) content = $"> {content}";

            textRenderer.Render(content, 22, rightX, invStartY + i * invSlotHeight, color);
        }

        // Controls below inventory
        var controlsStartY = invStartY + invTotalHeight + 20;
        const int controlLineHeight = 22;
        var controlY = controlsStartY;

        void WriteControlLine(string text, Vector3 color)
        {
            textRenderer.Render(text, 20, rightX, controlY, color);
            controlY += controlLineHeight;
        }

        WriteControlLine("Controls:", highlightColor);
        WriteControlLine("  WASD - Move", textColor);
        WriteControlLine("  Shift/Ctrl - Up/Down", textColor);
        WriteControlLine("  Mouse - Look", textColor);
        WriteControlLine("  F - Toggle Ghost/Physics", textColor);
        WriteControlLine("  F3 - Toggle Biome Debug", textColor);
        WriteControlLine("  F5 - Toggle Wireframe", textColor);
        WriteControlLine("  Left Click - Break Block", textColor);
        WriteControlLine("  Esc - Exit", textColor);
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
        // CRITICAL: Explicitly save all pending world data before closing.
        // This must be called before any cleanup to ensure block edits are persisted.
        streamingManager?.Shutdown();
        
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

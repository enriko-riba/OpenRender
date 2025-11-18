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
using SpyroGame.Components;
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
    
    private Vector2 mouseCenter;
    private Vector2 lastMousePosition;
    private Sprite crosshair = default!;
    private DayNightCycle dayNightCycle = default!;
    private SkyBoxSun skyBox = default!;
    private WaterNode waterNode = default!;

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
    /// Called by TerrainLoadingScene to pass initialized GPU terrain components.
    /// </summary>
    public void SetupGpuTerrain(ChunkStreamingManager streamingMgr, VoxelTerrainRenderer renderer)
    {
        streamingManager = streamingMgr;
        terrainRenderer = renderer;
        // BlockPickingService will be initialized in Load() after world is set
        Log.Info("GameScene: GPU terrain components configured");
    }

    public override void Load()
    {
        base.Load();
        BackgroundColor = Color4.CornflowerBlue;

        // Initialize block picking service NOW (after world and terrainRenderer are set)
        if (terrainRenderer != null && world != null)
        {
            blockPickingService = new BlockPickingService(world, terrainRenderer);
            Log.Info("GameScene: Block picking service initialized");
        }

        // Hide mouse cursor
        SceneManager.CursorState = CursorState.Hidden;

        // Enable backface culling
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        GL.FrontFace(FrontFaceDirection.Ccw);

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

        // Create player with physics enabled (we have proper voxel data!)
        player = new Player(camera, startPos, world);
        player.IsGhostMode = true; // Start in ghost mode for easy exploration

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
            AddNode(terrainRenderer);
            Log.Info("GameScene: Using GPU terrain renderer");
        }
        else
        {
            // Fallback to old renderer (shouldn't happen in normal flow)
            AddNode(world.ChunkRenderer);
            Log.Warn("GameScene: No GPU terrain renderer, using fallback");
        }
        
        world.Camera = camera!;
        camera!.Invalidate();

        // Initial terrain was already generated by TerrainLoadingScene
        // No need to call PrepareStartingChunks() here

        // Add water
        waterNode = WaterNode.Create(dayNightCycle);
        AddNode(waterNode);

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

        // Update day/night cycle
        dayNightCycle.Tick(elapsedSeconds);

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
        else
        {
            // OLD system fallback - try to update streaming
            try { world.UpdateStreamingFromCamera(); } catch { }
            try { world.ProcessGpuStreamingOnGlThread(); } catch { }
            try
            {
                if (world.ChunkInitializer?.TryCompleteBatch(out var completed) == true && completed.Length > 0)
                {
                    // Batch completed
                }
            }
            catch { }

            // Process queued batches
            for (int i = 0; i < 5 && world.ChunkInitializer?.HasInFlightBatch == false; i++)
            {
                try { world.UpdateStreamingFromCamera(); } catch { }
            }
        }

        // Update visibility
        world.UpdateVisibilityFromCamera(camera!);

        // Update player (handles physics, collision, and WASD movement input)
        player.Update(elapsedSeconds, SceneManager.KeyboardState);
        
        // Update block picking service (decoupled from rendering)
        blockPickingService?.Update(
            currentTime: SceneManager.Time,
            camera: camera!,
            screenCenterX: Width / 2,
            screenCenterY: Height / 2,
            maxDistance: 5.0f
        );
        
        // Sync picked block to player (for block breaking)
        if (blockPickingService != null)
        {
            player.PickedBlock = blockPickingService.PickedBlock;
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
            const float mouseSensitivity = 0.2f; // Increased from 0.15f
            player.AddRotation(delta.X * mouseSensitivity, delta.Y * mouseSensitivity, 0);

            // Re-center mouse when near edge
            if (mousePos.X < 100 || mousePos.Y < 100 ||
                mousePos.X > Width - 100 || mousePos.Y > Height - 100)
            {
                SceneManager.MousePosition = mouseCenter;
                lastMousePosition = mouseCenter;
            }
        }

        // Handle block breaking
        if (SceneManager.MouseState.IsButtonPressed(MouseButton.Left))
        {
            player.BreakBlock();
        }

        // Call base which updates all nodes
        base.UpdateFrame(elapsedSeconds);
    }

    public override void RenderFrame(double elapsedSeconds)
    {
        base.RenderFrame(elapsedSeconds);
        
        // Picking data is rendered on-demand in UpdateFrame (right before reading)
        // to avoid race conditions with FBO clearing
        
        RenderUI();
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
        //WriteLine("", textColor);

        // World Stats (FIXED - Use correct VoxelHelper constants)
        //WriteLine("World:", highlightColor);
       // WriteLine($"  Size: {VoxelHelper.WorldChunksXZ}x{VoxelHelper.WorldChunksXZ} chunks", textColor);
        //WriteLine($"  Chunk Size: {VoxelHelper.ChunkSideSize}x{VoxelHelper.ChunkYSize}", textColor);
        WriteLine($"View Distance: {VoxelHelper.MaxDistanceInChunks} chunks", textColor);
        WriteLine("", textColor);

        // Time
        WriteLine($"Time: {dayNightCycle.TimeOfDay:hh\\:mm\\:ss}", new Vector3(1, 1, 0));
        WriteLine("", textColor);

        // Chunk Stats (FIXED - Show actual generated chunks, not theoretical surrounding)
        int loadedChunks = 0;
        int generatedChunks = 0;  // Actually generated terrain
        
        if (streamingManager != null)
        {
            // GPU terrain - get stats from streaming manager
            var (total, pending, generating, ready) = streamingManager.GetStats();
            loadedChunks = ready;  // Only count ready chunks as "loaded"
            generatedChunks = ready;  // Same as loaded for GPU terrain
        }
        else
        {
            // Fallback to VoxelWorld (old system)
            loadedChunks = world.LoadedChunksCount;
            generatedChunks = world.LoadedChunksCount;
        }
        
        WriteLine("Chunks:", highlightColor);
        WriteLine($"  Generated: {generatedChunks:N0}", textColor);
        WriteLine($"  Loaded: {loadedChunks:N0}", textColor);
        
        // Get visibility stats from terrain renderer (GPU-based)
        if (terrainRenderer != null)
        {
            var visibleDraws = terrainRenderer.VisibleDraws;
            
            // NOTE: After GPU culling optimization, CPU-side visibility flags are not available
            // The actual frustum culling happens on GPU, so we can only show the total loaded chunks
            WriteLine($"  Visible: {visibleDraws:N0}", textColor);
            WriteLine($"  Culling: GPU-based (stats N/A)", new Vector3(0.7f, 0.7f, 0.7f));
        }
        WriteLine("", textColor);

        // Rendering Stats (FIXED - Use terrain renderer stats)
        WriteLine("Rendering:", highlightColor);
        if (terrainRenderer != null)
        {
            WriteLine($"  Faces: {terrainRenderer.RenderedBlocks:N0}", textColor);
            WriteLine($"  Draw Calls: {terrainRenderer.DrawCallCount}", textColor);
        }
        WriteLine("", textColor);

        // Player Stats
        WriteLine("Player:", highlightColor);
        WriteLine($"  Position: {player.Position:F1}", textColor);
        WriteLine($"  Mode: {(player.IsGhostMode ? "Ghost (Fly)" : "Physics")} ", textColor);
        WriteLine($"  Grounded: {player.IsGrounded}", textColor);
        WriteLine($"  Jumping: {player.IsJumping}", textColor);
        WriteLine($"  Velocity Y: {player.VelocityY:F2}", textColor);
        WriteLine("", textColor);
        
        // Picked Block (highlighted section)
        {
            var block = blockPickingService?.PickedBlock;
            WriteLine("Picked Block:", new Vector3(1.0f, 1.0f, 0.0f)); // Yellow
            WriteLine($"  Type: {block?.BlockType.ToString() ?? "n/a"}", textColor);
            
            if (block.HasValue)
            {
                // Calculate local position within chunk
                var globalPos = block.Value.GlobalPosition;
                var chunkOrigin = VoxelHelper.GetChunkPositionGlobal(block.Value.ChunkIndex);
                var localPos = new Vector3i(
                    globalPos.X - chunkOrigin.X,
                    globalPos.Y - chunkOrigin.Y,
                    globalPos.Z - chunkOrigin.Z
                );
                WriteLine($"  Local: ({localPos.X}, {localPos.Y}, {localPos.Z})", textColor);
                WriteLine($"  Global: ({globalPos.X}, {globalPos.Y}, {globalPos.Z})", textColor);
            }
            else
            {
                WriteLine($"  Local: n/a", textColor);
                WriteLine($"  Global: n/a", textColor);
            }
            
            WriteLine($"  Chunk: {block?.ChunkIndex.ToString() ?? "n/a"}", textColor);
            WriteLine("", textColor);
        }
        
        if (player.CurrentBlockBellow is not null)
        {
            WriteLine($"Block Below: {player.CurrentBlockBellow.Value.BlockType}", textColor);
        }
        WriteLine("", textColor);

        

        // Controls
        WriteLine("Controls:", highlightColor);
        WriteLine("  WASD - Move", textColor);
        WriteLine("  Shift/Ctrl - Up/Down", textColor);
        WriteLine("  Mouse - Look", textColor);
        WriteLine("  F - Toggle Ghost/Physics", textColor);
        WriteLine("  Left Click - Break Block", textColor);
        WriteLine("  Esc - Exit", textColor);
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
        
        // Picking FBO removed - no longer needed
    }
}

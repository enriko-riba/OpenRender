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
using SpyroGame.World;

namespace SpyroGame;

/// <summary>
/// Test scene for GPU-generated terrain.
/// Receives pre-initialized terrain from GpuTerrainLoadingScene.
/// </summary>
internal class GpuTerrainTestScene : Scene
{
    private readonly ITextRenderer textRenderer;
    private readonly ChunkStreamingManager streamingManager;
    private readonly VoxelTerrainRenderer terrainRenderer;
    private readonly GpuTerrainTimings initialTimings;
    private int testChunkCount;

    private Player player = default!;
    private Vector2 mouseCenter;
    private Vector2 lastMousePosition;
    private Sprite crosshair = default!;

    private readonly Vector3 textColor = Vector3.One;
    private readonly Vector3 highlightColor = new(0.3f, 0.8f, 0.3f);
    
    // Frustum culling throttling - time-based to handle multiple UpdateFrame calls per render frame
    private double lastCullingTime = -1.0; // -1 to trigger immediately on first frame
    private const double CullingIntervalSeconds = 0.166; // ~6 times per second (166ms)

    public GpuTerrainTestScene(
        ITextRenderer textRenderer,
        ChunkStreamingManager streamingManager,
        VoxelTerrainRenderer terrainRenderer,
        GpuTerrainTimings initialTimings,
        int chunkCount)
    {
        this.textRenderer = textRenderer;
        this.streamingManager = streamingManager;
        this.terrainRenderer = terrainRenderer;
        this.initialTimings = initialTimings;
        this.testChunkCount = chunkCount;
        Name = "GpuTerrainTestScene";
    }

    public override void Load()
    {
        base.Load();
        BackgroundColor = Color4.DarkSlateBlue;

        // Hide mouse cursor
        SceneManager.CursorState = CursorState.Hidden;

        // Enable backface culling for better performance
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        GL.FrontFace(FrontFaceDirection.Ccw);

        // Setup camera - FPS camera for terrain exploration
        var startPos = new Vector3(32, 10, 32);
        camera = new CameraFps(startPos, Width / (float)Height, 0.1f, VoxelHelper.FarPlane);
        camera.AddRotation(0, -20f, 0);

        // Create player with ghost mode enabled (for GPU test terrain)
        // NOTE: GPU-generated terrain doesn't populate VoxelWorld chunks, so collision detection won't work.
        // Use Ghost mode (fly mode) for free movement without physics.
        // Press 'F' to toggle physics mode if needed (but collision won't work with GPU-only terrain).
        player = new Player(camera, startPos, streamingManager.World);
        player.IsGhostMode = true; // Enable ghost mode (fly) - no physics, no collision

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

        // Add terrain renderer to scene
        AddNode(terrainRenderer);

        // Add light
        var dirLight = new LightUniform()
        {
            Direction = new Vector3(-0.5f, -0.8f, -0.3f),
            Ambient = new Vector3(0.3f, 0.3f, 0.35f),
            Diffuse = new Vector3(0.8f, 0.8f, 0.7f),
            Specular = new Vector3(0.4f, 0.4f, 0.4f),
        };
        AddLight(dirLight);

        Log.Info($"GpuTerrainTestScene: Loaded with {testChunkCount} chunks, {initialTimings.VertexCount:N0} vertices");
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
        if (SceneManager.KeyboardState.IsKeyDown(OpenTK.Windowing.GraphicsLibraryFramework.Keys.Escape))
        {
            SceneManager.Close();
            return;
        }

        // Execute frustum culling with time-based throttling
        // This is robust against multiple UpdateFrame calls per render frame (e.g., fixed timestep accumulators)
        var currentTime = SceneManager.Time;
        if (camera != null && (currentTime - lastCullingTime) >= CullingIntervalSeconds)
        {
            lastCullingTime = currentTime;
            var chunkIndices = GenerateTestChunkIndices(testChunkCount);
            var visibilityFlags = streamingManager.ExecuteFrustumCulling(camera, chunkIndices);
            terrainRenderer.SetVisibilityFlags(visibilityFlags, chunkIndices);
        }

        // Update player (handles physics, collision, and WASD movement input)
        player.Update(elapsedSeconds, SceneManager.KeyboardState, SceneManager.MouseState);

        // Vertical movement in ghost mode is handled by Player internally via keyboard actions
        // Note: Shift and Ctrl for up/down removed - player handles all movement

        // Handle mouse rotation
        var mousePos = SceneManager.MouseState.Position;
        var delta = lastMousePosition - mousePos;
        lastMousePosition = mousePos;
        if (delta.LengthSquared > 0)
        {
            const float mouseSensitivity = 0.1f;
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
        if (SceneManager.MouseState.IsButtonPressed(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left))
        {
            player.BreakBlock();
        }

        // Call base which updates all nodes
        base.UpdateFrame(elapsedSeconds);

        // Regenerate (R key) - regenerate terrain in place
        if (SceneManager.KeyboardState.IsKeyPressed(OpenTK.Windowing.GraphicsLibraryFramework.Keys.R))
        {
            testChunkCount = testChunkCount == 16 ? 64 : 16;
            Log.Info($"Regenerating with {testChunkCount} chunks...");

            try
            {
                // Reinitialize Phase 3 with new chunk count
                streamingManager.InitializePhase3(testChunkCount);
                
                // NOTE: Don't reinitialize frustum culling - it's already initialized and can handle different chunk counts
                // streamingManager.InitializeFrustumCulling(testChunkCount);  ← REMOVED

                // Generate new chunk indices
                var chunkIndices = GenerateTestChunkIndices(testChunkCount);

                // Reset timing
                initialTimings.Reset();
                initialTimings.StartTiming();

                // Phase 2: Generate voxel data
                streamingManager.DispatchGeneration(chunkIndices);
                initialTimings.RecordPhase2Generation(chunkIndices.Length);

                // Phase 3+4: Complete pipeline (assign descriptors, setup renderer)
                streamingManager.ExecuteCompletePipeline(chunkIndices);
                var phase3Buffers = typeof(ChunkStreamingManager)
                    .GetField("phase3Buffers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                    .GetValue(streamingManager) as Phase3BufferManager;
                if (phase3Buffers != null)
                {
                    var vertexCount = phase3Buffers.CurrentVertexBufferEnd;
                    var faceCount = vertexCount / 4; // 4 vertices per face
                    var indexCount = faceCount * 6;  // 6 indices per face
                    initialTimings.RecordPhase3Compaction(vertexCount, indexCount);
                    initialTimings.RecordPhase4Setup();
                }
                Log.Highlight($"✅ Regeneration Complete!");
                Log.Info($"   Total Time: {initialTimings.TotalPipelineMs:F2}ms");
                Log.Info($"   Vertices: {initialTimings.VertexCount:N0}, Indices: {initialTimings.IndexCount:N0}");
            }
            catch (Exception ex)
            {
                Log.Error($"Regeneration failed: {ex.Message}");
                Log.Error($"Stack trace: {ex.StackTrace}");
            }
        }
    }

    private int[] GenerateTestChunkIndices(int count)
    {
        var gridSize = (int)Math.Ceiling(Math.Sqrt(count));
        var indices = new List<int>();

        for (var z = 0; z < gridSize && indices.Count < count; z++)
        {
            for (var x = 0; x < gridSize && indices.Count < count; x++)
            {
                if (x >= 0 && x < VoxelHelper.WorldChunksXZ &&
                    z >= 0 && z < VoxelHelper.WorldChunksXZ)
                {
                    var idx = z * VoxelHelper.WorldChunksXZ + x;
                    indices.Add(idx);
                }
            }
        }

        return [.. indices];
    }

    public override void RenderFrame(double elapsedSeconds)
    {
        base.RenderFrame(elapsedSeconds);
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

        WriteLine("GPU TERRAIN TEST", highlightColor);
        WriteLine("", textColor);

        // Initial generation stats
        WriteLine($"Chunks: {testChunkCount}", textColor);
        WriteLine($"Vertices: {initialTimings.VertexCount:N0}", textColor);
        WriteLine($"Indices: {initialTimings.IndexCount:N0}", textColor);
        WriteLine($"Faces: {initialTimings.IndexCount / 3:N0}", textColor);
        WriteLine("", textColor);

        // Frustum culling stats
        var visibleChunks = streamingManager.VisibleChunkCount;
        var culledChunks = streamingManager.CulledChunkCount;
        var cullingEfficiency = testChunkCount > 0 ? (culledChunks * 100.0f / testChunkCount) : 0;
        WriteLine("Frustum Culling:", highlightColor);
        WriteLine($"  Visible: {visibleChunks}/{testChunkCount}", textColor);
        WriteLine($"  Culled:  {culledChunks} ({cullingEfficiency:F1}%)", textColor);
        WriteLine("", textColor);

        // Generation timing
        WriteLine("Generation Time:", highlightColor);
        WriteLine($"  Phase 2: {initialTimings.Phase2GenerationMs,6:F1} ms", textColor);
        WriteLine($"  Phase 3: {initialTimings.TotalPhase3Ms,6:F1} ms", textColor);
        WriteLine($"  Phase 4: {initialTimings.Phase4SetupMs,6:F1} ms", textColor);
        WriteLine($"  Total:   {initialTimings.TotalPipelineMs,6:F1} ms", textColor);
        WriteLine("", textColor);

        // Chunk layout info
        var gridSize = (int)Math.Ceiling(Math.Sqrt(testChunkCount));
        WriteLine($"Layout: {gridSize}x{gridSize} grid", textColor);
        WriteLine($"Height: Y[0-3] staircase", textColor);
        WriteLine("", textColor);

        // Controls
        WriteLine("Controls:", textColor);
        WriteLine("  WASD - Move", textColor);
        WriteLine("  Shift/Ctrl - Up/Down", textColor);
        WriteLine("  Mouse - Look", textColor);
        WriteLine("  F - Toggle Ghost/Physics", textColor);
        WriteLine("  R - Regenerate (16/64)", textColor);
        WriteLine("  Esc - Exit", textColor);
        WriteLine("", textColor);

        // Camera info
        WriteLine($"Camera: {camera!.Position:F1}", textColor);
        WriteLine($"FPS: {SceneManager.Fps:F0} ({SceneManager.AvgFrameDuration:F2}ms)", textColor);
        WriteLine("", textColor);

        // Player stats
        WriteLine("Player:", highlightColor);
        WriteLine($"  Position: {player.Position:F1}", textColor);
        WriteLine($"  Mode: {(player.IsGhostMode ? "Ghost (Fly)" : "Physics")} ", textColor);
        WriteLine($"  Grounded: {player.IsGrounded}", textColor);
        WriteLine($"  Jumping: {player.IsJumping}", textColor);
        WriteLine($"  Velocity Y: {player.VelocityY:F2}", textColor);
        
        if (player.PickedBlock is not null)
        {
            WriteLine($"  Picked Block: {player.PickedBlock}", textColor);
        }
        
        if (player.CurrentBlockBellow is not null)
        {
            WriteLine($"  Block Below: {player.CurrentBlockBellow.Value.BlockType}", textColor);
        }
    }

    public override void Close()
    {
        terrainRenderer?.Dispose();
        streamingManager?.Dispose();
        base.Close();
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

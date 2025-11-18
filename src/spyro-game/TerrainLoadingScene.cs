using OpenRender;
using OpenRender.Core.Rendering;
using OpenRender.Core.Rendering.Text;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using SpyroGame.World;
using System.Diagnostics;

namespace SpyroGame;

/// <summary>
/// Loading scene for GPU-based procedural terrain generation.
/// Uses ChunkStreamingManager (NEW GPU system) for modern voxel generation.
/// Transitions to GameScene when terrain is ready.
/// </summary>
internal class TerrainLoadingScene : Scene
{
    private readonly ITextRenderer textRenderer;
    private readonly VoxelWorld world;
    private readonly Stopwatch timer = new();
    
    private ChunkStreamingManager? streamingManager;
    private VoxelTerrainRenderer? terrainRenderer;
    
    private readonly Vector3 textColor = Vector3.One;
    private readonly Vector3 progressColor = new(1.0f, 0.8f, 0.3f);
    private readonly Vector3 highlightColor = new(0.3f, 0.8f, 0.3f);
    
    private readonly Queue<(string description, Action operation)> operationQueue = new();
    private string currentOperation = "Initializing...";
    private int totalOperations = 0;
    private int completedOperations = 0;
    private bool isComplete = false;
    private bool isQueueBuilt = false;
    
    private Vector3 startPosition;
    private int surroundingChunkCount = 0;
    private uint lastVertexCount = 0;
    private uint lastIndexCount = 0;

    public TerrainLoadingScene(ITextRenderer textRenderer, VoxelWorld world)
    {
        this.textRenderer = textRenderer;
        this.world = world;
        Name = "TerrainLoadingScene";
    }

    public override void Load()
    {
        base.Load();
        BackgroundColor = Color4.Black;
        timer.Start();
        
        // Create a simple 2D camera for the loading screen
        camera = new Camera2D(Vector3.Zero, Width, Height);
        
        // Calculate starting position (center of world)
        startPosition = new Vector3(
            VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f,
            100,
            VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f
        );
        
        Log.Info("TerrainLoadingScene: Starting GPU terrain generation...");
    }
    
    private void BuildOperationQueue()
    {
        // Calculate surrounding chunks for spawn position
        var cameraChunkX = (int)((startPosition.X + 0.5f) / VoxelHelper.ChunkSideSize);
        var cameraChunkZ = (int)((startPosition.Z + 0.5f) / VoxelHelper.ChunkSideSize);
        var chunkIndices = GenerateSurroundingChunkIndices(cameraChunkX, cameraChunkZ);
        surroundingChunkCount = chunkIndices.Length;
        
        // Phase 1: Initialize ChunkStreamingManager
        operationQueue.Enqueue(("Initializing GPU streaming manager...", () =>
        {
            streamingManager = new ChunkStreamingManager(world);
            Log.Info("ChunkStreamingManager created");
        }));
        
        // Phase 2: GPU Pipeline initialization
        operationQueue.Enqueue(("Initializing GPU generation pipeline...", () =>
        {
            // FULL PROCEDURAL MODE for GameScene
            // Elevation parameters calibrated for procedural noise range
            // Pre-allocates buffers for max view distance automatically
            streamingManager!.InitializeGpuGeneration(world.Seed, 
                elevOffset: -0.5f,     // Minimum terrain height (handles underwater/caves)
                elevScale: 0.8f,       // Scale factor to normalize to [0,1]
                testMode: false);      // PROCEDURAL MODE - full terrain generation
                                       // maxChunks=0 (default) uses CalculateMaxViewChunks()
            Log.Info($"GPU generation initialized (PROCEDURAL MODE) with pre-allocated buffers");
        }));
        
        operationQueue.Enqueue(("Initializing visibility & compaction buffers...", () =>
        {
            streamingManager!.InitializePhase3(surroundingChunkCount);
            Log.Info($"Phase 3 initialized for {surroundingChunkCount} chunks");
        }));
        
        operationQueue.Enqueue(("Initializing terrain renderer...", () =>
        {
            streamingManager!.InitializePhase4();
            terrainRenderer = streamingManager.GetTerrainRenderer();
            Log.Info("Terrain renderer initialized");
        }));
        
        operationQueue.Enqueue(("Initializing frustum culling...", () =>
        {
            // Pre-allocates for max view distance automatically (maxChunks=0)
            streamingManager!.InitializeFrustumCulling();
            Log.Info("Frustum culling initialized with pre-allocated buffers");
        }));
        
        // Phase 3: Generate starting chunks
        operationQueue.Enqueue(("Generating voxel data (GPU Phase 2)...", () =>
        {
            streamingManager!.DispatchGeneration(chunkIndices);
            Log.Info($"Generated {chunkIndices.Length} chunks on GPU");
        }));
        
        operationQueue.Enqueue(("Processing visibility & compaction (GPU Phase 3)...", (Action)(() =>
        {
            var (vertexCount, indexCount) = streamingManager!.ExecutePhase3(chunkIndices);
            lastVertexCount = vertexCount;
            lastIndexCount = indexCount;
            
            // Register chunks as ready so GameScene stats work correctly
            streamingManager.RegisterChunksAsReady(chunkIndices);
            
            Log.Info($"Phase 3 complete: {vertexCount:N0} vertices, {indexCount:N0} indices");
        })));
        
        operationQueue.Enqueue(("Setting up rendering buffers...", (Action)(() =>
        {
            // Use reflection to access private phase3Buffers field
            var phase3Buffers = typeof(ChunkStreamingManager)
                .GetField("phase3Buffers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                .GetValue(streamingManager) as Phase3BufferManager;
                
            if (phase3Buffers != null && terrainRenderer != null)
            {
                // Use the counts we stored from ExecutePhase3
                terrainRenderer.SetupBuffers(phase3Buffers, lastVertexCount, lastIndexCount);
                Log.Info("Rendering buffers configured");
            }
            
            Log.Highlight($"✅ GPU Terrain Generation Complete!");
            Log.Info($"   Total Time: {timer.ElapsedMilliseconds}ms");
            Log.Info($"   Chunks: {surroundingChunkCount}");
        })));
        
        totalOperations = operationQueue.Count;
        Log.Info($"TerrainLoadingScene: Queued {totalOperations} operations");
    }

    public override void UpdateFrame(double elapsedSeconds)
    {
        base.UpdateFrame(elapsedSeconds);
        
        // Build queue after first render
        if (!isQueueBuilt)
        {
            BuildOperationQueue();
            isQueueBuilt = true;
            return;
        }
        
        if (isComplete) return;
        
        try
        {
            // Process one operation per frame
            if (operationQueue.Count > 0)
            {
                var (description, operation) = operationQueue.Dequeue();
                currentOperation = description;
                
                Log.Info($"[{completedOperations + 1}/{totalOperations}] {description}");
                operation.Invoke();
                
                completedOperations++;
            }
            else if (streamingManager != null && terrainRenderer != null)
            {
                // All operations complete - transition to game
                isComplete = true;
                currentOperation = "Complete!";
                
                Log.Info("TerrainLoadingScene: Transitioning to GameScene");
                
                AddAction(() =>
                {
                    var gameScene = SceneManager.GetScene("GameScene");
                    // Pass the streaming manager and renderer to GameScene
                    if (gameScene is GameScene gs)
                    {
                        Log.Info($"TerrainLoadingScene: Passing terrain to GameScene - vertexCount={lastVertexCount}, indexCount={lastIndexCount}");
                        gs.SetupGpuTerrain(streamingManager, terrainRenderer);
                    }
                    else
                    {
                        Log.Error("TerrainLoadingScene: GameScene not found or wrong type!");
                    }
                    SceneManager.ActivateScene(gameScene);
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Loading failed: {ex.Message}");
            Log.Error($"Stack trace: {ex.StackTrace}");
            currentOperation = $"ERROR: {ex.Message}";
            operationQueue.Clear();
        }
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
        
        void WriteLine(String text, Vector3 color)
        {
            textRenderer.Render(text, 22, 20, lineY, color);
            lineY += lineHeight;
        }
        
        // Title
        WriteLine("GPU TERRAIN GENERATION", highlightColor);
        WriteLine("", textColor);
        
        // Animated spinner
        var spinnerChars = new[] { '|', '/', '-', '\\' };
        var spinner = spinnerChars[(int)(timer.Elapsed.TotalSeconds * 4) % 4];
        
        // Status
        WriteLine($"{spinner} {currentOperation}", progressColor);
        WriteLine("", textColor);
        
        // Progress bar
        var progress = totalOperations > 0 ? (float)completedOperations / totalOperations : 0f;
        progress = Math.Clamp(progress, 0f, 1f);
        var progressPercent = (int)(progress * 100);
        var barWidth = 40;
        var filledWidth = Math.Max(0, (int)(progress * barWidth));
        var emptyWidth = Math.Max(0, barWidth - filledWidth);
        var progressBar = new string('█', filledWidth) + new string('░', emptyWidth);
        
        WriteLine($"[{progressBar}] {progressPercent}%", textColor);
        WriteLine($"Operations: {completedOperations}/{totalOperations}", textColor);
        WriteLine("", textColor);
        
        // Stats
        WriteLine($"Elapsed: {timer.Elapsed.TotalSeconds:F2}s", textColor);
        WriteLine($"Chunks: {surroundingChunkCount}", textColor);
    }
    
    private int[] GenerateSurroundingChunkIndices(int centerX, int centerZ)
    {
        var indices = new List<int>();
        // Use a reasonable radius for initial load to avoid GPU OOM.
        // Full streaming radius (VoxelHelper.MaxDistanceInChunks = 26) would need ~88GB VRAM!
        // 
        // Calculation for 26 radius:
        //   Chunks: 2,809
        //   Voxels: 92,012,544
        //   Worst-case (all solid): ~88GB VRAM (75GB vertices + 12GB indices)
        //
        // Using radius=4 (81 chunks) is much more reasonable:
        //   ~2.5GB VRAM for worst-case
        //
        // TODO: Implement chunk streaming/batching to load more chunks progressively
        var radius = 5; 
        
        for (var z = -radius; z <= radius; z++)
        {
            for (var x = -radius; x <= radius; x++)
            {
                var chunkX = centerX + x;
                var chunkZ = centerZ + z;
                
                if (chunkX >= 0 && chunkX < VoxelHelper.WorldChunksXZ &&
                    chunkZ >= 0 && chunkZ < VoxelHelper.WorldChunksXZ)
                {
                    var idx = chunkZ * VoxelHelper.WorldChunksXZ + chunkX;
                    indices.Add(idx);
                }
            }
        }
        
        return [.. indices];
    }
}

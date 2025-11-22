using OpenRender;
using OpenRender.Core.Rendering;
using OpenRender.Core.Rendering.Text;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using SpyroGame.World;
using System.Diagnostics;

namespace SpyroGame;

/// <summary>
/// Loading scene for GPU terrain initialization and generation.
/// Displays progress during:
/// - Texture loading (VoxelWorld constructor)
/// - Shader compilation
/// - GPU terrain generation
/// Uses a queue-based approach to process one operation per frame,
/// keeping the UI responsive and showing real-time progress.
/// </summary>
internal class GpuTerrainLoadingScene : Scene
{
    private readonly ITextRenderer textRenderer;
    private readonly int testChunkCount;
    private readonly Stopwatch timer = new();
    
    private ChunkStreamingManager? streamingManager;
    private VoxelTerrainRenderer? terrainRenderer;
    private readonly GpuTerrainTimings timings = new();
    
    private readonly Vector3 textColor = Vector3.One;
    private readonly Vector3 progressColor = new(1.0f, 0.8f, 0.3f);
    private readonly Vector3 highlightColor = new(0.3f, 0.8f, 0.3f);
    
    private readonly Queue<(string description, Action operation)> operationQueue = new();
    private string currentOperation = "Initializing...";
    private int totalOperations = 0;
    private int completedOperations = 0;
    private bool isComplete = false;
    
    public GpuTerrainLoadingScene(ITextRenderer textRenderer, int chunkCount = 16)
    {
        this.textRenderer = textRenderer;
        testChunkCount = chunkCount;
        Name = "GpuTerrainLoadingScene";
    }
    
    public override void Load()
    {
        base.Load();
        BackgroundColor = Color4.DarkSlateBlue;
        timer.Start();
        
        // Create a dummy camera for the loading scene (required by base Scene.RenderFrame)
        camera = new Camera3D(Vector3.Zero, Width / (float)Height);
        
        // DON'T build the queue here - it would trigger VoxelWorld creation before first render
        // The queue will be built after the first frame renders
        
        Log.Info($"GpuTerrainLoadingScene: Starting initialization for {testChunkCount} chunks");
    }
    
    private bool isQueueBuilt = false;
    
    private void BuildOperationQueue()
    {
        // Phase 1: Create VoxelWorld with simulated progress for texture loading
        // Since VoxelWorld loads 9 textures synchronously in constructor,
        // we split it into: prepare → execute → complete for visual feedback
        
        operationQueue.Enqueue(("Preparing to load textures...", () => Log.Info("Preparing VoxelWorld initialization")));
        
        operationQueue.Enqueue(("Loading terrain textures (this may take a few seconds)...", () =>
        {
            var world = new VoxelWorld(1338);
            streamingManager = new ChunkStreamingManager(world);
            Log.Info($"VoxelWorld created with all textures loaded");
        }));
        
        // Phase 2: GPU Pipeline initialization
        operationQueue.Enqueue(("Initializing GPU generation...", () =>
        {
            streamingManager!.InitializeGpuGeneration(1338, testMode: true);
            Log.Info("GPU generation initialized");
        }));
        
        operationQueue.Enqueue(("Initializing Phase 3 buffers...", () =>
        {
            streamingManager!.InitializePhase3(testChunkCount);
            Log.Info("Phase 3 initialized");
        }));
        
        operationQueue.Enqueue(("Initializing Phase 4 rendering...", () =>
        {
            streamingManager!.InitializePhase4();
            terrainRenderer = streamingManager.GetTerrainRenderer();
            Log.Info("Phase 4 initialized");
            
            timings.Reset();
            timings.StartTiming();
        }));
        
        // Phase 2.5: Initialize frustum culling
        operationQueue.Enqueue(("Initializing frustum culling...", () =>
        {
            streamingManager!.InitializeFrustumCulling(testChunkCount * 4); // Allow room for growth
            Log.Info("Frustum culling initialized");
        }));
        
        // Phase 3: Terrain generation
        operationQueue.Enqueue(("Generating chunk indices...", (Action)(() =>
        {
            var chunkIndices = GenerateTestChunkIndices(testChunkCount);
            Log.Info($"Generated {chunkIndices.Length} chunk indices");
            
            // Store for next operations
            operationQueue.Enqueue(("GPU Phase 2: Generating voxels...", () =>
            {
                streamingManager!.DispatchGeneration(chunkIndices);
                timings.RecordPhase2Generation(chunkIndices.Length);
                Log.Info($"Phase 2 complete: {timings.Phase2GenerationMs:F2}ms");
            }
            ));
            
            operationQueue.Enqueue(("GPU Phase 3: Visibility & compaction + Setup", () =>
            {
                streamingManager!.ExecuteCompletePipeline(chunkIndices);
                if (typeof(ChunkStreamingManager)
                    .GetField("phase3Buffers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                    .GetValue(streamingManager) is Phase3BufferManager phase3Buffers)
                {
                    timings.RecordPhase3Compaction(phase3Buffers.CurrentVertexBufferEnd, (phase3Buffers.CurrentVertexBufferEnd / 4) * 6);
                    timings.RecordPhase4Setup();
                }
                Log.Info($"Phase 3+4 complete: {timings.TotalPhase3Ms:F2}ms");
            }));
            
            // Update total operations count now that we've added dynamic operations
            totalOperations = operationQueue.Count + completedOperations;
        })));
    }
    
    public override void UpdateFrame(double elapsedSeconds)
    {
        base.UpdateFrame(elapsedSeconds);
        
        // Build the queue after the first render (so UI shows before heavy loading)
        if (!isQueueBuilt)
        {
            BuildOperationQueue();
            totalOperations = operationQueue.Count;
            isQueueBuilt = true;
            Log.Info($"GpuTerrainLoadingScene: Queued {totalOperations} operations");
            return; // Let one more frame render before starting operations
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
                // All operations complete - transition to test scene
                isComplete = true;
                currentOperation = "Complete!";
                
                // Wait one more frame before transition
                AddAction(() =>
                {
                    var testScene = new GpuTerrainTestScene(textRenderer, streamingManager, terrainRenderer, timings, testChunkCount);
                    SceneManager.AddScene(testScene);
                    SceneManager.ActivateScene(testScene.Name);
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Loading failed: {ex.Message}");
            Log.Error($"Stack trace: {ex.StackTrace}");
            currentOperation = $"ERROR: {ex.Message}";
            operationQueue.Clear(); // Stop processing
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
        
        void WriteLine(string text, Vector3 color)
        {
            textRenderer.Render(text, 22, 20, lineY, color);
            lineY += lineHeight;
        }
        
        // Title
        WriteLine("GPU TERRAIN LOADING", highlightColor);
        WriteLine("", textColor);
        
        // Animated spinner
        var spinnerChars = new[] { '|', '/', '-', '\\' };
        var spinner = spinnerChars[(int)(timer.Elapsed.TotalSeconds * 4) % 4];
        
        // Current operation
        WriteLine($"{spinner} {currentOperation}", progressColor);
        WriteLine("", textColor);
        
        // Progress bar
        var progress = totalOperations > 0 ? (float)completedOperations / totalOperations : 0f;
        progress = Math.Clamp(progress, 0f, 1f); // Ensure progress is between 0 and 1
        var progressPercent = (int)(progress * 100);
        var barWidth = 40;
        var filledWidth = Math.Max(0, (int)(progress * barWidth)); // Ensure non-negative
        var emptyWidth = Math.Max(0, barWidth - filledWidth); // Ensure non-negative
        var progressBar = new string('█', filledWidth) + new string('░', emptyWidth);
        
        WriteLine($"[{progressBar}] {progressPercent}%", textColor);
        WriteLine($"Operations: {completedOperations}/{totalOperations}", textColor);
        WriteLine("", textColor);
        
        // Timing
        WriteLine($"Elapsed: {timer.Elapsed.TotalSeconds:F2}s", textColor);
        WriteLine($"Chunks: {testChunkCount}", textColor);
        
        // Show timing breakdown if available
        if (timings.TotalPipelineMs > 0)
        {
            WriteLine("", textColor);
            WriteLine("Generation Timing:", highlightColor);
            WriteLine($"  Phase 2: {timings.Phase2GenerationMs,6:F1} ms", textColor);
            WriteLine($"  Phase 3: {timings.TotalPhase3Ms,6:F1} ms", textColor);
            WriteLine($"  Phase 4: {timings.Phase4SetupMs,6:F1} ms", textColor);
            WriteLine($"  Total:   {timings.TotalPipelineMs,6:F1} ms", progressColor);
        }
    }
    
    private static int[] GenerateTestChunkIndices(int count)
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
}

using OpenRender;
using OpenRender.Core.Rendering;
using OpenRender.Core.Rendering.Text;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using SpyroGame.World;
using System.Diagnostics;

namespace SpyroGame;

/// <summary>
/// Loading scene for CPU-based procedural terrain generation.
/// Uses ChunkStreamingManager's CPU pipeline for voxel generation while keeping GPU rendering.
/// Transitions to GameScene when terrain is ready with detailed 0-100% progress tracking.
/// </summary>
internal class TerrainLoadingScene : Scene
{
    private readonly ITextRenderer textRenderer;
    private readonly VoxelWorld world;
    private readonly Stopwatch timer = new();
    private readonly ProgressTracker progressTracker = new();

    private ChunkStreamingManager? streamingManager;
    private VoxelTerrainRenderer? terrainRenderer;

    private readonly Vector3 textColor = Vector3.One;
    private readonly Vector3 progressColor = new(1.0f, 0.8f, 0.3f);
    private readonly Vector3 highlightColor = new(0.3f, 0.8f, 0.3f);
    private readonly Vector3 dimColor = new(0.6f, 0.6f, 0.6f);

    private readonly Queue<(string description, Action operation)> operationQueue = new();
    private string currentStage = "Initializing...";
    private bool isComplete = false;
    private bool isQueueBuilt = false;

    private Vector3 startPosition;
    private int targetChunkCount = 0;

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

        Log.Info("TerrainLoadingScene: Starting CPU terrain generation with enhanced progress tracking...");
    }

    private bool isStreamingTerrain = false;
    private bool isWaitingForProgressAnimation = false;
    private const int INITIAL_LOAD_DISTANCE = 5;
    private int initialChunkCount = 0;
    private int minimumReadyChunksForTransition;
    private const int MaxOutstandingChunksForTransition = 4;

    private void BuildOperationQueue()
    {
        minimumReadyChunksForTransition = VoxelHelper.CalculateCircularChunkCount(INITIAL_LOAD_DISTANCE);
        targetChunkCount = minimumReadyChunksForTransition;
        
        // Define progress ranges for each operation
        progressTracker.AddOperation("Initialize Streaming Manager", 0f, 10f);
        progressTracker.AddOperation("Initialize Terrain Generation", 10f, 25f);
        progressTracker.AddOperation("Initialize Visibility & Compaction", 25f, 40f);
        progressTracker.AddOperation("Initialize Terrain Renderer", 40f, 50f);
        progressTracker.AddOperation("Initialize Frustum Culling", 50f, 60f);
        progressTracker.AddOperation("Stream Initial Terrain", 60f, 95f);
        progressTracker.AddOperation("Finalize", 95f, 100f);

        // Phase 1: Initialize ChunkStreamingManager
        operationQueue.Enqueue(("Initialize Streaming Manager", () =>
        {
            progressTracker.UpdateOperation("Initialize Streaming Manager", 0.5f, "Creating chunk streaming manager...");
            streamingManager = new ChunkStreamingManager(world)
            {
                LoadDistance = INITIAL_LOAD_DISTANCE
            };
            streamingManager.SetPrefetchMargin(0);
            progressTracker.CompleteOperation("Initialize Streaming Manager");
            Log.Info("ChunkStreamingManager created");
        }));

        // Phase 2: GPU Pipeline initialization
        operationQueue.Enqueue(("Initialize Terrain Generation", () =>
        {
            progressTracker.UpdateOperation("Initialize Terrain Generation", 0.3f, "Allocating buffers...");
            streamingManager!.InitializeGpuGeneration(world.Seed);
            progressTracker.UpdateOperation("Initialize Terrain Generation", 0.9f, "Preparing CPU pipeline...");
            progressTracker.CompleteOperation("Initialize Terrain Generation");
            Log.Info($"CPU terrain generation initialized with pre-allocated buffers");
        }));

        operationQueue.Enqueue(("Initialize Visibility & Compaction", () =>
        {
            progressTracker.UpdateOperation("Initialize Visibility & Compaction", 0.4f, "Setting up visibility buffers...");
            streamingManager!.InitializePhase3();
            progressTracker.UpdateOperation("Initialize Visibility & Compaction", 0.8f, "Setting up compaction pipeline...");
            progressTracker.CompleteOperation("Initialize Visibility & Compaction");
            Log.Info($"Phase 3 initialized");
        }));

        operationQueue.Enqueue(("Initialize Terrain Renderer", () =>
        {
            progressTracker.UpdateOperation("Initialize Terrain Renderer", 0.5f, "Creating renderer...");
            streamingManager!.InitializePhase4();
            terrainRenderer = streamingManager.GetTerrainRenderer();
            progressTracker.CompleteOperation("Initialize Terrain Renderer");
            Log.Info("Terrain renderer initialized");
        }));

        operationQueue.Enqueue(("Initialize Frustum Culling", () =>
        {
            progressTracker.UpdateOperation("Initialize Frustum Culling", 0.5f, "Allocating culling buffers...");
            streamingManager!.InitializeFrustumCulling();
            progressTracker.CompleteOperation("Initialize Frustum Culling");
            Log.Info("Frustum culling initialized with pre-allocated buffers");
        }));

        // Phase 3: Stream terrain with progress tracking
        operationQueue.Enqueue(("Stream Initial Terrain", () =>
        {
            isStreamingTerrain = true;
            streamingManager!.Update(startPosition);
            Log.Info($"Started terrain streaming (target: {targetChunkCount} chunks)...");
        }));

        Log.Info($"TerrainLoadingScene: Queued {operationQueue.Count} operations");
    }
    

    public override void UpdateFrame(double elapsedSeconds)
    {
        base.UpdateFrame(elapsedSeconds);

        // Update progress tracker animation
        progressTracker.Update(elapsedSeconds);

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
            // Handle terrain streaming progress
            if (isStreamingTerrain && streamingManager != null)
            {
                streamingManager.Update(startPosition);
                var (total, pending, generating, ready) = streamingManager.GetStats();

                // Calculate streaming progress (60% to 95%)
                var streamingProgress = ready / (float)targetChunkCount;
                streamingProgress = Math.Clamp(streamingProgress, 0f, 1f);
                
                progressTracker.UpdateOperation(
                    "Stream Initial Terrain", 
                    streamingProgress, 
                    $"{ready}/{targetChunkCount} chunks ready (Pending: {pending}, Generating: {generating})");

                currentStage = $"Streaming terrain: {ready}/{targetChunkCount} chunks";

                // Check if streaming is complete
                var requiredReadyChunks = Math.Max(targetChunkCount, minimumReadyChunksForTransition);
                var outstanding = pending + generating;
                var areaReady = ready >= requiredReadyChunks && outstanding <= MaxOutstandingChunksForTransition;

                if (areaReady)
                {
                    isStreamingTerrain = false;
                    isWaitingForProgressAnimation = true; // NEW: Wait for animation to catch up
                    progressTracker.CompleteOperation("Stream Initial Terrain");
                    Log.Info($"Initial terrain streaming complete. Ready: {ready}/{requiredReadyChunks}. Waiting for progress animation...");
                }
                else
                {
                    return; // Keep streaming
                }
            }

            // Wait for progress animation to catch up before finalizing
            if (isWaitingForProgressAnimation)
            {
                if (!progressTracker.HasCaughtUp())
                {
                    // Keep updating to allow progress bar to animate
                    return;
                }
                else
                {
                    // Progress has caught up, proceed to finalization
                    isWaitingForProgressAnimation = false;
                    Log.Info("Progress animation caught up, proceeding to finalization");
                }
            }

            // Process one operation per frame
            if (operationQueue.Count > 0)
            {
                var (description, operation) = operationQueue.Dequeue();
                currentStage = description;

                Log.Info($"[Loading] {description}");
                operation.Invoke();
            }
            else if (streamingManager != null && terrainRenderer != null && !isStreamingTerrain)
            {
                // All operations complete - finalize and transition
                progressTracker.UpdateOperation("Finalize", 0.5f, "Preparing game scene...");
                
                isComplete = true;
                currentStage = "Complete!";
                progressTracker.SetProgress(100f);

                Log.Info("TerrainLoadingScene: Transitioning to GameScene");

                AddAction(() =>
                {
                    var gameScene = SceneManager.GetScene("GameScene");
                    if (gameScene is GameScene gs)
                    {
                        Log.Info($"TerrainLoadingScene: Passing terrain to GameScene");
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
            currentStage = $"ERROR: {ex.Message}";
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
        void WriteLineCentered(string text, Vector3 color, int fontSize, int y)
        {
            var size = textRenderer.Measure(text, fontSize);
            textRenderer.Render(text, fontSize, (Width - size.Width) / 2, y, color);
        }

        // Fixed positions for each section
        var titleY = 50;
        var stageY = 120;
        var detailedStatusY = 180;
        var progressBarY = 220;
        var statsY = 300;
        var errorMessagesY = Height - 200; // Bottom half, leaving room for spinner
        var spinnerY = Height - 60;

        // Title - Centered
        WriteLineCentered("SPYRO TERRAIN LOADING", highlightColor, 28, titleY);

        // Current Stage - Centered
        WriteLineCentered(currentStage, progressColor, 20, stageY);

        // Detailed Status - Centered (if available)
        if (!string.IsNullOrEmpty(progressTracker.DetailedStatus))
        {
            WriteLineCentered(progressTracker.DetailedStatus, dimColor, 20, detailedStatusY);
        }

        // Progress Bar - Centered
        var progress = progressTracker.Progress / 100f;
        var progressPercent = (int)progressTracker.Progress;
        
        const int barWidth = 500;
        const int barHeight = 35;
        var barX = (Width - barWidth) / 2;

        // Draw progress bar background (dark)
        DrawProgressBar(barX, progressBarY, barWidth, barHeight, 0f, new Vector3(0.2f, 0.2f, 0.2f));
        
        // Draw progress bar fill (gradient)
        DrawProgressBar(barX, progressBarY, barWidth, barHeight, progress, progressColor);
        
        // Draw progress bar border
        DrawProgressBar(barX - 2, progressBarY - 2, barWidth + 4, barHeight + 4, 0f, textColor, true);

        // Progress percentage text - Centered on bar
        var percentText = $"{progressPercent}%";
        WriteLineCentered(percentText, textColor, 24, progressBarY + 5);

        // Stats section - Centered (changed from left-aligned)
        var line1Y = statsY;
        WriteLineCentered($"Elapsed Time: {progressTracker.ElapsedTime:mm\\:ss}", textColor, 22, line1Y);
        
        // Line 2: Est. Remaining (always reserve space)
        var line2Y = line1Y + 45;
        if (progressTracker.Progress is > 1f and < 99f)
        {
            var eta = progressTracker.EstimatedTimeRemaining;
            WriteLineCentered($"Est. Remaining: {eta:mm\\:ss}", textColor, 22, line2Y);
        }
        
        // Line 3: Target Chunks (with extra spacing)
        var line3Y = line2Y + 60;
        WriteLineCentered($"Target Chunks: {targetChunkCount}", dimColor, 22, line3Y);
        
        // Line 4: Chunks Ready - SAME SPACING as other lines (45px)
        var line4Y = line3Y + 45; // Changed from 45 to match consistent spacing
        if (streamingManager != null)
        {
            var (_, _, _, ready) = streamingManager.GetStats();
            WriteLineCentered($"Chunks Ready: {ready}", dimColor, 22, line4Y);
        }

        // Line 5: GPU Memory - SAME SPACING as other lines (45px)
        var line5Y = line4Y + 60; // Changed from 60 to match consistent spacing
        if (streamingManager != null)
        {
            var (totalBytes, voxelBytes, visBytes, compactBytes) = streamingManager.GetMemoryStats();
            var totalMB = totalBytes / (1024f * 1024f);
            WriteLineCentered($"GPU Memory: {totalMB:F1} MB", dimColor, 22, line5Y);
        }

        // Error messages - Bottom half, centered
        if (!string.IsNullOrEmpty(currentStage) && currentStage.StartsWith("ERROR:"))
        {
            // Split error message into lines and render each centered
            var errorLines = currentStage.Split('\n');
            var errorY = errorMessagesY;
            foreach (var errorLine in errorLines)
            {
                WriteLineCentered(errorLine, new Vector3(1.0f, 0.3f, 0.3f), 18, errorY);
                errorY += 30;
            }
        }

        // Animated spinner - Bottom
        var spinnerChars = new[] { '|', '/', '-', '\\' };
        var spinner = spinnerChars[(int)(timer.Elapsed.TotalSeconds * 4) % 4];
        WriteLineCentered($"{spinner}", dimColor, 20, spinnerY);
    }

    private void DrawProgressBar(int x, int y, int width, int height, float progress, Vector3 color, bool borderOnly = false)
    {
        // Simple filled rectangle using text renderer (hack, but works for loading screen)
        // In a real implementation, you'd use a proper sprite or shader
        var fillWidth = borderOnly ? width : (int)(width * progress);
        var blockChar = borderOnly ? "□" : "█";
        var blockSize = textRenderer.Measure(blockChar, 20);
        var blocksNeeded = fillWidth / (int)blockSize.Width;
        
        for (var i = 0; i < blocksNeeded; i++)
        {
            textRenderer.Render(blockChar, 20, x + i * blockSize.Width, y, color);
        }
    }
}

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
            5133,
            230,
            4015
        );

        Log.Info("TerrainLoadingScene: Starting CPU terrain generation with enhanced progress tracking...");
    }

    private bool isStreamingTerrain = false;
    private bool isWaitingForProgressAnimation = false;
    private int minimumReadyChunksForTransition;
    private const int MaxOutstandingChunksForTransition = 4;

    private void BuildOperationQueue()
    {
        minimumReadyChunksForTransition = VoxelHelper.CalculateCircularChunkCount(VoxelHelper.MaxDistanceInChunks);
        targetChunkCount = minimumReadyChunksForTransition;
        
        // Define progress ranges for each operation
        progressTracker.AddOperation("Initialize Streaming Manager", 0f, 1f);
        progressTracker.AddOperation("Initialize Terrain Generation", 1f, 2f);
        progressTracker.AddOperation("Initialize Visibility & Compaction", 2f, 3f);
        progressTracker.AddOperation("Initialize Terrain Renderer", 3f, 4f);
        progressTracker.AddOperation("Initialize Frustum Culling", 4f, 5f);
        progressTracker.AddOperation("Stream Initial Terrain", 5f, 95f);
        progressTracker.AddOperation("Finalize", 95f, 100f);

        // Phase 1: Initialize ChunkStreamingManager
        operationQueue.Enqueue(("Initialize Streaming Manager", () =>
        {
            progressTracker.UpdateOperation("Initialize Streaming Manager", 0.5f, "Creating chunk streaming manager...");
            streamingManager = new ChunkStreamingManager(world)
            {
                LoadDistance = VoxelHelper.MaxDistanceInChunks
            };
            streamingManager.SetPrefetchMargin(0);
            progressTracker.CompleteOperation("Initialize Streaming Manager");
            Log.Info("ChunkStreamingManager created");
        }));

        // Phase 2: GPU Pipeline initialization
        operationQueue.Enqueue(("Initialize Terrain Generation", () =>
        {
            progressTracker.UpdateOperation("Initialize Terrain Generation", 0.3f, "Allocating buffers...");
            streamingManager!.InitializeCpuGeneration(world.Seed);
            progressTracker.UpdateOperation("Initialize Terrain Generation", 0.9f, "Preparing CPU pipeline...");
            progressTracker.CompleteOperation("Initialize Terrain Generation");
            Log.Info($"CPU terrain generation initialized with pre-allocated buffers");
        }));

        operationQueue.Enqueue(("Initialize Visibility & Compaction", () =>
        {
            progressTracker.UpdateOperation("Initialize Visibility & Compaction", 0.4f, "Setting up visibility buffers...");
            streamingManager!.InitializeMeshBuffers();
            progressTracker.UpdateOperation("Initialize Visibility & Compaction", 0.8f, "Setting up compaction pipeline...");
            progressTracker.CompleteOperation("Initialize Visibility & Compaction");
            Log.Info($"Mesh buffers initialized");
        }));

        operationQueue.Enqueue(("Initialize Terrain Renderer", () =>
        {
            progressTracker.UpdateOperation("Initialize Terrain Renderer", 0.5f, "Creating renderer...");
            streamingManager!.InitializeRendering();
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
                var (total, pending, generating, hasTerrain, processing, ready) = streamingManager.GetDetailedStats();

                // Calculate granular progress:
                // - Terrain generation (Generating → HasTerrain): 0-50% of streaming phase
                // - Meshing (Processing → Ready): 50-100% of streaming phase
                var terrainComplete = hasTerrain + processing + ready;
                var meshingComplete = ready;
                
                // Weight: terrain gen = 50%, meshing = 50%
                var terrainProgress = terrainComplete / (float)targetChunkCount * 0.5f;
                var meshingProgress = meshingComplete / (float)targetChunkCount * 0.5f;
                var streamingProgress = Math.Clamp(terrainProgress + meshingProgress, 0f, 1f);
                
                // Build descriptive status
                string status;
                if (terrainComplete < targetChunkCount)
                {
                    status = $"Generating terrain: {terrainComplete}/{targetChunkCount}";
                    currentStage = "Generating terrain...";
                }
                else if (ready < targetChunkCount)
                {
                    status = $"Building meshes: {ready}/{targetChunkCount}";
                    currentStage = "Building meshes...";
                }
                else
                {
                    status = $"{ready}/{targetChunkCount} chunks ready";
                    currentStage = "Finalizing...";
                }
                
                progressTracker.UpdateOperation("Stream Initial Terrain", streamingProgress, status);

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
                        gs.SetupTerrainSystem(streamingManager, terrainRenderer, startPosition);
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
        // Helper for consistent spacing
        var leftMargin = 50;
        var currentY = 50;
        const int SectionGap = 20;

        void DrawText(string text, int fontSize, Vector3 color, bool center = false)
        {
            if (center)
            {
                var size = textRenderer.Measure(text, fontSize);
                textRenderer.Render(text, fontSize, (Width - size.Width) / 2, currentY, color);
            }
            else
            {
                textRenderer.Render(text, fontSize, leftMargin, currentY, color);
            }
            currentY += fontSize + 10;
        }

        // Title
        DrawText("PREPARING GAME...", 28, highlightColor, true);
        currentY += SectionGap;

        // Current Stage
        DrawText(currentStage, 20, progressColor);
        
        // Detailed Status
        if (!string.IsNullOrEmpty(progressTracker.DetailedStatus))
        {
            DrawText(progressTracker.DetailedStatus, 20, dimColor);
        }
        currentY += SectionGap;

        // Progress Bar
        var progressPercent = Math.Clamp((int)progressTracker.Progress, 0, 100);
        const int totalChars = 50;
        var filledChars = progressPercent / 2;
        var emptyChars = totalChars - filledChars;
        var barText = "[" + new string('#', filledChars) + new string('_', emptyChars) + "]";
        var barSize = textRenderer.Measure(barText, 20);
        textRenderer.Render(barText, 20, leftMargin, currentY, progressColor);
        
        // Render percentage to the right
        var percentText = $"{progressPercent}%";
        textRenderer.Render(percentText, 20, leftMargin + (int)barSize.Width + 20, currentY, textColor);
        
        currentY += 24 + 10 + SectionGap;
        
        if (streamingManager != null)
        {
            var (totalBytes, _, _, _) = streamingManager.GetMemoryStats();
            var totalMB = totalBytes / (1024f * 1024f);
            DrawText($"Estimated terrain GPU Memory: {totalMB:F1} MB", 22, dimColor);
        }

        currentY += SectionGap;
        DrawText($"Elapsed Time: {progressTracker.ElapsedTime:mm\\:ss\\:ff}", 22, textColor);

        // Error messages
        if (!string.IsNullOrEmpty(currentStage) && currentStage.StartsWith("ERROR:"))
        {
            var errorY = Height - 200;
            var errorLines = currentStage.Split('\n');
            foreach (var errorLine in errorLines)
            {
                var size = textRenderer.Measure(errorLine, 18);
                textRenderer.Render(errorLine, 18, (Width - size.Width) / 2, errorY, new Vector3(1.0f, 0.3f, 0.3f));
                errorY += 30;
            }
        }

        // Spinner
        var spinnerChars = new[] { '|', '/', '-', '\\' };
        var spinner = spinnerChars[(int)(timer.Elapsed.TotalSeconds * 4) % 4];
        var spinnerSize = textRenderer.Measure(spinner.ToString(), 20);
        textRenderer.Render(spinner.ToString(), 20, (Width - spinnerSize.Width) / 2, Height - 60, dimColor);
    }
}

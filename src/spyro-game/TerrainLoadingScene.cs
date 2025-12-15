using OpenRender;
using OpenRender.Core.Rendering;
using OpenRender.Core.Rendering.Text;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using SpyroGame.Client;
using SpyroGame.Client.Terrain;
using SpyroGame.Shared.State;
using SpyroGame.Shared.Input;
using SpyroGame.World;
using System.Diagnostics;

namespace SpyroGame;

/// <summary>
/// Loading scene that initializes client-side terrain rendering resources.
/// Terrain streaming/generation is server-owned.
/// </summary>
internal class TerrainLoadingScene : Scene
{
    private readonly ITextRenderer textRenderer;
    private readonly GameSession session;
    private readonly VoxelWorld world;
    private readonly Stopwatch timer = new();
    private readonly ProgressTracker progressTracker = new();

    private ClientTerrainSystem? terrainSystem;
    private VoxelTerrainRenderer? terrainRenderer;

    private LocalGameClient localClient;
    private int appliedChunkPayloadCount;

    private readonly Vector3 textColor = Vector3.One;
    private readonly Vector3 progressColor = new(1.0f, 0.8f, 0.3f);
    private readonly Vector3 highlightColor = new(0.3f, 0.8f, 0.3f);
    private readonly Vector3 dimColor = new(0.6f, 0.6f, 0.6f);

    private readonly Queue<(string description, Action operation)> operationQueue = new();
    private string currentStage = "Initializing...";
    private bool isComplete = false;
    private bool isQueueBuilt = false;

    private Vector3 startPosition;

    public TerrainLoadingScene(ITextRenderer textRenderer, GameSession session)
    {
        this.textRenderer = textRenderer;
        this.session = session;
        world = session.World;
        localClient = session.Client;
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
        startPosition = new Vector3(6450, 80, 7850);    //  TODO: hardcoded position to debug Ocean biome

        Log.Info("TerrainLoadingScene: Initializing client terrain + connecting to local server...");
    }

    private void BuildOperationQueue()
    {
        progressTracker.AddOperation("Chunk Generation", 0f, 50f);
        progressTracker.AddOperation("Meshing", 50f, 100f);

        operationQueue.Enqueue(("Initialize Client Terrain", () =>
        {
            terrainSystem = new ClientTerrainSystem();
            terrainSystem.InitializeGraphics();
            terrainRenderer = terrainSystem.TerrainRenderer;
            Log.Info("Client terrain system initialized");
        }));

        operationQueue.Enqueue(("Connect", () =>
        {
            // Program owns server lifecycle. Loading scene just starts the session + connects.
            session.Start();
            localClient.Connect();

            // Send one baseline input so the server has a known held-state, but do not rely on it for connect.
            localClient.SendInput(default(PlayerInputCommand));

            Log.Info("Session started; hello sent");
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
            // Process one operation per frame
            if (operationQueue.Count > 0)
            {
                var (description, operation) = operationQueue.Dequeue();
                currentStage = description;

                Log.Info($"[Loading] {description}");
                operation.Invoke();
            }
            else if (terrainSystem != null && terrainRenderer != null)
            {
                // Keep draining chunk payloads and uploading meshes while we show progress.
                var desired = 0;
                var ready = 0;
                var generating = 0;

                var progress = localClient.LastLoadingProgress;
                if (progress.HasValue)
                {
                    desired = progress.Value.DesiredChunkCount;
                    ready = progress.Value.ReadyChunkCount;
                    generating = progress.Value.GeneratingChunkCount;
                }

                var meshed = terrainSystem.ReadyChunkCount;

                currentStage = desired > 0
                    ? $"Generation: {ready}/{desired} (generating {generating}) | Meshing: {meshed}/{desired}"
                    : (localClient.HasServerGameStarted ? "Starting game..." : "Waiting for server streaming...");

                // Apply a limited number of chunk payloads per frame to keep the loading UI responsive.
                var appliedThisFrame = 0;
                const int MaxChunkPayloadsToApplyPerFrame = 8;
                while (appliedThisFrame < MaxChunkPayloadsToApplyPerFrame && localClient.TryDequeueChunkPayload(out var payload))
                {
                    terrainSystem.ApplyChunkPayloadBytes(payload.ChunkIndex, payload.Payload);
                    appliedChunkPayloadCount++;
                    appliedThisFrame++;
                }

                terrainSystem.UpdateUploads();

                var haveTargets = desired > 0;
                var generationDone = haveTargets && ready >= desired;
                var meshingDone = haveTargets && meshed >= desired && terrainSystem.PendingMeshCount == 0;

                // Transition when the server has started gameplay AND the client finished meshing the initial set.
                if (localClient.HasServerGameStarted && (!haveTargets || (generationDone && meshingDone)))
                {
                    isComplete = true;
                    currentStage = "Complete!";
                    progressTracker.SetProgress(100f);

                    Log.Info("TerrainLoadingScene: Transitioning to GameScene");

                    AddAction(() =>
                    {
                        var gameScene = SceneManager.GetScene("GameScene");
                        if (gameScene is GameScene gs)
                        {
                            Log.Info("TerrainLoadingScene: Passing terrain + connections to GameScene");
                            gs.SetupTerrainSystem(terrainSystem, terrainRenderer, startPosition, session);
                        }
                        else
                        {
                            Log.Error("TerrainLoadingScene: GameScene not found or wrong type!");
                        }
                        SceneManager.ActivateScene(gameScene);
                    });
                }
                else
                {
                    // Phase 1: server chunk readiness (0-50%)
                    if (desired > 0)
                    {
                        var genProgress = ready / (float)Math.Max(1, desired);
                        genProgress = Math.Clamp(genProgress, 0f, 1f);
                        progressTracker.UpdateOperation(
                            "Chunk Generation",
                            genProgress,
                            $"Server: ready {ready}/{desired}, generating {generating}");

                        // Phase 2: client meshing completion (50-100%)
                        var meshProgress = meshed / (float)Math.Max(1, desired);
                        meshProgress = Math.Clamp(meshProgress, 0f, 1f);
                        progressTracker.UpdateOperation(
                            "Meshing",
                            meshProgress,
                            $"Client: payloads {appliedChunkPayloadCount:N0}, meshed {meshed}/{desired}, pending meshes {terrainSystem.PendingMeshCount:N0}");
                    }
                }
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

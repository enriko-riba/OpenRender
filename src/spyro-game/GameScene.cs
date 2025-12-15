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
using SpyroGame.Client;
using SpyroGame.Client.Terrain;
using SpyroGame.Shared.Commands;
using SpyroGame.Shared.Net;
using SpyroGame.Shared.State;
using SpyroGame.Shared.Input;
using SpyroGame.World;
using SpyroGame.World.Generation;

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

    private ClientTerrainSystem? terrainSystem;
    private VoxelTerrainRenderer? terrainRenderer;
    private BlockPickingService? blockPickingService;
    private LocalGameClient? localClient;
    private GameSession? session;

    private PlayerId localPlayerId;

    // Client-side view of server streaming state (chunk indices that are Ready).
    // This is the seam for future remote server chunk streaming.
    private readonly HashSet<int> serverReadyChunkIndices = [];

    private PlayerInputState lastSentInputState;
    private bool hasSentInitialInputState;

    private const int GameplayPrefetchMarginChunks = 2;

    private Vector2 mouseCenter;
    private Vector2 lastMousePosition;
    private bool wasLeftButtonDown;

    private FogUniform defaultFog;
    //private bool defaultFogCaptured;
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
    public void SetupTerrainSystem(
        ClientTerrainSystem terrainSystem,
        VoxelTerrainRenderer renderer,
        Vector3 spawnPosition,
        GameSession session)
    {
        this.terrainSystem = terrainSystem;
        terrainRenderer = renderer;
        // world reference is provided via Program.cs

        // Add renderer to scene
        AddNode(terrainRenderer);

        // Ensure camera is initialized before creating player
        EnsureCameraInitialized(spawnPosition);

        // Initialize player with world at center position
        player = new Player(camera!, spawnPosition, world, streamingManager: null);

        // Initialize block picking service
        blockPickingService = new BlockPickingService(terrainSystem.CollisionManager, terrainRenderer);

        // Assign service to player
        player.BlockPickingService = blockPickingService;

        this.session = session;
        localPlayerId = session.PlayerId;
        localClient = session.Client;

        // Reset input state tracking on scene (re)entry.
        hasSentInitialInputState = false;
        lastSentInputState = default;

        Log.Info("GameScene: terrain components configured (server streams, client meshes)");
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

        // Capture default fog settings so we can restore them when not submerged.
        defaultFog = Fog;
        //defaultFogCaptured = true;

        // CRITICAL: Disable polygon/line smoothing to prevent visible triangle edges
        // The base Scene.Load() enables these, but they cause visible lines on voxel terrain
        GL.Disable(EnableCap.PolygonSmooth);
        GL.Disable(EnableCap.LineSmooth);

        // Initialize block picking service NOW (after terrain system and renderer are set)
        if (terrainRenderer != null && terrainSystem != null && blockPickingService == null)
        {
            blockPickingService = new BlockPickingService(terrainSystem.CollisionManager, terrainRenderer);
            Log.Info("GameScene: Block picking service initialized");
        }

        // Hide mouse cursor
        // NOTE: Hidden does not confine the cursor, which can cause the window to lose focus
        // and make keyboard/mouse input appear "stuck". Grabbed keeps focus stable for FPS controls.
        SceneManager.CursorState = CursorState.Grabbed;

        // Enable backface culling
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        GL.FrontFace(FrontFaceDirection.Ccw);

        //EnsureCameraInitialized();

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
        var wantsInputCapture = SceneManager.CursorState == CursorState.Grabbed;
        if (!SceneManager.IsFocused && !wantsInputCapture)
        {
            if (SceneManager.CursorState != CursorState.Normal)
            {
                SceneManager.CursorState = CursorState.Normal;
            }
            lastMousePosition = SceneManager.MouseState.Position;
            base.UpdateFrame(elapsedSeconds);
            return;
        }

        if (SceneManager.CursorState != CursorState.Grabbed)
        {
            SceneManager.CursorState = CursorState.Grabbed;
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

        // Debug map hotkeys
        if (SceneManager.KeyboardState.IsKeyPressed(Keys.F6))
        {
            GenerateHeightDebugMap();
        }
        if (SceneManager.KeyboardState.IsKeyPressed(Keys.F7))
        {
            GenerateBiomeDebugMap();
        }
        if (SceneManager.KeyboardState.IsKeyPressed(Keys.F8))
        {
            GenerateClimateDebugMap(ClimateParameter.Continentalness);
        }
        if (SceneManager.KeyboardState.IsKeyPressed(Keys.F9))
        {
            GenerateClimateDebugMap(ClimateParameter.Temperature);
        }
        if (SceneManager.KeyboardState.IsKeyPressed(Keys.F10))
        {
            GenerateClimateDebugMap(ClimateParameter.Humidity);
        }
        if (SceneManager.KeyboardState.IsKeyPressed(Keys.F11))
        {
            GenerateClimateDebugMap(ClimateParameter.Erosion);
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

        // Terrain streaming/generation is server-owned (LocalGameServer.Tick).


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

            // Underwater fog: clamp visibility to ~dozen blocks and ensure sky/terrain/water
            // all converge to the same ambient-tinted fog color (preserves day/night).
            //if (defaultFogCaptured)
            {
                if (isUnderwater)
                {
                    var ambient = dayNightCycle.DirLight.Ambient;
                    // Water-tinted fog that still tracks ambient intensity and color.
                    var fogColor = new Vector3(ambient.X * 0.12f, ambient.Y * 0.32f, ambient.Z * 0.45f);

                    Fog = new FogUniform
                    {
                        FogColor = new Vector4(fogColor.X, fogColor.Y, fogColor.Z, 1.0f),
                        FogParams = new Vector4(
                            5.0f,   // near
                            20.0f,  // far
                            1.0f,   // enabled
                            0.0f)
                    };
                }
                else
                {
                    Fog = defaultFog;
                }
            }
        }

        // Gather mouse look delta first so the simulation tick uses current view.
        // When cursor is grabbed, MouseState.Position may remain constant; use MouseState.Delta instead.
        var lookDelta = Vector2.Zero;
        const float mouseSensitivity = 0.05f;
        if (SceneManager.CursorState == CursorState.Grabbed)
        {
            // Preserve previous behavior: we previously used (lastPos - currentPos), i.e. negative of typical delta.
            var mouseDelta = SceneManager.MouseState.Delta;
            if (mouseDelta.LengthSquared > 0)
            {
                lookDelta = -mouseDelta * mouseSensitivity;
            }
        }
        else
        {
            var mousePos = SceneManager.MouseState.Position;
            var posDelta = lastMousePosition - mousePos;
            lastMousePosition = mousePos;

            if (posDelta.LengthSquared > 0)
            {
                lookDelta = posDelta * mouseSensitivity;

                // Re-center mouse when near edge
                if (mousePos.X < 100 || mousePos.Y < 100 ||
                    mousePos.X > Width - 100 || mousePos.Y > Height - 100)
                {
                    SceneManager.MousePosition = mouseCenter;
                    lastMousePosition = mouseCenter;
                }
            }
        }

        // Apply look rotation locally for rendering/picking. The same look delta is also sent
        // to the authoritative server; keeping the client camera in sync prevents WASD from
        // feeling "sideways" relative to the visible camera direction.
        if (camera != null && lookDelta != Vector2.Zero)
        {
            const float lookRotationSpeed = 10.0f; // Must match Player.RotationSpeed semantics.
            camera.AddRotation(lookDelta.X * lookRotationSpeed, lookDelta.Y * lookRotationSpeed, 0);
            camera.Invalidate();
        }

        // Client sends input only on change; server runs independently; client applies latest received snapshot.
        if (localClient != null)
        {
            var toggleGhostPressed = SceneManager.KeyboardState.IsKeyPressed(Keys.F);
            var desiredGhostMode = toggleGhostPressed ? !player.IsGhostMode : player.IsGhostMode;
            bool? setGhostMode = toggleGhostPressed ? desiredGhostMode : null;

            var state = PlayerInputMapper.BuildState(SceneManager.KeyboardState, desiredGhostMode);
            var input = PlayerInputMapper.Build(SceneManager.KeyboardState, SceneManager.MouseState, desiredGhostMode, lookDelta, setGhostMode);

            var shouldSend = false;

            // Always send once so the server has a known baseline.
            if (!hasSentInitialInputState)
            {
                shouldSend = true;
                hasSentInitialInputState = true;
            }

            // Send when held-state changes (press/release W/A/S/D, sprint/crouch, ghost vertical).
            if (!state.Equals(lastSentInputState))
            {
                shouldSend = true;
                lastSentInputState = state;
            }

            // Send when there are one-shot events or look deltas.
            if (input.LookDelta != Vector2.Zero || input.JumpPressed || input.SetGhostMode.HasValue ||
                input.SelectHotbarSlot.HasValue || input.HotbarScrollDelta != 0)
            {
                shouldSend = true;
            }

            if (shouldSend)
            {
                // Always send full current state; server persists held input.
                localClient.SendInput(input with
                {
                    MoveAxes = state.MoveAxes,
                    VerticalAxis = state.VerticalAxis,
                    SprintHeld = state.SprintHeld,
                    CrouchHeld = state.CrouchHeld
                });
            }

            GameStateSnapshot? latestSnap = null;
            while (localClient.TryDequeueSnapshot(out var snap))
            {
                latestSnap = snap;

                if (snap.ChunkDelta is { HasChanges: true } chunkDelta)
                {
                    ApplyChunkDelta(chunkDelta);
                }
            }

            if (latestSnap.HasValue)
            {
                player.ApplyServerSnapshot(latestSnap.Value.Player);
            }

            // Smooth rendered position between server ticks.
            player.UpdateClientSmoothing(elapsedSeconds);
        }

        // Apply any received voxel payloads and upload meshes.
        if (localClient != null && terrainSystem != null)
        {
            const int maxChunkPayloadsToApplyPerFrame = 4;
            var appliedThisFrame = 0;
            while (localClient.TryDequeueChunkPayload(out var payload))
            {
                terrainSystem.ApplyChunkPayloadBytes(payload.ChunkIndex, payload.Payload);

                appliedThisFrame++;
                if (appliedThisFrame >= maxChunkPayloadsToApplyPerFrame)
                {
                    break;
                }
            }

            terrainSystem.UpdateUploads();
        }

        // Update block below player - find highest solid block at player X/Z regardless of mode
        UpdateBlockBelow();

        // Update block picking service (decoupled from rendering)
        // CRITICAL: Update picking AFTER player movement/camera update but BEFORE interaction
        blockPickingService?.Update(
            currentTime: SceneManager.Time,
            camera: camera!,
            maxDistance: 5.0f
        );

        // Handle interactions (Break/Place) with fresh picking data.
        // Client determines target positions via picking; server executes edits.
        if (localClient != null && blockPickingService?.PickedBlock is { } picked)
        {
            if (SceneManager.MouseState.IsButtonPressed(MouseButton.Left))
            {
                // Predict locally for responsiveness: update inventory, collision, and picking immediately.
                if (!picked.Block.IsAir())
                {
                    player.Inventory.AddItem(picked.Block);
                    terrainSystem?.TryApplyPredictedBlockEdit(picked.GlobalPosition, BlockId.Air);
                    blockPickingService.Invalidate();
                    blockPickingService.ForceUpdate(SceneManager.Time, camera!, maxDistance: 5.0f);
                }

                localClient.Send(new BreakBlockCommand(picked.GlobalPosition));
            }

            if (SceneManager.MouseState.IsButtonPressed(MouseButton.Right))
            {
                var item = player.Inventory.GetSelectedItem();
                if (!item.IsEmpty)
                {
                    var hitNormal = blockPickingService.HitNormal;
                    var placePos = picked.GlobalPosition + new Vector3i((int)hitNormal.X, (int)hitNormal.Y, (int)hitNormal.Z);

                    // Predict locally: consume item + set voxel so the feedback is instant.
                    if (terrainSystem?.TryApplyPredictedBlockEdit(placePos, item.Block) == true)
                    {
                        player.Inventory.TryConsumeSelectedItem();
                        blockPickingService.Invalidate();
                        blockPickingService.ForceUpdate(SceneManager.Time, camera!, maxDistance: 5.0f);
                    }

                    localClient.Send(new PlaceBlockCommand(placePos, item.Block));
                }
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
        var readyChunks = serverReadyChunkIndices.Count;
        var queuedChunks = 0;
        var targetChunks = serverReadyChunkIndices.Count;

        WriteLine("Rendering:", highlightColor);
        WriteLine($"  GPU Ready: {readyChunks:N0} | Queued: {queuedChunks:N0} | Target: {targetChunks:N0}", textColor);

        // Visibility stats are not available in the decoupled client pipeline yet.
        
        if (terrainRenderer != null)
        {
            WriteLine($"  Visible Chunks: {terrainRenderer.VisibleDraws:N0} | Draw Calls: {terrainRenderer.DrawCallCount}", textColor);
        }
        WriteLine("", textColor);

        // Player Stats
        WriteLine("Player:", highlightColor);

        var modeStr = player.IsGhostMode ? "Ghost" : "Phys";
        var groundedStr = player.IsGrounded ? "Grnd" : "Air";
        var jumpStr = player.IsJumping ? "Jump" : "";
        WriteLine($"  {modeStr} | {groundedStr} {jumpStr} | VelY: {player.VelocityY:F2}", textColor);

        var a = player.Attributes;
        WriteLine($"  HP: {a.Health}/{a.MaxHealth} | Food: {a.Food}/{a.MaxFood} | Sat: {a.Saturation:F1}", textColor);

        var pLocal = player.ChunkLocalPosition;
        var pChunk = player.CurrentChunk?.Index ?? -1;
        var pGlobal = player.Position;

        WriteLine($"  Smoothed Pos: ({(int)pLocal.X},{(int)pLocal.Y},{(int)pLocal.Z})@{pChunk} : ({pGlobal.X:F1},{pGlobal.Y:F1},{pGlobal.Z:F1})", textColor);

        var serverPos = player.ServerPosition;
        var sChunkIdx = -1;
        var sLocal = Vector3.Zero;
        if (world.GetChunkByGlobalPosition(serverPos, out var sChunk) && sChunk != null)
        {
            sChunkIdx = sChunk.Index;
            sLocal = serverPos - sChunk.Position;
        }

        WriteLine($"  Server Pos:   ({(int)sLocal.X},{(int)sLocal.Y},{(int)sLocal.Z})@{sChunkIdx} : ({serverPos.X:F1},{serverPos.Y:F1},{serverPos.Z:F1})", textColor);

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
        const int rightMargin = 300;
        var rightX = Width - rightMargin;
        const int invSlotHeight = 30;
        var invTotalHeight = Inventory.HotbarSize * invSlotHeight;
        var invStartY = (Height - invTotalHeight) - 250;

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
        WriteControlLine("  F6 - Generate Heightmap", textColor);
        WriteControlLine("  F7 - Generate Biome Map", textColor);
        WriteControlLine("  F8 - Generate Continentalness Map", textColor);
        WriteControlLine("  F9 - Generate Temperature Map", textColor);
        WriteControlLine("  F10 - Generate Humidity Map", textColor);
        WriteControlLine("  F11 - Generate Erosion Map", textColor);
        WriteControlLine("  Left Click - Break Block", textColor);
        WriteControlLine("  Esc - Exit", textColor);
    }

    /// <summary>
    /// Gets the biome name for a given block by querying the cached biome data.
    /// </summary>
    private string GetBiomeNameForBlock(BlockState block)
    {
        if (terrainSystem == null)
            return "Unknown";

        // Query the actual biome from the client-side cached voxel data
        var worldX = (int)block.GlobalPosition.X;
        var worldZ = (int)block.GlobalPosition.Z;

        var biomeId = terrainSystem.GetBiomeAtWorldPos(worldX, worldZ);

        return biomeId.ToString();
    }

    /// <summary>
    /// Generate a biome debug map centered on the player's current position.
    /// Creates a BMP file in the saves directory showing biome distribution.
    /// </summary>
    private const int DebugMapRadiusBlocks = 1536;
    private const int DebugMapCellSizeBlocks = 2;
    private const bool DebugMapMirrorX = true;

    private void GenerateBiomeDebugMap()
    {
        Log.Warn("Biome debug maps are unavailable in the decoupled pipeline (server owns terrain config)");
    }

    private void GenerateClimateDebugMap(ClimateParameter parameter)
    {
        Log.Warn("Climate debug maps are unavailable in the decoupled pipeline (server owns terrain config)");
    }

    private void GenerateHeightDebugMap()
    {
        Log.Warn("Height debug maps are unavailable in the decoupled pipeline (server owns terrain config)");
    }

    public override void Close()
    {
        try { localClient?.Stop(); } catch { }
        
        world?.Close();
        base.Close();
    }

    /// <summary>
    /// Generate surrounding chunk indices based on camera position and view distance.
    /// IMPORTANT: Only returns chunks that have actually been generated/loaded.
    /// </summary>
    private int[] GenerateSurroundingChunkIndices()
    {
        if (camera == null) return [];
        return serverReadyChunkIndices.ToArray();
    }

    private void ApplyChunkDelta(in ChunkDeltaSnapshot delta)
    {
        // Apply unloads first to keep sets consistent.
        foreach (var idx in delta.UnloadedChunkIndices)
        {
            serverReadyChunkIndices.Remove(idx);
            terrainSystem?.UnloadChunk(idx);
        }

        foreach (var idx in delta.LoadedChunkIndices)
        {
            serverReadyChunkIndices.Add(idx);
        }
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

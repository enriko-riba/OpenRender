using OpenRender;
using OpenRender.Components;
using OpenRender.Core;
using OpenRender.Core.Culling;
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
using SpyroGame.Client.Mobs;
using SpyroGame.Client.Terrain;
using SpyroGame.Server.World.Generation;
using SpyroGame.Shared.Commands;
using SpyroGame.Shared.Input;
using SpyroGame.Shared.State;
using SpyroGame.World;
using SpyroGame.World.Registry;

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

    private MobBlockRenderer? mobRenderer;
    private Client.Rendering.DroppedItemRenderer? droppedItemRenderer;
    private MobId? pickedMobId;
    private float pickedMobDistance;
    private MobKind pickedMobKind;
    private PlayerId localPlayerId;
    private HotBar hotBar = default!;

    // Client-side view of server streaming state (chunk indices that are Ready).
    // This is the seam for future remote server chunk streaming.
    private readonly HashSet<int> serverReadyChunkIndices = [];

    private readonly Frustum uiFrustum = new();

    private sealed record DeferredChunkPayload(byte[] Payload, double ExpiresAtSeconds);
    private readonly Dictionary<int, DeferredChunkPayload> deferredChunkPayloads = [];
    private const double DeferredPayloadTtlSeconds = 2.0;

    private PlayerInputState lastSentInputState;
    private bool hasSentInitialInputState;


    private Vector2 mouseCenter;
    private Vector2 lastMousePosition;
    //private bool wasLeftButtonDown;

    private FogUniform defaultFog;
    //private bool defaultFogCaptured;
    private Sprite crosshair = default!;
    private DayNightCycle dayNightCycle = default!;
    private SkyBoxSun skyBox = default!;
    private bool hasAppliedServerWorldTime;
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
        crosshair.SetPosition(clientSize / 2);
        crosshair.Pivot = new(0.5f, 0.5f);

        // Setup day/night cycle and lighting
        dayNightCycle = new DayNightCycle(this);
        dayNightCycle.Tick(0);

        // dirLight field no longer needed - managed by DayNightCycle
        // (Remove the manual AddLight call below)

        // Create skybox
        skyBox = SkyBoxSun.Create(dayNightCycle);
        AddNode(skyBox);

        // Client-side mob renderer (single Corey-textured block per mob).
        mobRenderer ??= new MobBlockRenderer(this);
        droppedItemRenderer ??= new Client.Rendering.DroppedItemRenderer(this);

        // Add terrain rendererchunksperframe
        if (terrainRenderer != null)
        {
            if (terrainRenderer.Scene == null)
            {
                AddNode(terrainRenderer);
            }
        }
        else
        {
            throw new ArgumentNullException("terrain renderer");
        }

        hotBar = HotBar.Create(SceneManager.ClientSize.X / 2, SceneManager.ClientSize.Y - HotBar.Height - 5, Color4.AliceBlue);        
        AddNode(hotBar);
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


        // Update day/night cycle
        if (!hasAppliedServerWorldTime)
        {
            dayNightCycle.Tick(elapsedSeconds);
        }

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
                    ApplyChunkDelta(snap.TickId, snap.Player.Position, chunkDelta);
                }
            }

            if (latestSnap.HasValue)
            {
                player.ApplyServerSnapshot(latestSnap.Value.Player);
            }

            // Apply latest server world time (if any) to day/night.
            WorldTimeSnapshot? latestTime = null;
            while (localClient.TryDequeueWorldTimeSnapshot(out var t))
            {
                latestTime = t;
            }

            if (latestTime.HasValue)
            {
                dayNightCycle.SetTimeOfDaySeconds(latestTime.Value.TimeOfDaySeconds);
                hasAppliedServerWorldTime = true;
            }

            // Apply latest mob snapshot (if any) to the renderer.
            MobStateSnapshot? latestMobSnap = null;
            while (localClient.TryDequeueMobSnapshot(out var mobSnap))
            {
                latestMobSnap = mobSnap;
            }

            if (latestMobSnap.HasValue)
            {
                mobRenderer?.ApplySnapshot(latestMobSnap.Value);
            }

            // Update dropped items
            if (localClient.LastSnapshot.HasValue)
            {
                droppedItemRenderer?.Update(localClient.LastSnapshot.Value.DroppedItems, elapsedSeconds);
            }

            // Smooth rendered position between server ticks.
            player.UpdateClientSmoothing(elapsedSeconds);
        }

        // Apply any received voxel payloads and upload meshes.
        if (localClient != null && terrainSystem != null)
        {
            var nowSeconds = SceneManager.Time;

            // Gameplay streaming budget: keep higher than the loading scene to avoid
            // visible "holes" when FPS drops (e.g., due to mobs) while crossing chunk borders.
            const int maxChunkPayloadsToApplyPerFrame = 16;
            var appliedThisFrame = 0;
            while (localClient.TryDequeueChunkPayload(out var payload))
            {
                // Payloads can arrive out-of-order relative to snapshot deltas.
                // If we apply a payload for a chunk that the server has already unloaded,
                // we can accidentally resurrect stale terrain behind the visible disk.
                if (serverReadyChunkIndices.Contains(payload.ChunkIndex))
                {
                    terrainSystem.ApplyChunkPayloadBytes(payload.ChunkIndex, payload.Payload);
                    appliedThisFrame++;
                }
                else
                {
                    deferredChunkPayloads[payload.ChunkIndex] = new DeferredChunkPayload(payload.Payload, nowSeconds + DeferredPayloadTtlSeconds);
                }

                if (appliedThisFrame >= maxChunkPayloadsToApplyPerFrame)
                {
                    break;
                }
            }

            // Apply deferred payloads for chunks that became ready.
            // Keep the same per-frame budget.
            if (appliedThisFrame < maxChunkPayloadsToApplyPerFrame && deferredChunkPayloads.Count > 0)
            {
                var toRemove = new List<int>(capacity: 8);
                foreach (var kvp in deferredChunkPayloads)
                {
                    if (appliedThisFrame >= maxChunkPayloadsToApplyPerFrame)
                    {
                        break;
                    }

                    var idx = kvp.Key;
                    var entry = kvp.Value;
                    if (serverReadyChunkIndices.Contains(idx))
                    {
                        terrainSystem.ApplyChunkPayloadBytes(idx, entry.Payload);
                        appliedThisFrame++;
                        toRemove.Add(idx);
                    }
                    else
                    {
                        if (nowSeconds >= entry.ExpiresAtSeconds)
                        {
                            toRemove.Add(idx);
                        }
                    }
                }

                foreach (var idx in toRemove)
                {
                    deferredChunkPayloads.Remove(idx);
                }
            }

            terrainSystem.UpdateUploads(maxUploadsPerFrame: 16);
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

        // Update mob picking
        if (mobRenderer != null && camera != null)
        {
            if (mobRenderer.Pick(camera.Position, camera.Front, 5.0f, out var id, out var dist, out var kind))
            {
                pickedMobId = id;
                pickedMobDistance = dist;
                pickedMobKind = kind;
            }
            else
            {
                pickedMobId = null;
                pickedMobDistance = float.MaxValue;
                pickedMobKind = MobKind.Unknown;
            }
        }

        // Handle interactions (Break/Place/Attack) with fresh picking data.
        // Client determines target positions via picking; server executes edits.
        if (localClient != null)
        {
            var blockHit = blockPickingService?.PickedBlock is { } picked;
            var blockDist = blockPickingService?.HitDistance ?? float.MaxValue;
            var mobHit = pickedMobId.HasValue;
            var mobDist = pickedMobDistance;

            // Prioritize mob if hit and closer (or block not hit)
            // User requested: "win over voxel picking as we don't care that much about voxels when hostale mobs are near"
            // So we strictly prefer mob if it's closer.

            var prioritizeMob = false;
            if (mobHit)
            {
                if (!blockHit)
                {
                    prioritizeMob = true;
                }
                else
                {
                    // Check if mob is hostile
                    var isHostile = pickedMobKind is MobKind.Zombie or MobKind.Skeleton;

                    // Check if block is non-solid (e.g. grass, flowers)
                    var blockId = blockPickingService!.PickedBlock!.Value.Block;
                    var isNonSolidBlock = false;
                    if (BlockRegistry.Blocks.TryGetValue(blockId, out var blockDef))
                    {
                        isNonSolidBlock = !blockDef.IsSolid;
                    }

                    if (isHostile && isNonSolidBlock)
                    {
                        prioritizeMob = true; // Hostile priority over non-solid
                    }
                    else if (mobDist < blockDist)
                    {
                        prioritizeMob = true; // Standard closer check
                    }
                }
            }

            if (prioritizeMob)
            {
                if (SceneManager.MouseState.IsButtonPressed(MouseButton.Left))
                {
                    // Attack!
                    localClient.SendAttack(pickedMobId!.Value);
                    // Add a small cooldown or visual feedback here if needed
                }
            }
            else if (blockHit)
            {
                var pickedBlock = blockPickingService!.PickedBlock!.Value;

                if (SceneManager.MouseState.IsButtonPressed(MouseButton.Left))
                {
                    // Predict locally for responsiveness: update inventory, collision, and picking immediately.
                    if (!pickedBlock.Block.IsAir())
                    {
                        player.Inventory.AddItem((ItemId)pickedBlock.Block);
                        terrainSystem?.TryApplyPredictedBlockEdit(pickedBlock.GlobalPosition, BlockId.Air);
                        blockPickingService.Invalidate();
                        blockPickingService.ForceUpdate(SceneManager.Time, camera!, maxDistance: 5.0f);
                    }

                    localClient.Send(new BreakBlockCommand(pickedBlock.GlobalPosition));
                }

                if (SceneManager.MouseState.IsButtonPressed(MouseButton.Right))
                {
                    var item = player.Inventory.GetSelectedItem();
                    if (!item.IsEmpty && ItemRegistry.Items.TryGetValue(item.Item, out var itemDef) && itemDef is BlockItem blockItem)
                    {
                        var hitNormal = blockPickingService.HitNormal;
                        var placePos = pickedBlock.GlobalPosition + new Vector3i((int)hitNormal.X, (int)hitNormal.Y, (int)hitNormal.Z);

                        // Predict locally: consume item + set voxel so the feedback is instant.
                        if (terrainSystem?.TryApplyPredictedBlockEdit(placePos, blockItem.BlockId) == true)
                        {
                            player.Inventory.TryConsumeSelectedItem();
                            blockPickingService.Invalidate();
                            blockPickingService.ForceUpdate(SceneManager.Time, camera!, maxDistance: 5.0f);
                        }

                        localClient.Send(new PlaceBlockCommand(placePos, blockItem.BlockId));
                    }
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

        var clientActiveChunks = terrainSystem?.ActiveChunkCount ?? 0;
        var clientReadyChunks = terrainSystem?.ReadyChunkCount ?? 0;
        var clientPendingMeshes = terrainSystem?.PendingMeshCount ?? 0;
        var visibleCullingChunks = 0;

        // UI-only frustum culling approximation for terrain.
        // Uses the set of Ready chunks (chunks that can actually render).
        int totalCullingChunks;
        if (terrainSystem != null && camera != null)
        {
            uiFrustum.Update(camera);
            var planes = uiFrustum.Planes;

            var readyIndices = terrainSystem.GetReadyChunkIndicesSnapshot();
            totalCullingChunks = readyIndices.Length;
            for (var i = 0; i < readyIndices.Length; i++)
            {
                var idx = readyIndices[i];
                var origin = VoxelHelper.GetChunkPositionGlobal(idx);
                var min = new Vector3(origin.X, 0, origin.Z);
                var max = new Vector3(origin.X + VoxelHelper.ChunkSideSize, VoxelHelper.ChunkYSize, origin.Z + VoxelHelper.ChunkSideSize);
                if (CullingHelper.IsAabbCenterInFrustum((min, max), planes))
                {
                    visibleCullingChunks++;
                }
            }
        }
        else
        {
            totalCullingChunks = clientReadyChunks;
            visibleCullingChunks = clientReadyChunks;
        }

        var culledCullingChunks = Math.Max(0, totalCullingChunks - visibleCullingChunks);

        WriteLine("Rendering:", highlightColor);
        WriteLine($"  GPU Ready: {readyChunks:N0}", textColor);
        WriteLine($"  Frustum: culled {culledCullingChunks:N0} | visible {visibleCullingChunks:N0} | total {totalCullingChunks:N0}", textColor);
        WriteLine($"  Client Chunks: active {clientActiveChunks:N0} | ready {clientReadyChunks:N0} | pending {clientPendingMeshes:N0}", textColor);
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

        // Picked Mob
        if (pickedMobId.HasValue)
        {
            WriteLine($"Picked Mob: ID={pickedMobId.Value.Value} Dist={pickedMobDistance:F1}", new Vector3(1.0f, 0.5f, 0.5f));
            WriteLine("", textColor);
        }

        // Controls below Picked Block (left side)
        WriteLine("Controls:", highlightColor);
        WriteLine("  WASD - Move", textColor);
        WriteLine("  Shift/Ctrl - Up/Down", textColor);
        WriteLine("  Mouse - Look", textColor);
        WriteLine("  F - Toggle Ghost/Physics", textColor);
        WriteLine("  F3 - Toggle Biome Debug", textColor);
        WriteLine("  F5 - Toggle Wireframe", textColor);
        WriteLine("  Left Click - Break Block", textColor);
        WriteLine("  Esc - Exit", textColor);

        // === RIGHT SIDE: Inventory ===
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

            var content = item.IsEmpty ? "Empty" : $"{item.Item} x{item.Count}";
            if (isSelected) content = $"> {content}";

            textRenderer.Render(content, 22, rightX, invStartY + i * invSlotHeight, color);
        }
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

    public override void Close()
    {
        try { localClient?.Stop(); } catch { }
        droppedItemRenderer?.Dispose();

        world?.Close();
        base.Close();
    }

    private void ApplyChunkDelta(ulong tickId, Vector3 snapshotPlayerPosition, in ChunkDeltaSnapshot delta)
    {
        // Apply unloads first to keep sets consistent.
        foreach (var idx in delta.UnloadedChunkIndices)
        {
            serverReadyChunkIndices.Remove(idx);
            deferredChunkPayloads.Remove(idx);
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
        crosshair.SetPosition(clientSize / 2);
        crosshair.Pivot = new(0.5f, 0.5f);

        hotBar.SetPosition(new(clientSize.X / 2, clientSize.Y - 5));
        mouseCenter = clientSize / 2;
    }
}

using OpenRender.Core.Rendering;
using OpenTK.Mathematics;
using SpyroGame.Shared.Abstractions;
using SpyroGame.Shared.Commands;
using SpyroGame.Shared.Input;
using SpyroGame.Shared.State;
using SpyroGame.World;

namespace SpyroGame.Server;

/// <summary>
/// In-process server used to prepare for client/server segregation.
/// Designed to behave like a standalone server: players are keyed by <see cref="PlayerId"/>,
/// and each player receives its own slice of state.
/// </summary>
public sealed class LocalGameServer : IGameServer, IChunkPayloadSource, ILoadingProgressSource
{
    private sealed class PendingChunkPayloadQueue
    {
        private readonly Queue<int> queue = new();
        private readonly HashSet<int> set = [];

        public int Count => queue.Count;

        public void Enqueue(int chunkIndex)
        {
            if (set.Add(chunkIndex))
            {
                queue.Enqueue(chunkIndex);
            }
        }

        public bool TryDequeue(out int chunkIndex)
        {
            while (queue.Count > 0)
            {
                chunkIndex = queue.Dequeue();
                set.Remove(chunkIndex);
                return true;
            }

            chunkIndex = -1;
            return false;
        }
    }

    private readonly VoxelWorld world;
    private readonly SpyroGame.Server.Streaming.ChunkStreamingManager streamingManager;
    private readonly Vector3 spawnPosition;

    private ulong tickId;
    private double serverTimeSeconds;

    private readonly Dictionary<PlayerId, Player> players = [];
    private readonly Dictionary<PlayerId, HashSet<int>> lastVisibleReadyChunksByPlayer = [];
    private readonly Dictionary<PlayerId, GameStateSnapshot> lastSnapshotByPlayer = [];
    private readonly Dictionary<PlayerId, PendingChunkPayloadQueue> pendingChunkPayloadsByPlayer = [];

    private readonly Dictionary<PlayerId, HashSet<int>> initialChunkTargetByPlayer = [];
    private readonly Dictionary<PlayerId, HashSet<int>> initialChunksSentByPlayer = [];
    private readonly HashSet<PlayerId> gameStartSent = [];

    public LocalGameServer(VoxelWorld world, SpyroGame.Server.Streaming.ChunkStreamingManager streamingManager, Vector3 spawnPosition)
    {
        this.world = world;
        this.streamingManager = streamingManager;
        this.spawnPosition = spawnPosition;

        this.streamingManager.Initialize(world.Seed);
    }

    public void Submit(PlayerId playerId, PlayerInputCommand input)
    {
        EnsurePlayer(playerId).ApplyInput(input);
    }

    public void Connect(PlayerId playerId)
    {
        // Creates the player immediately so streaming can start before first input.
        EnsurePlayer(playerId);
    }

    public void Submit(PlayerId playerId, BreakBlockCommand command)
    {
        EnsurePlayer(playerId).TryBreakBlock(command.GlobalPosition);
    }

    public void Submit(PlayerId playerId, PlaceBlockCommand command)
    {
        EnsurePlayer(playerId).TryPlaceBlock(command.GlobalPosition, command.Block);
    }

    public void Tick(double elapsedSeconds)
    {
        serverTimeSeconds += Math.Max(0.0, elapsedSeconds);
        tickId++;

        // Tick all connected players and update their streaming focal positions.
        foreach (var kvp in players)
        {
            var playerId = kvp.Key;
            var player = kvp.Value;

            // Do not run physics before the initial terrain/collision set is ready.
            // Otherwise gravity can move the player below the surface during the loading scene,
            // and the player will "spawn" underground once terrain data arrives.
            if (gameStartSent.Contains(playerId))
            {
                player.Simulate(elapsedSeconds);
            }

            // Always update streaming focal position so initial chunks can load.
            streamingManager.UpdatePlayer(playerId, player.Camera.Position);
        }

        // Server is authoritative for terrain streaming/generation.
        streamingManager.Tick(elapsedSeconds);

        // Drain global changed-chunk queue once per tick.
        var changedChunkIndices = new List<int>();
        while (streamingManager.TryDequeueChangedChunk(out var changedIdx))
        {
            changedChunkIndices.Add(changedIdx);
        }

        // Build per-player snapshots and per-player visible chunk deltas.
        foreach (var kvp in players)
        {
            var playerId = kvp.Key;
            var player = kvp.Value;

            // IMPORTANT: Treat chunk deltas as authoritative streaming state (loaded+ready),
            // not as a secondary visibility/culling filter. Using a filtered set here can
            // cause the client to stop rendering chunks that are still loaded, producing
            // holes during movement (especially when moving backwards).
            var currentVisibleReady = streamingManager.GetReadyChunksForPlayer(playerId);
            var lastVisible = GetOrCreateLastVisibleSet(playerId);

            List<int>? loaded = null;
            foreach (var idx in currentVisibleReady)
            {
                if (!lastVisible.Contains(idx))
                {
                    loaded ??= [];
                    loaded.Add(idx);
                }
            }

            List<int>? unloaded = null;
            foreach (var idx in lastVisible)
            {
                if (!currentVisibleReady.Contains(idx))
                {
                    unloaded ??= [];
                    unloaded.Add(idx);
                }
            }

            ChunkDeltaSnapshot? chunkDelta = null;
            if (loaded != null || unloaded != null)
            {
                chunkDelta = new ChunkDeltaSnapshot(
                    LoadedChunkIndices: loaded?.ToArray() ?? [],
                    UnloadedChunkIndices: unloaded?.ToArray() ?? []);
            }

            // Queue payloads for newly loaded chunks.
            if (loaded != null)
            {
                var pending = GetOrCreatePendingPayloadQueue(playerId);
                foreach (var idx in loaded)
                {
                    pending.Enqueue(idx);
                }
            }

            // Establish the initial target set once: the player's desired set.
            // This is used to gate the server's "game start" signal.
            // IMPORTANT: latch desired, not ready. At connect time ready is often 0, which can deadlock loading.
            if (!initialChunkTargetByPlayer.ContainsKey(playerId))
            {
                if (streamingManager.TryGetDesiredChunksForPlayer(playerId, out var desired) && desired.Count > 0)
                {
                    initialChunkTargetByPlayer[playerId] = desired;
                    initialChunksSentByPlayer[playerId] = [];
                }
            }

            // Queue payloads for changed chunks that this player currently has loaded.
            if (changedChunkIndices.Count > 0)
            {
                var pending = GetOrCreatePendingPayloadQueue(playerId);
                foreach (var idx in changedChunkIndices)
                {
                    if (currentVisibleReady.Contains(idx))
                    {
                        pending.Enqueue(idx);
                    }
                }
            }

            lastVisible.Clear();
            lastVisible.UnionWith(currentVisibleReady);

            lastSnapshotByPlayer[playerId] = BuildSnapshot(player, chunkDelta);
        }
    }

    public bool TryDequeueOutgoingChunkPayload(PlayerId playerId, out int chunkIndex, out byte[] payload)
    {
        payload = [];
        chunkIndex = -1;

        if (!pendingChunkPayloadsByPlayer.TryGetValue(playerId, out var queue) || queue.Count == 0)
        {
            return false;
        }

        while (queue.TryDequeue(out var idx))
        {
            // If the player moved on and this chunk is no longer in the desired+ready set,
            // drop it instead of sending late payloads that the client will ignore anyway.
            if (!streamingManager.IsChunkReadyForPlayer(playerId, idx))
            {
                continue;
            }

            if (streamingManager.TryGetChunkPayloadBytes(idx, out var bytes))
            {
                chunkIndex = idx;
                payload = bytes;

                // Track initial chunk completion.
                if (initialChunkTargetByPlayer.TryGetValue(playerId, out var target) &&
                    initialChunksSentByPlayer.TryGetValue(playerId, out var sent) &&
                    target.Contains(idx))
                {
                    sent.Add(idx);
                }
                return true;
            }
        }

        return false;
    }

    public bool TryGetLoadingProgress(PlayerId playerId, out LoadingProgressSnapshot progress)
        => streamingManager.TryGetLoadingProgress(playerId, out progress);

    public bool TryGetInitialChunkStatus(PlayerId playerId, out int sent, out int target)
    {
        sent = 0;
        target = 0;

        if (!initialChunkTargetByPlayer.TryGetValue(playerId, out var t) || !initialChunksSentByPlayer.TryGetValue(playerId, out var s))
        {
            return false;
        }

        target = t.Count;
        sent = s.Count;
        return true;
    }

    public bool ShouldSendGameStart(PlayerId playerId)
    {
        if (gameStartSent.Contains(playerId))
        {
            return false;
        }

        if (!initialChunkTargetByPlayer.TryGetValue(playerId, out var target) || !initialChunksSentByPlayer.TryGetValue(playerId, out var sent))
        {
            return false;
        }

        // Guard: require at least some target chunks.
        if (target.Count == 0)
        {
            return false;
        }

        if (sent.Count >= target.Count)
        {
            gameStartSent.Add(playerId);
            return true;
        }

        return false;
    }

    public bool TryGetSnapshot(PlayerId playerId, out GameStateSnapshot snapshot)
        => lastSnapshotByPlayer.TryGetValue(playerId, out snapshot);

    private Player EnsurePlayer(PlayerId playerId)
    {
        if (players.TryGetValue(playerId, out var existing))
        {
            return existing;
        }

        // Server-side camera: not used for rendering, but used for authoritative orientation/movement.
        ICamera serverCamera = new CameraFps(spawnPosition, aspectRatio: 1.0f, nearPlane: 0.1f, farPlane: VoxelHelper.FarPlane)
        {
            MaxFov = 70
        };

        var player = new Player(serverCamera, spawnPosition, world, streamingManager);
        players[playerId] = player;
        lastVisibleReadyChunksByPlayer[playerId] = [];
        pendingChunkPayloadsByPlayer[playerId] = new PendingChunkPayloadQueue();
        lastSnapshotByPlayer[playerId] = BuildSnapshot(player, chunkDelta: null);
        return player;
    }

    private PendingChunkPayloadQueue GetOrCreatePendingPayloadQueue(PlayerId playerId)
    {
        if (pendingChunkPayloadsByPlayer.TryGetValue(playerId, out var queue))
        {
            return queue;
        }

        queue = new PendingChunkPayloadQueue();
        pendingChunkPayloadsByPlayer[playerId] = queue;
        return queue;
    }

    private HashSet<int> GetOrCreateLastVisibleSet(PlayerId playerId)
    {
        if (lastVisibleReadyChunksByPlayer.TryGetValue(playerId, out var set))
        {
            return set;
        }

        set = [];
        lastVisibleReadyChunksByPlayer[playerId] = set;
        return set;
    }

    private GameStateSnapshot BuildSnapshot(Player player, ChunkDeltaSnapshot? chunkDelta)
    {
        var a = player.Attributes;
        var attrSnap = new PlayerAttributesSnapshot(
            MaxHealth: a.MaxHealth,
            Health: a.Health,
            MaxFood: a.MaxFood,
            Food: a.Food,
            Saturation: a.Saturation,
            Exhaustion: a.Exhaustion);

        var pSnap = new PlayerSnapshot(
            Position: player.Position,
            Direction: player.Direction,
            Velocity: player.Velocity,
            IsGrounded: player.IsGrounded,
            IsGhostMode: player.IsGhostMode,
            SelectedHotbarSlot: player.Inventory.SelectedSlot,
            Attributes: attrSnap,
            Inventory: player.Inventory.CreateSnapshot());

        return new GameStateSnapshot(
            TickId: tickId,
            ServerTimeSeconds: serverTimeSeconds,
            Player: pSnap,
            ChunkDelta: chunkDelta);
    }
}

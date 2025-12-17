using OpenRender.Core.Rendering;
using OpenTK.Mathematics;
using SpyroGame.Server.Mobs;
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

    private readonly MobManager mobManager = new();
    private readonly MobSpawnSystem mobSpawnSystem = new(
        new MobSpawnSystem.Settings(
            NoSpawnRadiusBlocks: 12,
            SpawnRadiusBlocks: 56,
            MaxSpawnAttemptsPerTick: 4));

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

        // Keep mobs scoped to the active (loaded/ready) region.
        // For Phase 1, we consider a mob active if its chunk is ready for ANY connected player.
        var anyReadyChunks = new HashSet<int>();
        foreach (var playerId in players.Keys)
        {
            var ready = streamingManager.GetReadyChunksForPlayer(playerId);
            anyReadyChunks.UnionWith(ready);
        }

        if (anyReadyChunks.Count > 0 && mobManager.Mobs.Count > 0)
        {
            List<MobId>? toRemove = null;
            foreach (var mob in mobManager.Mobs.Values)
            {
                var chunkIdx = GetChunkIndexFromWorldPos(mob.Position);
                if (!anyReadyChunks.Contains(chunkIdx))
                {
                    toRemove ??= [];
                    toRemove.Add(mob.Id);
                }
            }

            if (toRemove != null)
            {
                foreach (var id in toRemove)
                {
                    mobManager.Remove(id);
                }
            }
        }

        // Phase 1 spawning: sample candidate positions around players within their ready chunks.
        if (players.Count > 0)
        {
            var playerPositions = new (PlayerId PlayerId, Vector3 Position)[players.Count];
            var i = 0;
            foreach (var kvp in players)
            {
                playerPositions[i++] = (kvp.Key, kvp.Value.Position);
            }

            mobSpawnSystem.Tick(
                tickId,
                serverTimeSeconds,
                playerPositions,
                world,
                isChunkReadyForPlayer: streamingManager.IsChunkReadyForPlayer,
                mobManager);
        }

        // Minimal Phase 1 movement: slow wandering on the surface.
        // Keep mobs within ready chunks for at least one player.
        if (mobManager.Mobs.Count > 0 && anyReadyChunks.Count > 0)
        {
            var dt = (float)Math.Clamp(elapsedSeconds, 0.0, 0.1);
            foreach (var mob in mobManager.Mobs.Values)
            {
                if (mob.IsDead) continue;

                var mobChunk = GetChunkIndexFromWorldPos(mob.Position);
                if (!anyReadyChunks.Contains(mobChunk))
                {
                    continue;
                }

                // Occasionally change heading.
                if (Random.Shared.NextDouble() < 0.02)
                {
                    mob.YawDegrees += (float)(Random.Shared.NextDouble() * 120.0 - 60.0);
                    if (mob.YawDegrees < 0) mob.YawDegrees += 360;
                    if (mob.YawDegrees >= 360) mob.YawDegrees -= 360;
                }

                var yawRad = MathHelper.DegreesToRadians(mob.YawDegrees);
                var dir = new Vector3(MathF.Sin(yawRad), 0, MathF.Cos(yawRad));
                var speed = mob.Definition.WalkSpeed * 0.25f;

                var next = mob.Position + dir * (speed * dt);

                // Snap to surface height in the destination column.
                var wx = (int)MathF.Floor(next.X);
                var wz = (int)MathF.Floor(next.Z);
                if (wx < 0 || wz < 0 || wx > VoxelHelper.MaxBlockPositionXZ || wz > VoxelHelper.MaxBlockPositionXZ)
                {
                    continue;
                }

                var nextChunk = VoxelHelper.GetChunkIndexFromPositionGlobal(new Vector3i(wx, 0, wz));
                if (!anyReadyChunks.Contains(nextChunk))
                {
                    continue;
                }

                var chunk = world[nextChunk];
                if (chunk is null || !chunk.HasCollisionData)
                {
                    continue;
                }

                var origin = VoxelHelper.GetChunkPositionGlobal(nextChunk);
                var lx = wx - origin.X;
                var lz = wz - origin.Z;
                if ((uint)lx >= (uint)VoxelHelper.ChunkSideSize || (uint)lz >= (uint)VoxelHelper.ChunkSideSize)
                {
                    continue;
                }

                var heightTopFace = chunk.GetTerrainHeightAt(lx, lz);
                var startY = Math.Clamp(heightTopFace - 1, 0, VoxelHelper.MaxBlockPositionY);
                if (!TryFindMobGroundY(world, wx, wz, startY, out var groundY))
                {
                    continue;
                }

                var spawnY = groundY + 1;
                if (!HasMobHeadroom(world, wx, spawnY, wz))
                {
                    continue;
                }
                next.Y = spawnY + 0.55f;
                mob.Position = next;
                mob.Velocity = dir * speed;
                mob.OnGround = true;
            }
        }

        // Drain global changed-chunk queue once per tick.
        var changedChunkIndices = new List<int>();
        while (streamingManager.TryDequeueChangedChunk(out var changedIdx))
        {
            changedChunkIndices.Add(changedIdx);
        }

        // Maintain per-player bookkeeping.
        foreach (var kvp in players)
        {
            var playerId = kvp.Key;
            var player = kvp.Value;

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

            // Queue payloads for changed chunks that the client has already been told are loaded.
            // This avoids sending updates for chunks the client hasn't received a "load" delta for yet.
            if (changedChunkIndices.Count > 0 && lastVisibleReadyChunksByPlayer.TryGetValue(playerId, out var lastSentReady))
            {
                var pending = GetOrCreatePendingPayloadQueue(playerId);
                foreach (var idx in changedChunkIndices)
                {
                    if (lastSentReady.Contains(idx))
                    {
                        pending.Enqueue(idx);
                    }
                }
            }

            // Store latest player-only snapshot; chunk deltas are computed when a snapshot is requested.
            lastSnapshotByPlayer[playerId] = BuildSnapshot(player, chunkDelta: null);
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
    {
        snapshot = default;

        if (!players.TryGetValue(playerId, out var player))
        {
            return false;
        }

        // Compute delta against the last snapshot that was actually SENT to the client.
        // IMPORTANT: the host may tick multiple times per loop when catching up;
        // if we compute deltas per server tick, the client can miss intermediate unloads.
        var currentReady = streamingManager.GetReadyChunksForPlayer(playerId);
        var lastSentReady = GetOrCreateLastVisibleSet(playerId);

        List<int>? loaded = null;
        foreach (var idx in currentReady)
        {
            if (!lastSentReady.Contains(idx))
            {
                loaded ??= [];
                loaded.Add(idx);
            }
        }

        List<int>? unloaded = null;
        foreach (var idx in lastSentReady)
        {
            if (!currentReady.Contains(idx))
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

        // Queue initial payloads for newly loaded chunks so the client receives data promptly.
        if (loaded != null)
        {
            var pending = GetOrCreatePendingPayloadQueue(playerId);
            foreach (var idx in loaded)
            {
                pending.Enqueue(idx);
            }
        }

        // Advance last-sent set.
        lastSentReady.Clear();
        lastSentReady.UnionWith(currentReady);

        snapshot = BuildSnapshot(player, chunkDelta);
        lastSnapshotByPlayer[playerId] = snapshot;
        return true;
    }

    public bool TryGetMobSnapshot(PlayerId playerId, out MobStateSnapshot snapshot)
    {
        snapshot = default;

        if (!players.ContainsKey(playerId))
        {
            return false;
        }

        var ready = streamingManager.GetReadyChunksForPlayer(playerId);
        if (ready.Count == 0 || mobManager.Mobs.Count == 0)
        {
            snapshot = new MobStateSnapshot(tickId, serverTimeSeconds, Mobs: []);
            return true;
        }

        var list = new List<MobSnapshot>(capacity: Math.Min(mobManager.Mobs.Count, 64));
        foreach (var mob in mobManager.Mobs.Values)
        {
            var idx = GetChunkIndexFromWorldPos(mob.Position);
            if (ready.Contains(idx))
            {
                list.Add(mob.ToSnapshot());
            }
        }

        snapshot = new MobStateSnapshot(tickId, serverTimeSeconds, [.. list]);
        return true;
    }

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

    private static int GetChunkIndexFromWorldPos(in Vector3 worldPos)
    {
        // Chunk index is based on XZ only; Y is ignored.
        // Use floor to match block-space semantics.
        var x = (int)MathF.Floor(worldPos.X);
        var z = (int)MathF.Floor(worldPos.Z);
        return VoxelHelper.GetChunkIndexFromPositionGlobal(new Vector3i(x, 0, z));
    }

    private static bool TryFindMobGroundY(VoxelWorld world, int wx, int wz, int startY, out int groundY)
    {
        groundY = 0;
        const int maxScan = 48;
        var yMin = Math.Max(0, startY - maxScan);

        for (var y = startY; y >= yMin; y--)
        {
            var b = world.GetBlockByPositionGlobalSafe(wx, y, wz);
            if (b is null) continue;

            if (IsSuitableMobGround(b.Value.Block))
            {
                groundY = y;
                return true;
            }
        }

        return false;
    }

    private static bool IsSuitableMobGround(BlockId block)
    {
        if (!block.IsSolid()) return false;
        if (block.IsLiquid()) return false;
        if (!block.IsOpaque()) return false;

        return block is not (BlockId.OakLog or BlockId.BirchLog or BlockId.SpruceLog or BlockId.JungleLog)
               and not (BlockId.OakLeaves or BlockId.BirchLeaves or BlockId.SpruceLeaves or BlockId.JungleLeaves);
    }

    private static bool HasMobHeadroom(VoxelWorld world, int wx, int spawnY, int wz)
    {
        var a0 = world.GetBlockByPositionGlobalSafe(wx, spawnY, wz);
        if (a0 is not null && (!a0.Value.Block.IsReplaceable() || a0.Value.Block.IsLiquid()))
        {
            return false;
        }

        var a1 = world.GetBlockByPositionGlobalSafe(wx, spawnY + 1, wz);
        if (a1 is not null && (!a1.Value.Block.IsReplaceable() || a1.Value.Block.IsLiquid()))
        {
            return false;
        }

        return true;
    }
}

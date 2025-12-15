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
public sealed class LocalGameServer : IGameServer
{
    private readonly VoxelWorld world;
    private readonly ChunkStreamingManager streamingManager;
    private readonly Vector3 spawnPosition;

    private ulong tickId;
    private double serverTimeSeconds;

    private readonly Dictionary<PlayerId, Player> players = [];
    private readonly Dictionary<PlayerId, HashSet<int>> lastVisibleReadyChunksByPlayer = [];
    private readonly Dictionary<PlayerId, GameStateSnapshot> lastSnapshotByPlayer = [];

    public LocalGameServer(VoxelWorld world, ChunkStreamingManager streamingManager, Vector3 spawnPosition)
    {
        this.world = world;
        this.streamingManager = streamingManager;
        this.spawnPosition = spawnPosition;
    }

    public void Submit(PlayerId playerId, PlayerInputCommand input)
    {
        EnsurePlayer(playerId).ApplyInput(input);
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

        // Tick all connected players.
        foreach (var kvp in players)
        {
            kvp.Value.Simulate(elapsedSeconds);
        }

        // Server is authoritative for terrain streaming/generation.
        // NOTE: Current streaming manager is global (shared) and uses a single focal point.
        // For now we drive it from the first connected player; future multiplayer will stream the union.
        if (players.Count > 0)
        {
            var primary = players.Values.First();
            streamingManager.Update(primary.Camera.Position);
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
            var currentVisibleReady = GetReadyChunkIndices();
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

            lastVisible.Clear();
            lastVisible.UnionWith(currentVisibleReady);

            lastSnapshotByPlayer[playerId] = BuildSnapshot(player, chunkDelta);
        }
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
        lastSnapshotByPlayer[playerId] = BuildSnapshot(player, chunkDelta: null);
        return player;
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

    private HashSet<int> GetReadyChunkIndices()
    {
        // Current architecture uses a single in-process streaming manager.
        // The authoritative truth for what can be rendered is the set of Ready chunks.
        // Client-side culling (frustum, LOD, etc) can happen independently.
        var result = new HashSet<int>();
        foreach (var chunk in streamingManager.GetReadyChunks())
        {
            result.Add(chunk.ChunkIndex);
        }
        return result;
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
            Attributes: attrSnap);

        return new GameStateSnapshot(
            TickId: tickId,
            ServerTimeSeconds: serverTimeSeconds,
            Player: pSnap,
            ChunkDelta: chunkDelta);
    }
}

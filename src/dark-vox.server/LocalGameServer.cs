using OpenTK.Mathematics;
using DarkVox.Server.Combat;
using DarkVox.Server.Gameplay;
using DarkVox.Server.Mobs;
using DarkVox.Server.Persistence;
using DarkVox.Shared.Abstractions;
using DarkVox.Shared.Commands;
using DarkVox.Shared.Gameplay;
using DarkVox.Shared.Input;
using DarkVox.Shared.Net;
using System.Collections.Concurrent;
using DarkVox.Shared.State;
using System.Runtime.InteropServices;
using DarkVox.Shared.World;
using DarkVox.World;
using DarkVox.Shared.Gameplay.Crafting;
namespace DarkVox.Server;

/// <summary>
/// In-process server used to prepare for client/server segregation.
/// and each player receives its own slice of state.
/// </summary>
public sealed class LocalGameServer : IGameServer, IChunkPayloadSource, ILoadingProgressSource, ICombatEventSource
{
    private readonly ILog log;
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

    private void EnqueueCombatEvent(PlayerId playerId, CombatEventSnapshot combatEvent)
    {
        var queue = combatEventsByPlayer.GetOrAdd(playerId, static _ => new ConcurrentQueue<CombatEventSnapshot>());
        queue.Enqueue(combatEvent);
    }

    public bool TryDequeueCombatEvent(PlayerId playerId, out CombatEventSnapshot combatEvent)
    {
        if (combatEventsByPlayer.TryGetValue(playerId, out var queue) && queue.TryDequeue(out combatEvent))
        {
            return true;
        }

        combatEvent = default;
        return false;
    }

    private readonly VoxelWorld world;
    private readonly Streaming.ChunkStreamingManager streamingManager;
    private readonly Vector3 spawnPosition;

    private ulong tickId;
    private double serverTimeSeconds;

    private readonly WorldTimeService worldTime = new();

    private readonly MobManager mobManager = new();
    private readonly MobSpawnSystem mobSpawnSystem;
    private readonly MobPhysicsSystem mobPhysicsSystem;
    private readonly MobAiSystem mobAiSystem;
    private readonly Items.DroppedItemManager droppedItemManager = new();
    private readonly CombatSystem combatSystem;

    private readonly ConcurrentDictionary<PlayerId, ConcurrentQueue<CombatEventSnapshot>> combatEventsByPlayer = new();

    private readonly Dictionary<PlayerId, Player> players = [];
    private readonly Dictionary<PlayerId, HashSet<int>> lastVisibleReadyChunksByPlayer = [];
    private readonly Dictionary<PlayerId, GameStateSnapshot> lastSnapshotByPlayer = [];
    private readonly Dictionary<PlayerId, PendingChunkPayloadQueue> pendingChunkPayloadsByPlayer = [];

    private readonly Dictionary<PlayerId, HashSet<int>> initialChunkTargetByPlayer = [];
    private readonly Dictionary<PlayerId, HashSet<int>> initialChunksSentByPlayer = [];
    private readonly HashSet<PlayerId> gameStartSent = [];

    // Used to detect newly-ready chunks (union across players) for deterministic per-chunk spawning.
    private readonly HashSet<int> lastAnyReadyChunks = [];

    /// <summary>Gets the performance metrics for terrain generation/meshing.</summary>
    public ChunkProcessingMetrics? Metrics => streamingManager.Metrics;

#if DEBUG
    public bool TryGetPlayerForTests(PlayerId playerId, out Player player) => players.TryGetValue(playerId, out player!);
#endif

    public LocalGameServer(VoxelWorld world, Streaming.ChunkStreamingManager streamingManager, Vector3 spawnPosition, ILog? log = null)
    {
        this.log = log ?? NullLog.Instance;
        this.world = world;
        this.streamingManager = streamingManager;
        this.spawnPosition = spawnPosition;

        this.streamingManager.Initialize(world.Seed);

        mobSpawnSystem = new MobSpawnSystem(
            new MobSpawnSystem.Settings(
                NoSpawnRadiusBlocks: 16,
                SpawnRadiusBlocks: 64,
                MaxSpawnAttemptsPerTick: 4,
                Log: this.log));

        mobPhysicsSystem = new MobPhysicsSystem(world);
        mobAiSystem = new MobAiSystem(world);
        combatSystem = new CombatSystem(droppedItemManager);
    }

    public void Submit(PlayerId playerId, PlayerInputCommand input) => EnsurePlayer(playerId).ApplyInput(input);

    public void Connect(PlayerId playerId) =>
        // Creates the player immediately so streaming can start before first input.
        EnsurePlayer(playerId);

    public void Submit(PlayerId playerId, BreakBlockCommand command) => EnsurePlayer(playerId).TryBreakBlock(command.GlobalPosition);

    public void Submit(PlayerId playerId, PlaceBlockCommand command) => EnsurePlayer(playerId).TryPlaceBlock(command.GlobalPosition, command.Block);

    public void Submit(PlayerId playerId, EatFoodCommand command)
    {
        if (!players.TryGetValue(playerId, out var player)) return;
        player.TryEatFood();
    }

    public void Submit(PlayerId playerId, InventoryMoveCommand command)
    {
        if (!players.TryGetValue(playerId, out var player)) return;
        player.Inventory.TryMoveItem(command.SourceSlot, command.TargetSlot, command.Count);
    }

    public void Submit(PlayerId playerId, ReturnToStorageCommand command)
    {
        if (!players.TryGetValue(playerId, out var player)) return;
        
        var slot = command.HotbarSlot;
        if (slot < 0 || slot >= Inventory.HotbarSize) return;
        
        var item = player.Inventory.GetItem(slot);
        if (item.IsEmpty) return;
        
        // Clear the hotbar slot and return item to storage
        player.Inventory.SetItem(slot, default);
        player.Inventory.ReturnItemToStorage(item);
    }

    public void Submit(PlayerId playerId, ContainerMoveCommand command)
    {
        if (!players.TryGetValue(playerId, out var player)) return;

        // For now, only allow moves between inventory storage and the 2x2 crafting grid.
        // Hotbar is treated as a shortcut bar and is not craftable directly.
        if (command.SourceContainer == ItemContainer.Inventory && command.SourceSlot < Inventory.HotbarSize) return;
        if (command.TargetContainer == ItemContainer.Inventory && command.TargetSlot < Inventory.HotbarSize) return;

        if (command.SourceContainer == ItemContainer.Inventory && command.TargetContainer == ItemContainer.Crafting)
        {
            TryMoveInventoryToCrafting(player, command.SourceSlot, command.TargetSlot, command.Count);
        }
        else if (command.SourceContainer == ItemContainer.Crafting && command.TargetContainer == ItemContainer.Inventory)
        {
            TryMoveCraftingToInventory(player, command.SourceSlot, command.TargetSlot, command.Count);
        }
        else if (command.SourceContainer == ItemContainer.Crafting && command.TargetContainer == ItemContainer.Crafting)
        {
            TryMoveWithinCrafting(player, command.SourceSlot, command.TargetSlot, command.Count);
        }

        player.Crafting.RecomputeResultPreview();
    }

    public void Submit(PlayerId playerId, CraftFromGridCommand command)
    {
        if (!players.TryGetValue(playerId, out var player)) return;

        // Atomic craft: do nothing if inventory can't accept the output.
        var before = player.Crafting.BuildSnapshot();

        if (!player.Crafting.TryConsumeForRecipe(out var recipe))
        {
            player.Crafting.RecomputeResultPreview();
            return;
        }

        if (!player.Inventory.CanAddItem(recipe.ResultItem, recipe.ResultCount))
        {
            player.Crafting.ApplySnapshot(before);
            player.Crafting.RecomputeResultPreview();
            return;
        }

        player.Inventory.AddItem(recipe.ResultItem, recipe.ResultCount);
        player.Crafting.RecomputeResultPreview();
    }

    public void Submit(PlayerId playerId, ClearCraftingGridCommand command)
    {
        if (!players.TryGetValue(playerId, out var player)) return;

        var changed = false;

        // Move every slot back to inventory storage; drop overflow so the grid always empties.
        for (var i = 0; i < CraftingGrid.SlotCount; i++)
        {
            var item = player.Crafting.GetSlot(i);
            if (item.IsEmpty) continue;

            changed = true;
            player.Crafting.SetSlot(i, default);

            var remaining = player.Inventory.ReturnItemToStorage(item);
            if (remaining > 0)
            {
                droppedItemManager.Spawn(
                    item.Item,
                    remaining,
                    player.Position + new Vector3(0, 1.0f, 0),
                    new Vector3(0, 2.0f, 0));
            }
        }

        if (changed)
        {
            player.Crafting.RecomputeResultPreview();
        }
    }

    private static void TryMoveInventoryToCrafting(Player player, int invSlot, int craftSlot, int count)
    {
        if (invSlot is < Inventory.HotbarSize or >= Inventory.SlotCount) return;
        if (craftSlot is < 0 or >= CraftingGrid.SlotCount) return;

        var source = player.Inventory.GetItem(invSlot);
        if (source.IsEmpty) return;

        var target = player.Crafting.GetSlot(craftSlot);
        var moveCount = count < 0 ? source.Count : Math.Min(count, source.Count);
        if (moveCount <= 0) return;

        if (target.IsEmpty)
        {
            target = new InventoryItem { Item = source.Item, Count = moveCount };
            source.Count -= moveCount;
        }
        else if (target.Item == source.Item)
        {
            var maxStack = DarkVox.Shared.World.Registry.GameContentRegistry.Get(source.Item).MaxStackSize;
            var space = maxStack - target.Count;
            var toMove = Math.Min(space, moveCount);
            if (toMove <= 0) return;
            target.Count += toMove;
            source.Count -= toMove;
        }
        else
        {
            // Swap only if moving entire stack.
            if (count >= 0 && count < source.Count) return;
            (source, target) = (target, source);
        }

        player.Inventory.SetItem(invSlot, source.Count <= 0 ? default : source);
        player.Crafting.SetSlot(craftSlot, target);
    }

    private static void TryMoveCraftingToInventory(Player player, int craftSlot, int invSlot, int count)
    {
        if (craftSlot is < 0 or >= CraftingGrid.SlotCount) return;
        if (invSlot is < Inventory.HotbarSize or >= Inventory.SlotCount) return;

        var source = player.Crafting.GetSlot(craftSlot);
        if (source.IsEmpty) return;

        var target = player.Inventory.GetItem(invSlot);
        var moveCount = count < 0 ? source.Count : Math.Min(count, source.Count);
        if (moveCount <= 0) return;

        if (target.IsEmpty)
        {
            target = new InventoryItem { Item = source.Item, Count = moveCount };
            source.Count -= moveCount;
        }
        else if (target.Item == source.Item)
        {
            var maxStack = DarkVox.Shared.World.Registry.GameContentRegistry.Get(source.Item).MaxStackSize;
            var space = maxStack - target.Count;
            var toMove = Math.Min(space, moveCount);
            if (toMove <= 0) return;
            target.Count += toMove;
            source.Count -= toMove;
        }
        else
        {
            if (count >= 0 && count < source.Count) return;
            (source, target) = (target, source);
        }

        player.Crafting.SetSlot(craftSlot, source.Count <= 0 ? default : source);
        player.Inventory.SetItem(invSlot, target);
    }

    private static void TryMoveWithinCrafting(Player player, int from, int to, int count)
    {
        if (from is < 0 or >= CraftingGrid.SlotCount) return;
        if (to is < 0 or >= CraftingGrid.SlotCount) return;
        if (from == to) return;

        var source = player.Crafting.GetSlot(from);
        if (source.IsEmpty) return;

        var target = player.Crafting.GetSlot(to);
        var moveCount = count < 0 ? source.Count : Math.Min(count, source.Count);
        if (moveCount <= 0) return;

        if (target.IsEmpty)
        {
            target = new InventoryItem { Item = source.Item, Count = moveCount };
            source.Count -= moveCount;
        }
        else if (target.Item == source.Item)
        {
            var maxStack = DarkVox.Shared.World.Registry.GameContentRegistry.Get(source.Item).MaxStackSize;
            var space = maxStack - target.Count;
            var toMove = Math.Min(space, moveCount);
            if (toMove <= 0) return;
            target.Count += toMove;
            source.Count -= toMove;
        }
        else
        {
            if (count >= 0 && count < source.Count) return;
            (source, target) = (target, source);
        }

        player.Crafting.SetSlot(from, source.Count <= 0 ? default : source);
        player.Crafting.SetSlot(to, target);
    }

    public void SubmitAttack(PlayerId playerId, MobId targetMob)
    {
        if (!players.TryGetValue(playerId, out var player)) return;
        if (!mobManager.Mobs.TryGetValue(targetMob, out var mob)) return;

        var result = combatSystem.ProcessPlayerAttackMob(player, mob, mobManager);
        if (result.Hit && result.DamageDealt > 0)
        {
            var dmg = Math.Max(1, (int)MathF.Round(result.DamageDealt));
            var worldPos = mob.Position + new Vector3(0, mob.Definition.HitboxHeight, 0);
            EnqueueCombatEvent(playerId, new CombatEventSnapshot(CombatEventKind.PlayerDealtDamage, dmg, worldPos));
        }
    }

    public void Tick(double elapsedSeconds)
    {
        serverTimeSeconds += Math.Max(0.0, elapsedSeconds);
        worldTime.Tick(elapsedSeconds);
        tickId++;

        var timeSnapshot = worldTime.GetSnapshot();

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
            streamingManager.UpdatePlayer(playerId, player.Position);
        }

        // Server is authoritative for terrain streaming/generation.
        streamingManager.Tick();

        // Keep mobs scoped to the active (loaded/ready) region.
        // For Phase 1, we consider a mob active if its chunk is ready for ANY connected player.
        var anyReadyChunks = new HashSet<int>();
        foreach (var playerId in players.Keys)
        {
            var ready = streamingManager.GetReadyChunksForPlayer(playerId);
            anyReadyChunks.UnionWith(ready);
        }

        var playerPositions = new List<(PlayerId PlayerId, Vector3 Position)>(players.Count);
        {
            foreach (var kvp in players)
            {
                // Only include alive players for mob targeting/spawning
                if (kvp.Value.IsAlive)
                {
                    playerPositions.Add((kvp.Key, kvp.Value.Position));
                }
            }
        }

        var playerPositionsSpan = CollectionsMarshal.AsSpan(playerPositions);

        var readyChunkIndices = anyReadyChunks.Count > 0 ? anyReadyChunks.ToArray() : [];

        // Determine which chunks became ready since the last tick (for rare per-chunk spawn rolls).
        List<int>? newlyReadyChunks = null;
        foreach (var idx in anyReadyChunks)
        {
            if (!lastAnyReadyChunks.Contains(idx))
            {
                newlyReadyChunks ??= [];
                newlyReadyChunks.Add(idx);
            }
        }

        lastAnyReadyChunks.Clear();
        lastAnyReadyChunks.UnionWith(anyReadyChunks);

        if (mobManager.Mobs.Count > 0)
        {
            List<MobId>? toRemove = null;
            Dictionary<string, int>? removeReasons = null;
            foreach (var mob in mobManager.Mobs.Values)
            {
                var chunkIdx = GetChunkIndexFromWorldPos(mob.Position);

                // Despawn rules (Minecraft-like):
                // - >128 blocks from any player => instant
                // - 32..128 blocks => random chance over time
                var minDistSq = float.PositiveInfinity;
                foreach (var (_, p) in playerPositionsSpan)
                {
                    var d = mob.Position - p;
                    var dsq = d.X * d.X + d.Y * d.Y + d.Z * d.Z;
                    if (dsq < minDistSq) minDistSq = dsq;
                }

                var shouldRemove = false;
                var reason = "";
                if (mob.Definition.Category == MobCategory.Hostile)
                {
                    if (minDistSq > 128f * 128f)
                    {
                        shouldRemove = true;
                        reason = "too_far";
                    }
                    else if (minDistSq > 32f * 32f)
                    {
                        mob.TimeInRandomDespawnRangeSeconds += (float)elapsedSeconds;

                        // Bedrock/MC-like: after being in the 32..128 range for ~30 seconds,
                        // roll a 1-in-800 chance each game tick.
                        if (mob.TimeInRandomDespawnRangeSeconds >= 30.0f)
                        {
                            // 1/800 per tick at 20Hz => ~elapsedSeconds*20 trials.
                            // Use a small-prob approximation for efficiency.
                            var chance = Math.Clamp(elapsedSeconds / 40.0, 0.0, 1.0);
                            if (Random.Shared.NextDouble() < chance)
                            {
                                shouldRemove = true;
                                reason = "random_despawn";
                            }
                        }
                    }
                    else
                    {
                        mob.TimeInRandomDespawnRangeSeconds = 0;
                    }
                }

                // Also remove mobs that are no longer in any ready chunk.
                if (!shouldRemove && anyReadyChunks.Count > 0 && !anyReadyChunks.Contains(chunkIdx))
                {
                    shouldRemove = true;
                    reason = "unready_chunk";
                }

                if (shouldRemove)
                {
                    toRemove ??= [];
                    toRemove.Add(mob.Id);

                    removeReasons ??= new Dictionary<string, int>(StringComparer.Ordinal);
                    if (string.IsNullOrEmpty(reason))
                    {
                        reason = "despawn";
                    }
                    removeReasons[reason] = removeReasons.TryGetValue(reason, out var c) ? (c + 1) : 1;
                }
            }

            if (toRemove != null)
            {
                foreach (var id in toRemove)
                {
                    mobManager.Remove(id);
                }

                var globalNow = mobManager.Mobs.Count;
                var localParts = new List<string>(players.Count);
                foreach (var kvp in players)
                {
                    if (!kvp.Value.IsAlive) continue;
                    var localHostiles = CountHostilesAround(kvp.Value.Position, radius: 64f, mobManager);
                    localParts.Add($"{kvp.Key}={localHostiles}");
                }

                var reasonsPart = removeReasons != null
                    ? string.Join(", ", removeReasons.Select(kvp => $"{kvp.Key}={kvp.Value}"))
                    : "unknown";

                log.Info($"[MobUnload] removed={toRemove.Count}, globalNow={globalNow}, localHostiles64={{ {string.Join(", ", localParts)} }}, reasons={{ {reasonsPart} }}");
            }
        }

        // Spawning: passive is a rare per-chunk roll on first ready-load; hostiles spawn continuously.
        if (players.Count > 0)
        {
            mobSpawnSystem.Tick(
                tickId,
                timeSnapshot,
                world.Seed,
                newlyReadyChunks?.ToArray() ?? [],
                playerPositionsSpan,
                world,
                streamingManager.VoxelCache,
                mobManager);

            mobSpawnSystem.TickHostileContinuous(
                elapsedSeconds,
                timeSnapshot,
                world.Seed,
                readyChunkIndices,
                playerPositionsSpan,
                world,
                streamingManager.VoxelCache,
                mobManager);

            // Run AI and Physics
            mobAiSystem.Tick(elapsedSeconds, mobManager, playerPositionsSpan);
            mobPhysicsSystem.Tick(elapsedSeconds, mobManager);
            droppedItemManager.Tick(elapsedSeconds, (pos) => world.GetBlockByPositionGlobalSafe(pos.X, pos.Y, pos.Z), players.Values);

            // Process mob attacks on players
            foreach (var intent in mobAiSystem.PendingAttacks)
            {
                if (mobManager.Mobs.TryGetValue(intent.MobId, out var mob) &&
                    players.TryGetValue(intent.TargetPlayer, out var targetPlayer))
                {
                    var dmg = combatSystem.ProcessMobAttackPlayer(mob, targetPlayer);
                    if (dmg > 0)
                    {
                        EnqueueCombatEvent(intent.TargetPlayer, new CombatEventSnapshot(CombatEventKind.PlayerTookDamage, dmg, targetPlayer.Position));
                    }
                }
            }

            ResolveMobVsPlayerCollisions(mobManager, players);
            ResolveMobVsMobCollisions(mobManager);
        }

        // Temporary death handling: respawn immediately.
        // A dedicated death/respawn scene will be added later.
        foreach (var kvp in players)
        {
            var playerId = kvp.Key;
            var player = kvp.Value;
            if (!player.IsAlive)
            {
                log.Info($"[LocalGameServer] Player {playerId} died. Respawning at default spawn.");
                player.Respawn(spawnPosition);
                player.StartInvulnerability(10f);
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

    public WorldTimeSnapshot GetWorldTimeSnapshot() => worldTime.GetSnapshot();

    private static void ResolveMobVsPlayerCollisions(MobManager mobManager, Dictionary<PlayerId, Player> players)
    {
        if (mobManager.Mobs.Count == 0 || players.Count == 0) return;

        foreach (var mob in mobManager.Mobs.Values)
        {
            var mobRadius = mob.Definition.HitboxWidth * 0.5f;
            var mobMinY = mob.Position.Y;
            var mobMaxY = mob.Position.Y + mob.Definition.HitboxHeight;

            foreach (var player in players.Values)
            {
                var playerCollider = Player.Collider;
                var playerMinY = player.Position.Y;
                var playerMaxY = player.Position.Y + playerCollider.Height;

                if (mobMinY >= playerMaxY || playerMinY >= mobMaxY)
                {
                    continue;
                }

                var dx = mob.Position.X - player.Position.X;
                var dz = mob.Position.Z - player.Position.Z;
                var distSq = dx * dx + dz * dz;
                var minDist = mobRadius + playerCollider.Radius;
                var minDistSq = minDist * minDist;

                if (distSq >= minDistSq)
                {
                    continue;
                }

                var dist = MathF.Sqrt(MathF.Max(distSq, 0));
                var penetration = minDist - dist;
                if (penetration <= 0) continue;

                Vector3 normal;
                if (dist < 1e-4f)
                {
                    normal = Vector3.UnitX;
                }
                else
                {
                    normal = new Vector3(dx / dist, 0, dz / dist);
                }

                // Push mobs out of players (keeps player control stable).
                mob.Position += normal * (penetration + 0.001f);

                var dot = Vector3.Dot(mob.Velocity, normal);
                if (dot < 0)
                {
                    mob.Velocity -= normal * dot;
                }
            }
        }
    }

    private static void ResolveMobVsMobCollisions(MobManager mobManager)
    {
        if (mobManager.Mobs.Count <= 1) return;

        var mobs = mobManager.Mobs.Values.ToArray();
        for (var i = 0; i < mobs.Length; i++)
        {
            for (var j = i + 1; j < mobs.Length; j++)
            {
                var a = mobs[i];
                var b = mobs[j];

                var aRadius = a.Definition.HitboxWidth * 0.5f;
                var bRadius = b.Definition.HitboxWidth * 0.5f;
                var aMinY = a.Position.Y;
                var aMaxY = a.Position.Y + a.Definition.HitboxHeight;
                var bMinY = b.Position.Y;
                var bMaxY = b.Position.Y + b.Definition.HitboxHeight;

                if (aMinY >= bMaxY || bMinY >= aMaxY) continue;

                var dx = a.Position.X - b.Position.X;
                var dz = a.Position.Z - b.Position.Z;
                var distSq = dx * dx + dz * dz;
                var minDist = aRadius + bRadius;
                var minDistSq = minDist * minDist;
                if (distSq >= minDistSq) continue;

                var dist = MathF.Sqrt(MathF.Max(distSq, 0));
                var penetration = minDist - dist;
                if (penetration <= 0) continue;

                Vector3 normal;
                if (dist < 1e-4f)
                {
                    normal = Vector3.UnitX;
                }
                else
                {
                    normal = new Vector3(dx / dist, 0, dz / dist);
                }

                var correction = normal * ((penetration * 0.5f) + 0.001f);
                a.Position += correction;
                b.Position -= correction;

                var aDot = Vector3.Dot(a.Velocity, normal);
                if (aDot < 0) a.Velocity -= normal * aDot;

                var bDot = Vector3.Dot(b.Velocity, normal);
                if (bDot > 0) b.Velocity -= normal * bDot;
            }
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

            // Rare but possible: chunk was reported as ready, but payload bytes were not available.
            // Retry later instead of permanently dropping it (prevents loading hangs).
            log.Warn($"[LocalGameServer] Chunk payload bytes not available yet (player={playerId}, idx={idx}). Will retry.");
            queue.Enqueue(idx);
        }

        return false;
    }

    public void RequestChunkPayloadResend(PlayerId playerId)
    {
        // Recovery mechanism: re-enqueue all chunks that are currently ready for this player.
        // PendingChunkPayloadQueue deduplicates so this is cheap.
        var ready = streamingManager.GetReadyChunksForPlayer(playerId);
        if (ready.Count == 0)
        {
            return;
        }

        var pending = GetOrCreatePendingPayloadQueue(playerId);
        foreach (var idx in ready)
        {
            pending.Enqueue(idx);
        }

        log.Warn($"[LocalGameServer] Resend requested; re-enqueued {ready.Count} chunk payloads for {playerId}.");
    }

    public bool TryGetLoadingProgress(PlayerId playerId, out LoadingProgressSnapshot progress)
        => streamingManager.TryGetLoadingProgress(playerId, out progress);

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


        var player = new Player(world, spawnPosition, streamingManager);

        // Try to load saved player data
        var worldSaveDir = streamingManager.GetWorldSaveDirectory();
        var saveData = PlayerPersistence.Load(playerId.Value, worldSaveDir, log);
        if (saveData != null)
        {
            player.ApplySaveData(saveData);
            log.Info($"[LocalGameServer] Loaded player data for {playerId}");

            // Grace period after loading into the world.
            player.StartInvulnerability(10f);

            if (!player.IsAlive)
            {
                log.Warn($"[LocalGameServer] Player {playerId} loaded with Health=0. Respawning at default spawn.");
                player.Respawn(spawnPosition);
                player.StartInvulnerability(10f);
            }
        }
        else
        {
            log.Info($"[LocalGameServer] New player {playerId}, using default spawn position");
        }

        players[playerId] = player;
        lastVisibleReadyChunksByPlayer[playerId] = [];
        pendingChunkPayloadsByPlayer[playerId] = new PendingChunkPayloadQueue();
        lastSnapshotByPlayer[playerId] = BuildSnapshot(player, chunkDelta: null);
        return player;
    }

    /// <summary>
    /// Saves all connected players' data to disk.
    /// Call this on server shutdown or periodically for auto-save.
    /// </summary>
    public void SaveAllPlayers()
    {
        foreach (var kvp in players)
        {
            var playerId = kvp.Key;
            var player = kvp.Value;

            try
            {
                var saveData = player.BuildSaveData();
                var worldSaveDir = streamingManager.GetWorldSaveDirectory();
                PlayerPersistence.Save(playerId.Value, saveData, worldSaveDir);
                log.Info($"[LocalGameServer] Saved player data for {playerId}");
            }
            catch (Exception ex)
            {
                log.Warn($"[LocalGameServer] Failed to save player {playerId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Saves a specific player's data to disk.
    /// </summary>
    public void SavePlayer(PlayerId playerId)
    {
        if (!players.TryGetValue(playerId, out var player)) return;

        try
        {
            var saveData = player.BuildSaveData();
            var worldSaveDir = streamingManager.GetWorldSaveDirectory();
            PlayerPersistence.Save(playerId.Value, saveData, worldSaveDir);
            log.Info($"[LocalGameServer] Saved player data for {playerId}");
        }
        catch (Exception ex)
        {
            log.Warn($"[LocalGameServer] Failed to save player {playerId}: {ex.Message}");
        }
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
            InvulnerabilityRemainingSeconds: MathF.Max(0f, player.InvulnerabilityRemaining),
            SelectedHotbarSlot: player.Inventory.SelectedSlot,
            Inventory: player.Inventory.BuildSnapshot(),
            Attributes: attrSnap,
            Crafting: player.Crafting.BuildSnapshot());

        return new GameStateSnapshot(
            TickId: tickId,
            ServerTimeSeconds: serverTimeSeconds,
            Player: pSnap,
            ChunkDelta: chunkDelta,
            DroppedItems: droppedItemManager.GetSnapshots());
    }

    private static int GetChunkIndexFromWorldPos(in Vector3 worldPos)
    {
        // Chunk index is based on XZ only; Y is ignored.
        // Use floor to match block-space semantics.
        var x = (int)MathF.Floor(worldPos.X);
        var z = (int)MathF.Floor(worldPos.Z);
        return VoxelHelper.GetChunkIndexFromPositionGlobal(new Vector3i(x, 0, z));
    }

    private static int CountHostilesAround(in Vector3 position, float radius, MobManager mobManager)
    {
        var rSq = radius * radius;
        var count = 0;
        foreach (var mob in mobManager.Mobs.Values)
        {
            if (mob.Definition.Category != MobCategory.Hostile) continue;
            var d = mob.Position - position;
            if (d.LengthSquared <= rSq) count++;
        }
        return count;
    }
}

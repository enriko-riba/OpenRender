using OpenTK.Mathematics;
using SpyroGame.Server.Streaming;
using SpyroGame.Shared.State;
using SpyroGame.World;

namespace SpyroGame.Server.Mobs;

/// <summary>
/// Minecraft-like spawning scaffold.
/// Phase 1: choose candidates in loaded/ready chunks around players and validate via light/terrain.
/// </summary>
public sealed class MobSpawnSystem(MobSpawnSystem.Settings settings)
{
    public sealed record Settings(
        int NoSpawnRadiusBlocks,
        int SpawnRadiusBlocks,
        int MaxSpawnAttemptsPerTick);

    private readonly Dictionary<PlayerId, ulong> lastSpawnTickByPlayer = [];

    private const int MaxMobsPerPlayerSoftCap = 24;
    private const ulong SpawnCooldownTicks = 45; // ~0.75s at 60Hz; keeps spawning bursts in check.

    public void Tick(
        ulong tickId,
        double serverTimeSeconds,
        ReadOnlySpan<(PlayerId PlayerId, Vector3 Position)> players,
        VoxelWorld world,
        ChunkVoxelDataCache voxelCache,
        Func<PlayerId, int, bool> isChunkReadyForPlayer,
        MobManager mobManager)
    {
        _ = serverTimeSeconds;

        if (players.Length == 0)
        {
            return;
        }

        // Very simple Phase 1 implementation:
        // - spawn passive cows near players
        // - only in chunks that are Ready for that player
        // - only if the collision container is loaded (height + spans/heightmap)
        // - keep total count in check (soft cap per player)

        // Build a rough per-player cap: if we already have "enough" mobs within that player's ready set, skip.
        // (Filtering is performed by LocalGameServer.TryGetMobSnapshot.)
        foreach (var (playerId, playerPos) in players)
        {
            if (!ShouldAttemptSpawnForPlayer(tickId, playerId, mobManager))
            {
                continue;
            }

            var attempts = Math.Max(0, settings.MaxSpawnAttemptsPerTick);
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                if (TryPickSpawnPosition(playerPos, world, voxelCache, isChunkReadyForPlayer, playerId, out var spawnPos, out var def))
                {
                    var mob = mobManager.CreateMob(def);
                    mob.Position = spawnPos;
                    mob.YawDegrees = (float)(Random.Shared.NextDouble() * 360.0);
                    mob.PitchDegrees = 0;
                    RecordSpawnForPlayer(tickId, playerId);
                    break; // One spawn per player per tick.
                }
            }
        }
    }

    private bool ShouldAttemptSpawnForPlayer(ulong tickId, PlayerId playerId, MobManager mobManager)
    {
        // Soft cap: mobs are global in MobManager, but we throttle per player.
        if (mobManager.Mobs.Count >= MaxMobsPerPlayerSoftCap * 2)
        {
            return false;
        }

        if (lastSpawnTickByPlayer.TryGetValue(playerId, out var lastTick))
        {
            if (tickId >= lastTick && (tickId - lastTick) < SpawnCooldownTicks)
            {
                return false;
            }
        }

        return true;
    }

    private void RecordSpawnForPlayer(ulong tickId, PlayerId playerId) => lastSpawnTickByPlayer[playerId] = tickId;

    private bool TryPickSpawnPosition(
        in Vector3 playerPos,
        VoxelWorld world,
        ChunkVoxelDataCache voxelCache,
        Func<PlayerId, int, bool> isChunkReadyForPlayer,
        PlayerId playerId,
        out Vector3 spawnPos,
        out MobDefinition mobDef)
    {
        spawnPos = default;
        mobDef = default!;

        // Choose a random offset in XZ.
        // We use a square ring for simplicity; it is good enough for Phase 1.
        var dx = RandomInRange(settings.NoSpawnRadiusBlocks, settings.SpawnRadiusBlocks) * (Random.Shared.Next(0, 2) == 0 ? -1 : 1);
        var dz = RandomInRange(settings.NoSpawnRadiusBlocks, settings.SpawnRadiusBlocks) * (Random.Shared.Next(0, 2) == 0 ? -1 : 1);

        var wx = (int)MathF.Floor(playerPos.X) + dx;
        var wz = (int)MathF.Floor(playerPos.Z) + dz;
        if (wx < 0 || wz < 0 || wx > VoxelHelper.MaxBlockPositionXZ || wz > VoxelHelper.MaxBlockPositionXZ)
        {
            return false;
        }

        var chunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(new Vector3i(wx, 0, wz));
        if (!isChunkReadyForPlayer(playerId, chunkIndex))
        {
            return false;
        }

        var chunk = world[chunkIndex];
        if (chunk is null || !chunk.HasCollisionData)
        {
            return false;
        }

        // Get ChunkData for light
        if (!voxelCache.TryGetChunkData(chunkIndex, out var chunkData) || chunkData == null || chunkData.LightData == null)
        {
            return false;
        }

        var origin = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
        var lx = wx - origin.X;
        var lz = wz - origin.Z;

        var topY = chunk.GetTerrainHeightAt(lx, lz);

        // Try surface spawn
        if (TrySpawnAt(wx, topY, wz, chunkData, world, out spawnPos, out mobDef)) return true;

        // Try cave spawn (random Y below surface)
        if (topY > 10)
        {
            var caveY = Random.Shared.Next(5, topY - 5);
            if (TrySpawnAt(wx, caveY, wz, chunkData, world, out spawnPos, out mobDef)) return true;
        }

        return false;
    }

    private static bool TrySpawnAt(int x, int y, int z, ChunkData chunkData, VoxelWorld world, out Vector3 spawnPos, out MobDefinition mobDef)
    {
        spawnPos = default;
        mobDef = default!;

        // Check solid ground
        var groundBlock = world.GetBlockByPositionGlobalSafe(x, y - 1, z);
        if (!groundBlock.HasValue || !groundBlock.Value.IsSolid) return false;

        // Check space for mob (assume max height 2 for check)
        var headBlock = world.GetBlockByPositionGlobalSafe(x, y, z);
        var headBlock2 = world.GetBlockByPositionGlobalSafe(x, y + 1, z);
        if ((headBlock.HasValue && headBlock.Value.IsSolid) || (headBlock2.HasValue && headBlock2.Value.IsSolid)) return false;

        // Check light
        var light = GetSkyLight(chunkData, x % VoxelHelper.ChunkSideSize, y, z % VoxelHelper.ChunkSideSize);

        // Pick a mob that fits
        var candidates = MobRegistry.AllDefinitions.Where(d => light >= d.SpawnLightLevelMin && light <= d.SpawnLightLevelMax).ToList();
        if (candidates.Count == 0) return false;

        // Weighted random
        var totalWeight = candidates.Sum(c => c.SpawnWeight);
        var roll = Random.Shared.Next(totalWeight);
        var current = 0;
        foreach (var cand in candidates)
        {
            current += cand.SpawnWeight;
            if (roll < current)
            {
                mobDef = cand;
                spawnPos = new Vector3(x + 0.5f, y, z + 0.5f);
                return true;
            }
        }

        return false;
    }

    private static int GetSkyLight(ChunkData chunkData, int lx, int ly, int lz)
    {
        if (ly < 0 || ly >= VoxelHelper.ChunkYSize) return 15;
        var index = lx + lz * VoxelHelper.ChunkSideSize + ly * VoxelHelper.ChunkSideSizeSquare;
        if (index < 0 || index >= chunkData.LightData.Length) return 15;
        return (chunkData.LightData[index] >> 4) & 0xF;
    }

    private static int RandomInRange(int min, int max) => Random.Shared.Next(min, max + 1);
}

using DarkVox.Shared.State;
using DarkVox.Shared.World;
using DarkVox.Shared.World.Registry;
using DarkVox.World;
using OpenTK.Mathematics;

namespace DarkVox.Server.Mobs;

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

    // Track chunks we've already "rolled" for spawning in this session.
    // This keeps spawn distribution stable and avoids flooding when re-entering areas.
    private readonly HashSet<int> rolledChunks = [];

    // Chunks that became ready while it was nighttime (or before any players existed).
    // We defer the one-time passive roll until the next daytime tick.
    private readonly HashSet<int> pendingPassiveChunkRolls = [];

    // Continuous hostile spawning state.
    private double hostileSpawnAccumulatorSeconds;

    // Bedrock-like: global entity cap shared by all players in the dimension.
    // (This includes hostiles + passives + ambient; currently we only have passive + hostile.)
    private const int GlobalEntityHardCap = 200;

    // Bedrock-like density caps: limit local concentration regardless of player count.
    // These are applied per "density area".
    private const int DensityAreaSizeBlocks = 128;
    private const int PassiveDensityCap = 12;
    private const int SurfaceHostileDensityCap = 8;
    private const int CaveHostileDensityCap = 16;

    // Enforce density caps by proximity as well as by bucketed area.
    // The area buckets are good for amortized cost, but can be too permissive for large caves
    // (a single contiguous cave spanning multiple 128×128 areas can still flood).
    private const int HostileLocalDensityRadiusBlocks = 64;

    // Target rarity:
    // - Passive: ~1 per 10x10 chunks (1%)
    // - Hostile: rarer than passive
    private const int RollDenominator = 10_000;
    private const int PassiveRollThreshold = 100; // 1.00%
    private const int MaxPlacementAttemptsPerChunk = 24;

    // Hostile continuous spawning: attempt packs at a fixed cadence.
    // Spawning is "continuous" but should not be *high frequency*.
    // A high tick rate quickly slams into density caps and feels like instant overcrowding.
    private const double HostileSpawnCycleSeconds = 1.0;
    private const int HostilePackAttemptsPerCyclePerPlayer = 1;
    private const double HostileSpawnChancePerCycle = 0.25; // 25% chance per cycle per player
    private const int HostilePackSizeMin = 1;
    private const int HostilePackSizeMax = 3;
    private const float HostileMinSpawnRadiusBlocks = 24;
    private const float HostileMaxSpawnRadiusBlocks = 128;

    public void Tick(
        ulong tickId,
        WorldTimeSnapshot worldTime,
        int worldSeed,
        ReadOnlySpan<int> newlyReadyChunkIndices,
        ReadOnlySpan<(PlayerId PlayerId, Vector3 Position)> players,
        VoxelWorld world,
        ChunkVoxelDataCache voxelCache,
        MobManager mobManager)
    {
        // Passive mobs: rare per-chunk roll on first ready-load.
        var isDaytime = WorldTimeService.IsDaytime(worldTime.TimeOfDaySeconds);

        if (newlyReadyChunkIndices.Length > 0)
        {
            foreach (var idx in newlyReadyChunkIndices)
            {
                if (!rolledChunks.Contains(idx))
                {
                    pendingPassiveChunkRolls.Add(idx);
                }
            }
        }

        if (players.Length == 0 || pendingPassiveChunkRolls.Count == 0 || !isDaytime)
        {
            return;
        }


        var (passiveCount, hostileCount) = CountByCategory(mobManager);
        var passiveDensity = BuildPassiveDensityByArea(mobManager);
        var noSpawnRadiusSq = settings.NoSpawnRadiusBlocks * settings.NoSpawnRadiusBlocks;

        var toProcess = pendingPassiveChunkRolls.ToArray();
        foreach (var chunkIndex in toProcess)
        {
            if (mobManager.Mobs.Count >= GlobalEntityHardCap)
            {
                return;
            }

            pendingPassiveChunkRolls.Remove(chunkIndex);
            rolledChunks.Add(chunkIndex);

            // Chunk must have collision container and voxel data for light sampling.
            var chunk = world[chunkIndex];
            if (chunk is null || !chunk.HasCollisionData)
            {
                continue;
            }

            if (!voxelCache.TryAcquireChunkData(chunkIndex, out var chunkLease))
            {
                continue;
            }

            using (chunkLease)
            {
                var chunkData = chunkLease.Data;
                if (chunkData.LightData == null)
                {
                    continue;
                }

                var roll = (int)(HashToUInt(worldSeed, chunkIndex, salt: 0) % RollDenominator);
                if (roll >= PassiveRollThreshold)
                {
                    continue;
                }

                if (TrySpawnForChunk(worldSeed, isDaytime: true, MobCategory.Passive, chunkIndex, chunk, chunkData, players, noSpawnRadiusSq, passiveCount, hostileCount, world, voxelCache, passiveDensity, out var spawnPos, out var def))
                {
                    var mob = mobManager.CreateMob(def);
                    mob.Position = spawnPos;
                    mob.YawDegrees = HashToYawDegrees(worldSeed, chunkIndex);
                    mob.PitchDegrees = 0;
                    mob.SpawnLayer = MobSpawnLayer.Surface;

                    if (def.Category == MobCategory.Passive) passiveCount++;
                    else if (def.Category == MobCategory.Hostile) hostileCount++;

                    var areaKey = GetDensityAreaKey((int)MathF.Floor(spawnPos.X), (int)MathF.Floor(spawnPos.Z));
                    passiveDensity[areaKey] = passiveDensity.TryGetValue(areaKey, out var existing) ? (existing + 1) : 1;
                }
            }
        }
    }

    public void TickHostileContinuous(
        double elapsedSeconds,
        WorldTimeSnapshot worldTime,
        int worldSeed,
        ReadOnlySpan<int> readyChunkIndices,
        ReadOnlySpan<(PlayerId PlayerId, Vector3 Position)> players,
        VoxelWorld world,
        ChunkVoxelDataCache voxelCache,
        MobManager mobManager)
    {
        if (elapsedSeconds <= 0 || players.Length == 0 || readyChunkIndices.Length == 0)
        {
            return;
        }

        hostileSpawnAccumulatorSeconds += elapsedSeconds;
        if (hostileSpawnAccumulatorSeconds < HostileSpawnCycleSeconds)
        {
            return;
        }

        // Run as many cycles as needed if the server is catching up.
        var cycles = (int)Math.Floor(hostileSpawnAccumulatorSeconds / HostileSpawnCycleSeconds);
        hostileSpawnAccumulatorSeconds -= cycles * HostileSpawnCycleSeconds;
        cycles = Math.Min(cycles, 5); // prevent runaway on long frames

        var isDaytime = WorldTimeService.IsDaytime(worldTime.TimeOfDaySeconds);
        var noSpawnRadiusSq = settings.NoSpawnRadiusBlocks * settings.NoSpawnRadiusBlocks;

        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var (passiveCount, hostileCount) = CountByCategory(mobManager);
            if (mobManager.Mobs.Count >= GlobalEntityHardCap)
            {
                return;
            }

            var density = BuildHostileDensityByArea(mobManager);

            // Bedrock-style: do not scale aggressively with player count.
            // We still pick spawn anchors around players, but keep attempts modest.
            var packAttemptsThisCycle = HostilePackAttemptsPerCyclePerPlayer * Math.Min(players.Length, 2);
            for (var attempt = 0; attempt < packAttemptsThisCycle; attempt++)
            {
                if (Random.Shared.NextDouble() > HostileSpawnChancePerCycle)
                {
                    continue;
                }

                if (mobManager.Mobs.Count >= GlobalEntityHardCap)
                {
                    return;
                }

                var playerPos = players[Random.Shared.Next(players.Length)].Position;

                // Check if player is already overwhelmed (local density check around player)
                if (GetHostileCountAround(mobManager, playerPos, HostileLocalDensityRadiusBlocks) >= CaveHostileDensityCap)
                {
                    continue;
                }

                if (!TryPickHostileSpawnAnchor(worldSeed, worldTime, playerPos, readyChunkIndices, world, voxelCache, isDaytime, out var anchorPos))
                {
                    continue;
                }

                var packSize = Random.Shared.Next(HostilePackSizeMin, HostilePackSizeMax + 1);
                for (var k = 0; k < packSize; k++)
                {
                    if (mobManager.Mobs.Count >= GlobalEntityHardCap)
                    {
                        break;
                    }

                    // Small jitter around the anchor.
                    var ox = Random.Shared.Next(-4, 5);
                    var oz = Random.Shared.Next(-4, 5);
                    var x = (int)MathF.Floor(anchorPos.X) + ox;
                    var z = (int)MathF.Floor(anchorPos.Z) + oz;

                    // Keep spawns in the active ring around this player.
                    var dx = (x + 0.5f) - playerPos.X;
                    var dz = (z + 0.5f) - playerPos.Z;
                    var distSqXZ = dx * dx + dz * dz;
                    if (distSqXZ < HostileMinSpawnRadiusBlocks * HostileMinSpawnRadiusBlocks || distSqXZ > HostileMaxSpawnRadiusBlocks * HostileMaxSpawnRadiusBlocks)
                    {
                        continue;
                    }

                    // Also ensure we are not too close to any other player.
                    var tooClose = false;
                    foreach (var (_, p) in players)
                    {
                        var px = (x + 0.5f) - p.X;
                        var pz = (z + 0.5f) - p.Z;
                        if ((px * px + pz * pz) < noSpawnRadiusSq)
                        {
                            tooClose = true;
                            break;
                        }
                    }
                    if (tooClose)
                    {
                        continue;
                    }

                    var chunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(new Vector3i(x, 0, z));
                    if (!Contains(readyChunkIndices, chunkIndex))
                    {
                        continue;
                    }

                    if (!voxelCache.TryAcquireChunkData(chunkIndex, out var chunkLease))
                    {
                        continue;
                    }

                    using (chunkLease)
                    {
                        var chunkData = chunkLease.Data;
                        if (chunkData.LightData == null)
                        {
                            continue;
                        }

                        var chunk = world[chunkIndex];
                        if (chunk is null || !chunk.HasCollisionData)
                        {
                            continue;
                        }

                        var origin = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
                        var lx = x - origin.X;
                        var lz = z - origin.Z;
                        if (lx is < 0 or >= VoxelHelper.ChunkSideSize || lz is < 0 or >= VoxelHelper.ChunkSideSize)
                        {
                            continue;
                        }

                        var topY = chunk.GetTerrainHeightAt(lx, lz);
                        if (topY <= 1 || topY >= VoxelHelper.ChunkYSize - 2)
                        {
                            continue;
                        }

                        // At night, allow surface hostiles (effective light handled inside TrySpawnAt).
                        // During day, only allow caves/dark areas.
                        if (!isDaytime)
                        {
                            var areaKey = GetDensityAreaKey(x, z);
                            var counts = density.TryGetValue(areaKey, out var existing) ? existing : (Surface: 0, Cave: 0);
                            if (counts.Surface >= SurfaceHostileDensityCap)
                            {
                                continue;
                            }

                            if (!HasHostileLocalHeadroom(mobManager, x, z, MobSpawnLayer.Surface, SurfaceHostileDensityCap))
                            {
                                continue;
                            }

                            if (TrySpawnAt(isDaytime: false, passiveCount, hostileCount, isCaveSpawn: false, x, topY, z, chunkData, world, voxelCache, out var spawnPos, out var spawnedDef))
                            {
                                var mob = mobManager.CreateMob(spawnedDef);
                                mob.Position = spawnPos;
                                mob.YawDegrees = (float)(Random.Shared.NextDouble() * 360.0);
                                mob.PitchDegrees = 0;
                                mob.SpawnLayer = MobSpawnLayer.Surface;
                                hostileCount++;

                                density[areaKey] = (Surface: (counts.Surface + 1), Cave: counts.Cave);
                                continue;
                            }
                        }

                        // Cave spawn: scan downward for a dark pocket (raw skylight <= 7).
                        if (TryFindCaveSpawnY(world, chunkData, lx, lz, x, z, topY, out var y))
                        {
                            var areaKey = GetDensityAreaKey(x, z);
                            var counts = density.TryGetValue(areaKey, out var existing) ? existing : (Surface: 0, Cave: 0);
                            if (counts.Cave >= CaveHostileDensityCap)
                            {
                                continue;
                            }

                            if (!HasHostileLocalHeadroom(mobManager, x, z, MobSpawnLayer.Cave, CaveHostileDensityCap))
                            {
                                continue;
                            }

                            if (TrySpawnAt(isDaytime, passiveCount, hostileCount, isCaveSpawn: true, x, y, z, chunkData, world, voxelCache, out var cavePos, out var caveDef))
                            {
                                var mob = mobManager.CreateMob(caveDef);
                                mob.Position = cavePos;
                                mob.YawDegrees = (float)(Random.Shared.NextDouble() * 360.0);
                                mob.PitchDegrees = 0;
                                mob.SpawnLayer = MobSpawnLayer.Cave;
                                hostileCount++;

                                density[areaKey] = (Surface: counts.Surface, Cave: (counts.Cave + 1));
                            }
                        }
                    }
                }
            }
        }
    }

    private static bool HasHostileLocalHeadroom(MobManager mobManager, int x, int z, MobSpawnLayer layer, int cap)
    {
        var cx = x + 0.5f;
        var cz = z + 0.5f;
        var r = HostileLocalDensityRadiusBlocks;
        var rSq = r * r;

        var count = 0;
        foreach (var mob in mobManager.Mobs.Values)
        {
            if (mob.Definition.Category != MobCategory.Hostile)
            {
                continue;
            }

            if (mob.SpawnLayer != layer)
            {
                continue;
            }

            var dx = mob.Position.X - cx;
            var dz = mob.Position.Z - cz;
            if ((dx * dx + dz * dz) <= rSq)
            {
                count++;
                if (count >= cap)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static long GetDensityAreaKey(int x, int z)
    {
        var ax = x >= 0 ? x / DensityAreaSizeBlocks : -(((-x) + DensityAreaSizeBlocks - 1) / DensityAreaSizeBlocks);
        var az = z >= 0 ? z / DensityAreaSizeBlocks : -(((-z) + DensityAreaSizeBlocks - 1) / DensityAreaSizeBlocks);
        return ((long)ax << 32) | (uint)az;
    }

    private static Dictionary<long, (int Surface, int Cave)> BuildHostileDensityByArea(MobManager mobManager)
    {
        var dict = new Dictionary<long, (int Surface, int Cave)>();
        foreach (var mob in mobManager.Mobs.Values)
        {
            if (mob.Definition.Category != MobCategory.Hostile)
            {
                continue;
            }

            var x = (int)MathF.Floor(mob.Position.X);
            var z = (int)MathF.Floor(mob.Position.Z);
            var key = GetDensityAreaKey(x, z);
            var counts = dict.TryGetValue(key, out var existing) ? existing : (Surface: 0, Cave: 0);
            if (mob.SpawnLayer == MobSpawnLayer.Cave)
            {
                counts.Cave++;
            }
            else
            {
                counts.Surface++;
            }
            dict[key] = counts;
        }

        return dict;
    }

    private static Dictionary<long, int> BuildPassiveDensityByArea(MobManager mobManager)
    {
        var dict = new Dictionary<long, int>();
        foreach (var mob in mobManager.Mobs.Values)
        {
            if (mob.Definition.Category != MobCategory.Passive)
            {
                continue;
            }

            var x = (int)MathF.Floor(mob.Position.X);
            var z = (int)MathF.Floor(mob.Position.Z);
            var key = GetDensityAreaKey(x, z);
            dict[key] = dict.TryGetValue(key, out var c) ? (c + 1) : 1;
        }

        return dict;
    }

    private static bool TryPickHostileSpawnAnchor(
        int worldSeed,
        WorldTimeSnapshot worldTime,
        in Vector3 playerPos,
        ReadOnlySpan<int> readyChunkIndices,
        VoxelWorld world,
        ChunkVoxelDataCache voxelCache,
        bool isDaytime,
        out Vector3 anchorPos)
    {
        anchorPos = default;

        // Random position in an annulus around the player.
        for (var tries = 0; tries < 8; tries++)
        {
            var angle = Random.Shared.NextDouble() * Math.PI * 2.0;
            var radius = HostileMinSpawnRadiusBlocks + (float)Random.Shared.NextDouble() * (HostileMaxSpawnRadiusBlocks - HostileMinSpawnRadiusBlocks);

            var x = (int)MathF.Floor(playerPos.X + (float)(Math.Cos(angle) * radius));
            var z = (int)MathF.Floor(playerPos.Z + (float)(Math.Sin(angle) * radius));
            if (x < 0 || z < 0 || x > VoxelHelper.MaxBlockPositionXZ || z > VoxelHelper.MaxBlockPositionXZ)
            {
                continue;
            }

            var chunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(new Vector3i(x, 0, z));
            if (!Contains(readyChunkIndices, chunkIndex))
            {
                continue;
            }

            if (!voxelCache.TryAcquireChunkData(chunkIndex, out var chunkLease))
            {
                continue;
            }

            using (chunkLease)
            {
                var chunkData = chunkLease.Data;
                if (chunkData.LightData == null)
                {
                    continue;
                }

                var chunk = world[chunkIndex];
                if (chunk is null || !chunk.HasCollisionData)
                {
                    continue;
                }

                var origin = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
                var lx = x - origin.X;
                var lz = z - origin.Z;
                if (lx is < 0 or >= VoxelHelper.ChunkSideSize || lz is < 0 or >= VoxelHelper.ChunkSideSize)
                {
                    continue;
                }

                var topY = chunk.GetTerrainHeightAt(lx, lz);
                if (topY <= 1 || topY >= VoxelHelper.ChunkYSize - 2)
                {
                    continue;
                }

                // Determine if this anchor can ever spawn a hostile:
                // - at night: surface is allowed (effective light will be 0)
                // - at day: require a dark cave in the column
                if (isDaytime)
                {
                    if (!TryFindCaveSpawnY(world, chunkData, lx, lz, x, z, topY, out _))
                    {
                        continue;
                    }
                }

                anchorPos = new Vector3(x + 0.5f, topY, z + 0.5f);
                return true;
            }
        }

        return false;
    }

    private static bool Contains(ReadOnlySpan<int> values, int value)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] == value) return true;
        }
        return false;
    }

    private bool TrySpawnForChunk(
        int worldSeed,
        bool isDaytime,
        MobCategory category,
        int chunkIndex,
        Chunk chunk,
        ChunkData chunkData,
        ReadOnlySpan<(PlayerId PlayerId, Vector3 Position)> players,
        float noSpawnRadiusSq,
        int passiveCount,
        int hostileCount,
        VoxelWorld world,
        ChunkVoxelDataCache voxelCache,
        Dictionary<long, int> passiveDensity,
        out Vector3 spawnPos,
        out MobDefinition mobDef)
    {
        spawnPos = default;
        mobDef = default!;

        var origin = VoxelHelper.GetChunkPositionGlobal(chunkIndex);

        for (var attempt = 0; attempt < MaxPlacementAttemptsPerChunk; attempt++)
        {
            var h = HashToUInt(worldSeed, chunkIndex, salt: 1 + attempt);
            var lx = (int)(h & 0xF);
            var lz = (int)((h >> 8) & 0xF);

            var wx = origin.X + lx;
            var wz = origin.Z + lz;

            // Keep some distance from all players.
            var tooClose = false;
            foreach (var (_, pos) in players)
            {
                var dx = (wx + 0.5f) - pos.X;
                var dz = (wz + 0.5f) - pos.Z;
                if ((dx * dx + dz * dz) < noSpawnRadiusSq)
                {
                    tooClose = true;
                    break;
                }
            }
            if (tooClose)
            {
                continue;
            }

            var topY = chunk.GetTerrainHeightAt(lx, lz);
            if (topY <= 1 || topY >= VoxelHelper.ChunkYSize - 2)
            {
                continue;
            }

            // Density cap: prevent passive mob crowding in a single local area.
            if (category == MobCategory.Passive)
            {
                var areaKey = GetDensityAreaKey(wx, wz);
                var existing = passiveDensity.TryGetValue(areaKey, out var c) ? c : 0;
                if (existing >= PassiveDensityCap)
                {
                    continue;
                }
            }

            if (category == MobCategory.Passive)
            {
                if (TrySpawnAt(isDaytime, passiveCount, hostileCount, isCaveSpawn: false, wx, topY, wz, chunkData, world, voxelCache, out spawnPos, out mobDef))
                {
                    return true;
                }
            }
            else
            {
                // Always allow cave hostiles (prefer actual dark sky-light <= 7).
                if (TryFindCaveSpawnY(world, chunkData, lx, lz, wx, wz, topY, out var y) &&
                    TrySpawnAt(isDaytime, passiveCount, hostileCount, isCaveSpawn: true, wx, y, wz, chunkData, world, voxelCache, out spawnPos, out mobDef))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindCaveSpawnY(VoxelWorld world, ChunkData chunkData, int lx, int lz, int wx, int wz, int topY, out int spawnY)
    {
        spawnY = 0;
        var startY = Math.Clamp(topY - 3, 4, VoxelHelper.ChunkYSize - 3);
        var yMin = Math.Max(2, startY - 80);

        for (var y = startY; y >= yMin; y--)
        {
            if (!IsValidSpawnSpot(world, wx, y, wz))
            {
                continue;
            }

            var (sky, block) = GetLightLevels(chunkData, lx, y, lz);
            // Must be dark to be a valid cave spawn (checking both sky and block light).
            if (Math.Max(sky, block) > 7)
            {
                continue;
            }

            spawnY = y;
            return true;
        }

        return false;
    }

    private static bool TrySpawnAt(bool isDaytime, int passiveCount, int hostileCount, bool isCaveSpawn, int x, int y, int z, ChunkData chunkData, VoxelWorld world, ChunkVoxelDataCache voxelCache, out Vector3 spawnPos, out MobDefinition mobDef)
    {
        spawnPos = default;
        mobDef = default!;

        if (passiveCount + hostileCount >= GlobalEntityHardCap)
        {
            return false;
        }

        // Check solid ground
        var groundBlock = world.GetBlockByPositionGlobalSafe(x, y - 1, z);
        if (!groundBlock.HasValue || !groundBlock.Value.IsSolid) return false;

        // Check space for mob (assume max height 2 for check)
        var headBlock = world.GetBlockByPositionGlobalSafe(x, y, z);
        var headBlock2 = world.GetBlockByPositionGlobalSafe(x, y + 1, z);
        if ((headBlock.HasValue && headBlock.Value.IsSolid) || (headBlock2.HasValue && headBlock2.Value.IsSolid)) return false;

        // Liquid rule:
        // - Never spawn hostiles in liquid (even if they can swim); we don't have aquatic hostiles yet.
        // - Allow passive mobs in liquid only if CanSwim=true.
        var isInLiquid = (headBlock.HasValue && headBlock.Value.Block.IsLiquid())
            || (headBlock2.HasValue && headBlock2.Value.Block.IsLiquid());

        // Check light
        var lx = Mod(x, VoxelHelper.ChunkSideSize);
        var lz = Mod(z, VoxelHelper.ChunkSideSize);
        var (sky, block) = GetLightLevels(chunkData, lx, y, lz);

        // Sky-light is a static precomputed value in our current lighting system.
        // At night the surface still has skyLight=15, but gameplay should treat it as dark.
        // So for *surface* spawns at night, consider sky light as 0 for the spawn rule.
        // We must also consider block light (torches), which is valid regardless of time.
        var effectiveSky = isDaytime ? sky : 0;
        var light = Math.Max(effectiveSky, block);

        // Pick a mob that fits
        var candidates = MobRegistry.AllDefinitions
            .Where(d => light >= d.SpawnLightLevelMin && light <= d.SpawnLightLevelMax)
            .Where(d => d.Category switch
            {
                MobCategory.Passive => isDaytime,
                MobCategory.Hostile => !isDaytime || isCaveSpawn,
                _ => true
            })
            .Where(d => !isInLiquid || (d.Category == MobCategory.Passive && d.CanSwim))
            .Where(d => d.Category switch
            {
                _ => true
            })
            .ToList();
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

    private static (int Sky, int Block) GetLightLevels(ChunkData chunkData, int lx, int ly, int lz)
    {
        if (ly is < 0 or >= VoxelHelper.ChunkYSize) return (15, 0);
        var index = lx + lz * VoxelHelper.ChunkSideSize + ly * VoxelHelper.ChunkSideSizeSquare;
        if (index < 0 || index >= chunkData.LightData.Length) return (15, 0);
        // Low nibble = sky light; high nibble = block light.
        var val = chunkData.LightData[index];
        return (val & 0xF, (val >> 4) & 0xF);
    }
    private static (int Passive, int Hostile) CountByCategory(MobManager mobManager)
    {
        var passive = 0;
        var hostile = 0;

        foreach (var mob in mobManager.Mobs.Values)
        {
            switch (mob.Definition.Category)
            {
                case MobCategory.Passive:
                    passive++;
                    break;
                case MobCategory.Hostile:
                    hostile++;
                    break;
            }
        }

        return (passive, hostile);
    }
    private static bool IsValidSpawnSpot(VoxelWorld world, int x, int y, int z)
    {
        // floor must be solid, spawn cell + head cell must be non-solid
        var ground = world.GetBlockByPositionGlobalSafe(x, y - 1, z);
        if (!ground.HasValue || !ground.Value.IsSolid) return false;

        var body = world.GetBlockByPositionGlobalSafe(x, y, z);
        if (body.HasValue && body.Value.IsSolid) return false;

        var head = world.GetBlockByPositionGlobalSafe(x, y + 1, z);
        if (head.HasValue && head.Value.IsSolid) return false;

        return true;
    }

    private static uint HashToUInt(int seed, int chunkIndex, int salt)
    {
        unchecked
        {
            // Simple avalanche hash; deterministic and fast.
            uint x = (uint)seed;
            x ^= (uint)chunkIndex * 0x9E3779B9u;
            x ^= (uint)salt * 0x85EBCA6Bu;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x;
        }
    }
    private static float HashToYawDegrees(int seed, int chunkIndex)
    {
        var h = HashToUInt(seed, chunkIndex, salt: 1337);
        return (h % 360u);
    }
    private static int Mod(int value, int mod)
    {
        var r = value % mod;
        return r < 0 ? r + mod : r;
    }

    private static int GetHostileCountAround(MobManager mobManager, Vector3 pos, float radius)
    {
        var rSq = radius * radius;
        var count = 0;
        foreach (var mob in mobManager.Mobs.Values)
        {
            if (mob.Definition.Category != MobCategory.Hostile) continue;
            var d = mob.Position - pos;
            if (d.LengthSquared <= rSq) count++;
        }
        return count;
    }
}

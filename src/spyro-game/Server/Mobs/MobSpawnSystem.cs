using OpenTK.Mathematics;
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

    private static readonly MobDefinition DefaultCow = new(
        Kind: MobKind.Cow,
        Category: MobCategory.Passive,
        HitboxWidth: 0.95f,
        HitboxHeight: 0.95f,
        MaxHealth: 10,
        WalkSpeed: 2.0f,
        RunSpeed: 3.5f,
        StepHeight: 0.6f,
        CanSwim: true,
        CanClimb: false,
        CanFly: false,
        BaseDamage: 0,
        AttackRange: 0,
        AttackCooldownSeconds: 0,
        AggroRange: 0,
        LoseAggroRange: 0);

    public void Tick(
        ulong tickId,
        double serverTimeSeconds,
        ReadOnlySpan<(PlayerId PlayerId, Vector3 Position)> players,
        VoxelWorld world,
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
                if (TryPickSpawnPosition(playerPos, world, isChunkReadyForPlayer, playerId, out var spawnPos))
                {
                    var mob = mobManager.CreateMob(DefaultCow);
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
        Func<PlayerId, int, bool> isChunkReadyForPlayer,
        PlayerId playerId,
        out Vector3 spawnPos)
    {
        spawnPos = default;

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

        var origin = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
        var lx = wx - origin.X;
        var lz = wz - origin.Z;
        if ((uint)lx >= (uint)VoxelHelper.ChunkSideSize || (uint)lz >= (uint)VoxelHelper.ChunkSideSize)
        {
            return false;
        }

        var heightTopFace = chunk.GetTerrainHeightAt(lx, lz);
        var startY = Math.Clamp(heightTopFace - 1, 0, VoxelHelper.MaxBlockPositionY);

        // The heightmap is "highest non-air", which can be leaves. Scan downward to find real ground.
        if (!TryFindGroundY(world, wx, wz, startY, out var groundY))
        {
            return false;
        }

        var spawnY = groundY + 1;

        if (!HasHeadroom(world, wx, spawnY, wz))
        {
            return false;
        }

        // Spawn centered on the block and slightly above the ground.
        spawnPos = new Vector3(wx + 0.5f, spawnY + 0.55f, wz + 0.5f);
        return true;
    }

    private static bool HasHeadroom(VoxelWorld world, int wx, int spawnY, int wz)
    {
        var a0 = world.GetBlockByPositionGlobalSafe(wx, spawnY, wz);
        if (a0 is not null && (!a0.Value.Block.IsReplaceable() || a0.Value.Block.IsLiquid()))
        {
            return false;
        }

        var a1 = world.GetBlockByPositionGlobalSafe(wx, spawnY + 1, wz);
        return a1 is null || a1.Value.Block.IsReplaceable() && !a1.Value.Block.IsLiquid();
    }

    private static bool TryFindGroundY(VoxelWorld world, int wx, int wz, int startY, out int groundY)
    {
        groundY = 0;

        // Scan a limited distance down; trees aren't that tall, and this is called rarely.
        const int maxScan = 48;
        var yMin = Math.Max(0, startY - maxScan);

        for (var y = startY; y >= yMin; y--)
        {
            var b = world.GetBlockByPositionGlobalSafe(wx, y, wz);
            if (b is null) continue;

            if (IsSuitableGround(b.Value.Block))
            {
                groundY = y;
                return true;
            }
        }

        return false;
    }

    private static bool IsSuitableGround(BlockId block)
    {
        if (block.IsLiquid()) return false;
        if (block.IsTree()) return false;
        if (!block.IsOpaque()) return false; // prevents leaves/alpha-test and many non-ground surfaces
        return block.IsSolid();
    }

    private static int RandomInRange(int minInclusive, int maxInclusive)
    {
        if (maxInclusive < minInclusive)
        {
            (minInclusive, maxInclusive) = (maxInclusive, minInclusive);
        }

        // Ensure we don't always pick the inner radius.
        if (minInclusive == maxInclusive)
        {
            return minInclusive;
        }

        return Random.Shared.Next(minInclusive, maxInclusive + 1);
    }
}

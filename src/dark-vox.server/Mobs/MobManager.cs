using DarkVox.Shared.State;

namespace DarkVox.Server.Mobs;

/// <summary>
/// Owns server-side mob entities for the active (loaded) world region.
/// This is a scaffold; Phase 1+ fills in spawning/AI/physics.
/// </summary>
public sealed class MobManager
{
    private readonly Dictionary<MobId, MobEntity> mobs = [];
    private ulong nextMobId = 1;

    public IReadOnlyDictionary<MobId, MobEntity> Mobs => mobs;

    public MobEntity CreateMob(MobDefinition def)
    {
        var entity = new MobEntity
        {
            Id = new MobId(nextMobId++),
            Definition = def,
            Health = def.MaxHealth,
        };

        mobs[entity.Id] = entity;
        return entity;
    }

    public bool TryGet(MobId id, out MobEntity? mob) => mobs.TryGetValue(id, out mob);

    public void Remove(MobId id) => mobs.Remove(id);

    public MobStateSnapshot BuildSnapshot(ulong tickId, double serverTimeSeconds)
    {
        if (mobs.Count == 0)
        {
            return new MobStateSnapshot(tickId, serverTimeSeconds, Mobs: []);
        }

        var arr = new MobSnapshot[mobs.Count];
        var i = 0;
        foreach (var mob in mobs.Values)
        {
            arr[i++] = mob.ToSnapshot();
        }

        return new MobStateSnapshot(tickId, serverTimeSeconds, arr);
    }
}

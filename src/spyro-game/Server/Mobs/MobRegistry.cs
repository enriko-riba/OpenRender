using SpyroGame.Shared.State;

namespace SpyroGame.Server.Mobs;

/// <summary>
/// Registry for all mob definitions.
/// </summary>
public static class MobRegistry
{
    private static readonly Dictionary<MobKind, MobDefinition> definitions = [];
    private static readonly List<MobDefinition> allDefinitions = [];

    public static IReadOnlyDictionary<MobKind, MobDefinition> Definitions => definitions;
    public static IReadOnlyList<MobDefinition> AllDefinitions => allDefinitions;

    static MobRegistry()
    {
        Register(new MobDefinition(
            Kind: MobKind.Cow,
            Category: MobCategory.Passive,
            HitboxWidth: 0.9f,
            HitboxHeight: 1.4f,
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
            LoseAggroRange: 0,
            SpawnLightLevelMin: 9,
            SpawnLightLevelMax: 15,
            SpawnWeight: 10
        ));

        Register(new MobDefinition(
            Kind: MobKind.Pig,
            Category: MobCategory.Passive,
            HitboxWidth: 0.9f,
            HitboxHeight: 0.9f,
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
            LoseAggroRange: 0,
            SpawnLightLevelMin: 9,
            SpawnLightLevelMax: 15,
            SpawnWeight: 10
        ));

        Register(new MobDefinition(
            Kind: MobKind.Zombie,
            Category: MobCategory.Hostile,
            HitboxWidth: 0.6f,
            HitboxHeight: 1.95f,
            MaxHealth: 20,
            WalkSpeed: 1.0f,
            RunSpeed: 2.5f,
            StepHeight: 0.6f,
            CanSwim: true,
            CanClimb: false,
            CanFly: false,
            BaseDamage: 3,
            AttackRange: 1.5f,
            AttackCooldownSeconds: 1.0f,
            AggroRange: 16,
            LoseAggroRange: 32,
            SpawnLightLevelMin: 0,
            SpawnLightLevelMax: 7,
            SpawnWeight: 100
        ));

        Register(new MobDefinition(
            Kind: MobKind.Skeleton,
            Category: MobCategory.Hostile,
            HitboxWidth: 0.6f,
            HitboxHeight: 1.99f,
            MaxHealth: 20,
            WalkSpeed: 2.0f,
            RunSpeed: 4.0f,
            StepHeight: 0.6f,
            CanSwim: false,
            CanClimb: false,
            CanFly: false,
            BaseDamage: 2, // Ranged usually, but melee for now
            AttackRange: 1.5f,
            AttackCooldownSeconds: 1.0f,
            AggroRange: 16,
            LoseAggroRange: 32,
            SpawnLightLevelMin: 0,
            SpawnLightLevelMax: 7,
            SpawnWeight: 80
        ));
    }

    private static void Register(MobDefinition def)
    {
        definitions[def.Kind] = def;
        allDefinitions.Add(def);
    }

    public static MobDefinition? Get(MobKind kind) => definitions.TryGetValue(kind, out var def) ? def : null;
}

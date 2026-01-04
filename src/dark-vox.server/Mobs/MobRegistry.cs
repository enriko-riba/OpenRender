using DarkVox.Shared.State;
using DarkVox.Shared.World.Registry;

namespace DarkVox.Server.Mobs;

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
        // === Passive Mobs ===
        Register(new MobDefinition(
            Kind: MobKind.Cow,
            Category: MobCategory.Passive,
            HitboxWidth: 0.9f,
            HitboxHeight: 1.4f,
            StepHeight: 0.6f,
            CanSwim: true,
            CanClimb: false,
            CanFly: false,
            MaxHealth: 10,
            BaseDamage: 0,
            AttackRange: 0,
            AttackCooldownSeconds: 0,
            AttackSpeed: 0,
            WalkSpeed: 2.0f,
            RunSpeed: 3.5f,
            AggroRange: 0,
            LoseAggroRange: 0,
            SpawnLightLevelMin: 9,
            SpawnLightLevelMax: 15,
            SpawnWeight: 10,
            ModelPath: "Resources/models/entity/cow.json",
            AnimationPath: "Resources/animations/cow_walk.json",
            RenderScaleX: 0.6f,
            RenderScaleY: 1.4f,
            RenderScaleZ: 1.5f,
            YawOffsetDegrees: 180f, // Cow model has UV front/back swapped, needs 180° correction
            Drops: new DropTable(
                new DropEntry(GameObjectId.RawBeef, 1, 3),
                new DropEntry(GameObjectId.Leather, 0, 2)
            ),
            XpDropMin: 1,
            XpDropMax: 3
        ));

        Register(new MobDefinition(
            Kind: MobKind.Pig,
            Category: MobCategory.Passive,
            HitboxWidth: 0.9f,
            HitboxHeight: 0.9f,
            StepHeight: 0.6f,
            CanSwim: true,
            CanClimb: false,
            CanFly: false,
            MaxHealth: 10,
            BaseDamage: 0,
            AttackRange: 0,
            AttackCooldownSeconds: 0,
            AttackSpeed: 0,
            WalkSpeed: 2.0f,
            RunSpeed: 3.5f,
            AggroRange: 0,
            LoseAggroRange: 0,
            SpawnLightLevelMin: 9,
            SpawnLightLevelMax: 15,
            SpawnWeight: 10,
            ModelPath: "Resources/models/entity/simple_block.json",
            AnimationPath: "Resources/animations/simple_walk.json",
            RenderScaleX: 0.9f,
            RenderScaleY: 0.9f,
            RenderScaleZ: 0.9f,
            Drops: new DropTable(
                new DropEntry(GameObjectId.RawPorkchop, 1, 3)
            ),
            XpDropMin: 1,
            XpDropMax: 3
        ));

        // === Hostile Mobs ===
        Register(new MobDefinition(
            Kind: MobKind.Zombie,
            Category: MobCategory.Hostile,
            HitboxWidth: 0.6f,
            HitboxHeight: 1.95f,
            StepHeight: 0.6f,
            CanSwim: true,
            CanClimb: false,
            CanFly: false,
            MaxHealth: 20,
            BaseDamage: 3,  // Easy: 2.5, Normal: 3, Hard: 4.5
            AttackRange: 1.5f,
            AttackCooldownSeconds: 1.0f,
            AttackSpeed: 1.0f,  // 1 attack per second
            WalkSpeed: 1.0f,
            RunSpeed: 2.5f,
            AggroRange: 40,  // Minecraft: 40 blocks
            LoseAggroRange: 40,
            KnockbackStrength: 0.4f,
            SpawnLightLevelMin: 0,
            SpawnLightLevelMax: 7,
            SpawnWeight: 100,
            ModelPath: "Resources/models/entity/simple_block_hostile.json",
            AnimationPath: "Resources/animations/simple_walk.json",
            RenderScaleX: 0.6f,
            RenderScaleY: 1.95f,
            RenderScaleZ: 0.6f,
            Drops: new DropTable(
                new DropEntry(GameObjectId.RottenFlesh, 0, 2)
            ),
            XpDropMin: 5,
            XpDropMax: 5
        ));

        Register(new MobDefinition(
            Kind: MobKind.Skeleton,
            Category: MobCategory.Hostile,
            HitboxWidth: 0.6f,
            HitboxHeight: 1.99f,
            StepHeight: 0.6f,
            CanSwim: false,
            CanClimb: false,
            CanFly: false,
            MaxHealth: 20,
            BaseDamage: 2,  // Ranged: 2-5 depending on difficulty
            AttackRange: 1.5f,  // TODO: Should be ranged attack
            AttackCooldownSeconds: 1.0f,
            AttackSpeed: 1.0f,  // 1 attack per second
            WalkSpeed: 2.0f,
            RunSpeed: 4.0f,
            AggroRange: 16,
            LoseAggroRange: 32,
            KnockbackStrength: 0.4f,
            SpawnLightLevelMin: 0,
            SpawnLightLevelMax: 7,
            SpawnWeight: 80,
            ModelPath: "Resources/models/entity/simple_block_hostile.json",
            AnimationPath: "Resources/animations/simple_walk.json",
            RenderScaleX: 0.6f,
            RenderScaleY: 1.99f,
            RenderScaleZ: 0.6f,
            Drops: new DropTable(
                new DropEntry(GameObjectId.Bone, 0, 2),
                new DropEntry(GameObjectId.Arrow, 0, 2)
            ),
            XpDropMin: 5,
            XpDropMax: 5
        ));
    }

    private static void Register(MobDefinition def)
    {
        definitions[def.Kind] = def;
        allDefinitions.Add(def);
    }

    public static MobDefinition? Get(MobKind kind) => definitions.TryGetValue(kind, out var def) ? def : null;
}

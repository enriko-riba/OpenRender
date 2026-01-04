# Combat System Documentation

This document describes the combat mechanics implemented in the game, following Minecraft-like patterns.

## Overview

The combat system is **server-authoritative** - all damage calculations, hit detection, and state changes happen on the server. The client sends attack intents and receives state updates.

## Core Components

### CombatSystem (`Server/Combat/CombatSystem.cs`)

The central class handling all combat logic:
- Player attacking mobs
- Mobs attacking players
- Damage calculation
- Knockback application
- Cooldown management

### Combat Flow

```
Player Click → ClientAttackMobMessage → Server validates → CombatSystem.ProcessPlayerAttackMob()
                                                      ↓
                                           Damage applied to mob
                                           Knockback applied
                                           Cooldown started
                                           XP/Drops generated (if mob dies)
```

```
Mob AI detects player → MobAiSystem queues attack intent → Server processes
                                                       ↓
                                            CombatSystem.ProcessMobAttackPlayer()
                                                       ↓
                                            Damage applied to player
                                            Knockback applied
                                            Invulnerability started
```

## Mechanics

### Attack Cooldown

**Player attacks** have a cooldown based on weapon attack speed:
- Cooldown duration = `1 / attackSpeed` seconds
- Fist: 4.0 attacks/sec → 0.25s cooldown
- Sword: 1.6 attacks/sec → 0.625s cooldown
- Axe: varies by material (0.8-1.0 attacks/sec)

**Mob attacks** use `AttackCooldownSeconds` from `MobDefinition`:
- Zombie: 1.0s between attacks
- Skeleton: 1.0s between attacks

### Damage Calculation

#### Player → Mob
```
baseDamage = weaponDamage (from held item, or 1.0 for fist)
if (critical hit):
    baseDamage *= 1.5
armorReduction = min(20, mobArmor) / 25
actualDamage = baseDamage * (1 - armorReduction)
```

Critical hits occur when:
- Player is falling (velocity.Y < 0)
- Player is not on ground

#### Mob → Player
```
baseDamage = mob.Definition.BaseDamage
// Player armor would reduce this (TODO: implement player armor)
actualDamage = ceil(baseDamage)
```

### Knockback

**On mobs** (when hit by player):
```
direction = normalize(mob.position - player.position)
mob.velocity.XZ += direction.XZ * 0.4 * 10
mob.velocity.Y = 0.4 * 10
```

**On players** (when hit by mob):
```
direction = normalize(player.position - mob.position)
player.ApplyKnockback(direction * strength * 10, verticalComponent * 10)
```

Knockback strength can vary per mob (default 0.4).

### Invulnerability Frames

After taking damage, entities have a brief immunity period:
- Duration: 0.5 seconds
- During this time, subsequent attacks deal no damage
- Prevents rapid multi-hit exploits

### Reach Distance

- **Survival mode**: 3.0 blocks
- **Creative mode**: 5.0 blocks

The reach check accounts for mob hitbox size:
```
maxDistance = reach + (mob.HitboxWidth * 0.5)
```

## Network Messages

### Client → Server

```csharp
record ClientAttackMobMessage(PlayerId PlayerId, MobId TargetMob) : IClientToServerMessage;
```

### Server → Client

Currently, combat results are reflected in the `GameStateSnapshot`:
- `MobSnapshot.Health` - current health
- Mob removal from snapshot indicates death

TODO: Consider adding explicit combat event messages for:
- Damage numbers display
- Critical hit effects
- Death notifications

## Integration Points

### LocalGameServer

- `SubmitAttack(PlayerId, MobId)` - entry point for player attacks
- Combat system created as field: `private readonly CombatSystem combatSystem = new();`
- Mob attacks processed after AI tick via `mobAiSystem.PendingAttacks`

### MobAiSystem

- Hostile mobs in `Chase` state queue attack intents when:
  - Within `AttackRange` of target player
  - Attack cooldown has elapsed
- Attack intents stored in `PendingAttacks` list

### Player

Combat-related fields:
- `attackCooldownRemaining` - time until next attack
- `invulnerabilityRemaining` - immunity duration
- `IsAlive` - death state

Combat methods:
- `CanAttack()` - check if cooldown has elapsed
- `StartAttackCooldown(speed)` - begin recovery
- `StartInvulnerability(duration)` - begin immunity
- `ApplyKnockback(horizontal, vertical)` - apply velocity

### MobEntity

Combat-related fields:
- `AttackCooldownRemaining` - mob attack recovery
- `HurtTimeRemaining` - visual hurt flash duration
- `Health` - current health
- `IsDead` - death flag

## Mob Combat Attributes

Defined in `MobDefinition`:

| Attribute | Description | Example (Zombie) |
|-----------|-------------|------------------|
| `BaseDamage` | Raw damage per hit | 3.0 |
| `AttackRange` | Max distance to hit | 1.5 blocks |
| `AttackCooldownSeconds` | Recovery time | 1.0s |
| `AttackSpeed` | Attacks per second | 1.0 |
| `KnockbackStrength` | Knockback multiplier | 0.4 |
| `ArmorPoints` | Damage reduction | 0.0 |

## Weapon Stats (Future)

When the item system is fully integrated:

| Weapon | Damage | Speed | Cooldown |
|--------|--------|-------|----------|
| Fist | 1.0 | 4.0 | 0.25s |
| Wooden Sword | 4.0 | 1.6 | 0.625s |
| Stone Sword | 5.0 | 1.6 | 0.625s |
| Iron Sword | 6.0 | 1.6 | 0.625s |
| Diamond Sword | 7.0 | 1.6 | 0.625s |
| Netherite Sword | 8.0 | 1.6 | 0.625s |

## Constants

```csharp
public const float BasePlayerReach = 3.0f;
public const float CreativePlayerReach = 5.0f;
public const float InvulnerabilitySeconds = 0.5f;
public const float BaseKnockbackStrength = 0.4f;
public const float KnockbackYComponent = 0.4f;
public const float CriticalHitMultiplier = 1.5f;
public const float BaseFistDamage = 1.0f;
```

## TODO / Future Work

1. **Player Armor** - Reduce incoming damage based on equipped armor
2. **Enchantments** - Sharpness, Protection, Knockback, etc.
3. **Sweep Attack** - Sword attacks hit multiple nearby mobs
4. **Shield Blocking** - Block incoming attacks with shield
5. **Projectiles** - Arrows, snowballs, etc.
6. **Status Effects** - Poison, wither, regeneration affecting combat
7. **Difficulty Scaling** - Mob damage varies by difficulty setting
8. **Combat Events** - Network messages for visual feedback
9. **Death Animation** - Mob death sequence before removal
10. **Loot Generation** - Drop items from DropTable on mob death

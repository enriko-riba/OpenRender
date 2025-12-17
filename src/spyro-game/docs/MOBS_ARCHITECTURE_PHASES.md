# Mobs & Spawning Architecture (Phased)

This document proposes a Minecraft-like mob system that fits the current server-authoritative, client-rendering architecture.

Key constraints
- Server-authoritative simulation (movement, AI, combat, spawns).
- Client renders + interpolates snapshots.
- Mobs are **active only in loaded/ready chunks around players** (Minecraft-style). No offscreen persistence yet.
- Day/night time becomes **server-driven**. Client consumes server time and only updates render-facing state (sun, directional light, ambient, skybox).

---

## Phase 0 — Foundations (Types + Messaging + Time Ownership)

### Goals
- Establish shared types for mobs and network messages.
- Add a separate mob snapshot stream (do not bloat the existing `GameStateSnapshot` yet).
- Establish server-driven in-game time source.

### Server design
- Add `WorldTimeService` (server): owns “in-game time” progression (time-of-day, day count).
- Include in-game time in a dedicated message (e.g. `ServerWorldTimeMessage`) or piggyback on existing server snapshot cadence.
- Add `MobManager` (server): holds all active mob entities keyed by `MobId`.

### Client design
- Client receives in-game time snapshots.
- Client keeps only rendering concerns:
  - sun direction
  - day/night ambient + directional light intensity
  - skybox selection/tint

### Server tasks
- Add shared state types: `MobId`, `MobKind`, `MobSnapshot`, `MobStateSnapshot`.
- Add `ServerMobStateMessage` (server→client) and client queue plumbing.
- Add `WorldTimeService` + message (can be implemented in later phase, but types should exist now).

### Client tasks
- Add snapshot queue handling for `ServerMobStateMessage`.
- Add `ClientMobWorld` container to store interpolated render-state.

---

## Phase 1 — Basic Spawning + Simple Rendering

### Goals
- Spawn a small set of mobs around players.
- Render them with simple multi-draw (extra draw calls acceptable).

### Spawn rules (Minecraft-like)
- Spawn candidates are chosen from **loaded/ready chunks** in a ring around each player.
  - No-spawn radius: e.g. 24 blocks.
  - Spawn radius: e.g. 24–64 blocks.
- Validate spawn location:
  - Solid “spawnable” ground.
  - Sufficient headroom for mob hitbox.
  - Not inside water unless aquatic.
- Honor sky light + day/night:
  - Hostile mobs: require low sky light at position (e.g. `skyLight <= 7`) and time-of-day indicates night (or sufficiently dark).
  - Passive mobs: require high sky light (e.g. `skyLight >= 9`) and daytime.

### Server architecture
- `MobSpawnSystem`
  - Runs per server tick with a strict attempt budget.
  - Uses player positions + ready chunks to propose candidate positions.
  - Uses chunk light (`LightData`) for sky light at the column to enforce spawn constraints.
  - Enforces caps:
    - global cap per category
    - per-player local cap
    - per-chunk cap

### Client rendering architecture
- `MobModelDefinition` and `MobAnimationDefinition` stored as JSON in `spyro-game/Resources/Mobs/`.
- Minimal first implementation:
  - Procedural animation (walk cycle, idle bob, attack swing) + per-mob constants.
  - Render as composed boxes/parts (Minecraft-like).
  - Extra draw calls are acceptable: start with 1–6 draws per mob.

### Server tasks
- Implement `MobSpawnSystem` with candidate sampling + validation.
- Implement `MobManager` tick loop and periodic `MobStateSnapshot` publishing.

### Client tasks
- Implement `MobRenderer` that loads JSON model definitions.
- Implement `ClientMobWorld` interpolation (snapshot → render state).

---

## Phase 2 — Movement, Collision, and Basic AI

### Goals
- Mobs move convincingly and interact with terrain.

### Server movement/physics
- Create shared kinematic mover used by player and mobs:
  - gravity, friction, air control
  - step-up (`StepHeight`)
  - wall sliding
  - water buoyancy if `CanSwim`
- Collision queries use existing voxel collision representations (spans/heightmaps).

### Basic AI
- Local steering first (cheap):
  - wander within radius
  - chase player if hostile and in range
  - lose aggro beyond range / after timeout
- Later upgrade path: A* on a low-res 2D grid per chunk.

### Server tasks
- Add `MobPhysicsSystem`.
- Add `MobAiSystem` (wander/chase).

### Client tasks
- Add animations keyed off snapshot state:
  - Idle/Walk/Run/Attack/Hurt/Die

---

## Phase 3 — Combat, Loot, XP, and Equipment Hooks

### Goals
- Make fighting mobs meaningful and consistent with inventory/equipment.

### Combat rules
- Server-authoritative hit validation.
- Player attack damage depends on selected hotbar item:
  - fists (baseline damage)
  - weapon item (damage bonus + attack speed)
- Mobs have:
  - health, armor scalar (optional)
  - hurt cooldown / invulnerability window
  - knockback

### Drops and XP
- Data-driven `DropTable`:
  - entries: item id, count range, probability
- On death:
  - spawn item drops near corpse OR direct-to-inventory (simpler first).
  - grant XP (server-side stat) to player.

### Tasks
- Add `ClientAttackMessage` or `ClientUseItemMessage`.
- Add `ServerMobEventMessage` for death/hurt events (optional) or encode in snapshots.

---

## Phase 4 — Polishing & Scaling

- Spawn tuning (biome-specific weights, pack spawning).
- Despawn rules (distance, time, chunk unload).
- Better pathing.
- Animation clips loaded from JSON (keyframe curves).
- Rendering optimizations (instancing per mob type, SSBO transforms).

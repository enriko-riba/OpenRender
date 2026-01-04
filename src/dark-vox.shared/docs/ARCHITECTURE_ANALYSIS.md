# Dark-Vox Architecture Analysis

## Current Project Structure

```
dark-vox.shared/     - Shared types, snapshots, interfaces, world logic
dark-vox.server/     - Server-authoritative gameplay, terrain generation, mobs
dark-vox/            - Client: rendering, UI, input handling
```

## Completed Improvements (This Refactor)

### 1. ✅ Created `PlayerConstants` (dark-vox.shared/Gameplay/PlayerConstants.cs)
Centralized all player physics and collision constants:
- Collider dimensions (HalfWidth, Height, EyeHeight, StepHeight)
- Physics constants (Gravity, MoveSpeed, JumpForce, AirControl, etc.)
- Speed modifiers (SprintMultiplier, CrouchMultiplier, GhostModeMultiplier)
- Combat constants (AttackCooldown, InvulnerabilityDuration, AttackReach)

### 2. ✅ Created `PlayerPhysicsCore` (dark-vox.shared/Gameplay/PlayerPhysicsCore.cs)
Shared pure physics simulation logic:
- `CreateCollider()` - Creates kinematic collider from shared constants
- `BuildMovementVector()` - Converts input to world-space movement
- `SimulateGhostMode()` - Ghost/fly mode physics
- `SimulateMovement()` - Standard physics with gravity/collision
- `TryJump()` - Jump initiation logic
- `ApplyKnockback()` - Combat knockback
- `ApplyLookRotation()` - Server-side look rotation matching CameraFps

### 3. ✅ Refactored Server Player (dark-vox.server/Gameplay/Player.cs)
- Now uses `PlayerConstants` for all physics values
- Uses `PlayerPhysicsCore.CreateCollider()` for collider
- Uses `PlayerPhysicsCore.ApplyLookRotation()` for camera sync
- Uses `PlayerPhysicsCore.BuildMovementVector()` for movement

### 4. ✅ Refactored Client Player (dark-vox/Components/Player.cs)
- Removed all duplicated constant definitions
- Uses `PlayerConstants` throughout
- Uses `PlayerPhysicsCore.CreateCollider()` for collider
- Uses `PlayerPhysicsCore.BuildMovementVector()` in Simulate()
- Added documentation clarifying client is view-only

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                        dark-vox.shared                          │
├─────────────────────────────────────────────────────────────────┤
│  Gameplay/                                                      │
│    PlayerConstants.cs      - Shared physics constants           │
│    PlayerPhysicsCore.cs    - Shared physics simulation          │
│    PlayerAttributes.cs     - Health/hunger/stamina              │
│    Inventory.cs            - Inventory management               │
│                                                                 │
│  State/                                                         │
│    PlayerSnapshot.cs       - Serializable player state          │
│    GameStateSnapshot.cs    - Full game state                    │
│                                                                 │
│  World/                                                         │
│    VoxelWorld.cs           - Shared voxel world                 │
│    VoxelKinematicMover.cs  - Collision/movement                 │
│                                                                 │
│  Net/                                                           │
│    Messages.cs             - Client/server messages             │
│    IGameConnection.cs      - Connection interface               │
└─────────────────────────────────────────────────────────────────┘
                              ▲
                              │ references
              ┌───────────────┴───────────────┐
              │                               │
┌─────────────▼─────────────┐   ┌────────────▼────────────┐
│     dark-vox.server       │   │        dark-vox         │
├───────────────────────────┤   ├─────────────────────────┤
│  Gameplay/                │   │  Components/            │
│    Player.cs (server)     │   │    Player.cs (client)   │
│    - Authoritative sim    │   │    - View/camera only   │
│    - Uses shared physics  │   │    - Applies snapshots  │
│                           │   │    - Uses shared consts │
│  World/Generation/        │   │                         │
│    - Terrain generation   │   │  Client/                │
│    - Only on server       │   │    - GL rendering       │
│                           │   │    - Input handling     │
│  Mobs/                    │   │    - UI                 │
│    - Mob simulation       │   │                         │
│    - Only on server       │   │                         │
└───────────────────────────┘   └─────────────────────────┘
```

## Remaining Issues (Future Work)

### 1. Client References Server Project
`dark-vox.csproj` still references `dark-vox.server.csproj`.
To enable true network separation:
- Remove the reference
- Server becomes standalone process
- Client connects via network

### 2. Block Interaction Logic Duplication
Both client and server have `TryBreakBlock`/`TryPlaceBlock`.
Client version is for local UI feedback; server is authoritative.
Could extract shared validation in future for client-side prediction.

### 3. No IPlayerState Interface
Could add interface for polymorphic player handling if needed.
`PlayerSnapshot` already provides shared state representation.

## Network Separation Roadmap

### Phase 1: Current State ✅
- Shared constants and physics
- In-memory message passing
- Single process

### Phase 2: Transport Abstraction
- Replace `InMemoryDuplexConnection` with network implementation
- Add message serialization (already have snapshot types)
- Keep in-process option for local play

### Phase 3: Standalone Server
- Remove client→server project reference
- Create `dark-vox.server.exe` entry point
- Add configuration for listen address/port

### Phase 4: Production Features
- Latency compensation
- Client-side prediction using shared physics
- Server reconciliation

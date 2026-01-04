# Dark Vox Game

A voxel-based 3D game built on the OpenRender engine.

## Quick Start

```bash
cd src/dark-vox
dotnet run
```

This launches the game host, which runs both the simulation (server-side) and rendering/input (client-side) in-process.

## Project Structure

```
src/
├── dark-vox/                  # Game host / executable
│   ├── Client/                # Client-side systems (rendering, local presentation, content loading)
│   │   ├── Content/           # JSON-driven content (entity models, animations, etc.)
│   │   ├── Rendering/         # GPU/GL renderers (terrain, mobs, dropped items, buffers)
│   │   └── Terrain/           # Client-facing terrain wiring
│   ├── Server/                # Server-side simulation (world, generation, items, mobs, streaming)
│   │   ├── Combat/            # Combat simulation
│   │   ├── Items/             # Drop/item simulation
│   │   ├── Mobs/              # Mob AI/physics/spawning
│   │   ├── Streaming/         # Chunk streaming and visibility budgets
│   │   └── World/             # Terrain config + generation pipeline
│   ├── GameScene.cs           # Main gameplay scene
│   ├── TerrainLoadingScene.cs # Loading screen / initialization pipeline
│   └── Program.cs             # Entry point
├── dark-vox.shared/           # Shared types used by client + server
│   ├── Abstractions/          # Interfaces used across layers
│   ├── Commands/              # Shared command types
│   ├── Registry/              # Block/biome registries + IDs
│   └── State/                 # Serializable snapshots/deltas used for syncing & persistence
└── dark-vox.tests/            # Automated tests (generation, gameplay, collision, serialization)
```

## Client vs Server

- **Server-side** (`dark-vox/Server/`) owns authoritative simulation: terrain generation, chunk streaming, mobs, items, and time.
- **Client-side** (`dark-vox/Client/`) owns presentation: rendering, content/model loading, and client-only helpers.
- **Shared** (`dark-vox.shared/`) contains common contracts and data types consumed by both sides (IDs, registries, snapshots, and service abstractions).

# Spyro Game

A voxel-based 3D platformer built on the OpenRender engine.

## Quick Start

```bash
cd src/spyro-game
dotnet run
```

## Project Structure

```
src/spyro-game/
├── Components/         # Game-specific components (ChunkRenderer, etc.)
├── Shaders/           # Compute and rendering shaders
├── World/             # Voxel world system (terrain, chunks, etc.)
├── docs/              # Project documentation
│   ├── terrain/
│   │   ├── PLAN_TERRAIN_AND_BIOMES.md   # Architecture & detailed plan
│   │   └── PROGRESS.md                   # Progress tracker & next steps
├── MainScene.cs       # Main game scene
└── LoadingScene2.cs   # Loading screen
```

## Current Development

### Active Work: GPU Terrain System Redesign
**Status**: 🔴 Planning Phase  
**Timeline**: Week 1 of 5  
**Goal**: Replace CPU-based terrain with GPU-first streaming system

📋 **[Track Progress →](docs/terrain/PROGRESS.md)**  
📖 **[Architecture/Plan →](docs/terrain/PLAN_TERRAIN_AND_BIOMES.md)**

### Known Issues
- Terrain generation is slow (~45ms per 64 chunks)
- Memory usage high (~800MB VRAM)
- Shader execution issues (buffers returning zeros)

**Resolution**: Complete system redesign in progress

## Dependencies

- **OpenRender** - Generic rendering engine (sibling project)
- **TextRendering** - Font/text rendering library
- OpenTK 4.x - OpenGL bindings
- .NET 10

## Key Systems

### Voxel World (`World/`)
- Chunk-based terrain (16×16×128 voxels per chunk)
- GPU compute shader generation
- Edit system (breaking/placing blocks)
- Collision detection

### Rendering (`Components/ChunkRenderer.cs`)
- Multi-draw indirect (MDI) batching
- Instanced voxel rendering
- Frustum culling
- LOD system (planned)

### Shaders (`Shaders/`)
- `compute-generate.comp` – Chunk terrain generation + biome evaluation
- `compute-light.comp` – Column skylight/blocklight solve
- `compute-column-spans.comp` – Column metadata for streaming & height cache
- `compute-apply-edits.comp` – Apply queued voxel edits before upload
- `compute-frustum.comp` – GPU frustum culling / stats
- `voxel-terrain.*` – Primary terrain material
- `block-outline.*` – Picked block overlay
- `water.*` – Water surface pass
- `skybox-sun.*` – Gradient sky/sun billboard
- `terrain-*.glsl` – Shared include files (noise, biomes, bindings, etc.)

## Documentation

- **[Terrain Progress Tracker](docs/terrain/PROGRESS.md)** - Current work status
- **[Terrain Plan](docs/terrain/PLAN_TERRAIN_AND_BIOMES.md)** - Detailed architecture/plan
- **[Terrain Architecture (legacy CPU notes)](docs/terrain/terrain_generation_architecture.md)** - Prior design for reference

## Development Notes

### Performance Targets (60 FPS)
- Generation: <2ms per 64 chunks
- Frame time: <16.67ms
- Memory: <500MB VRAM
- Visible chunks: 2000+

### Current Metrics
- Generation: ~45ms ⚠️
- Frame time: ~25-40ms ⚠️
- Memory: ~800MB ⚠️
- Visible chunks: ~400 ⚠️

## Contributing

This is a personal project, but feel free to:
1. Review the architecture docs
2. Suggest optimizations
3. Report bugs in Issues

---

**Last Updated**: 2025-01-14  
**Engine**: OpenRender (custom)  
**Platform**: Windows/Linux (OpenGL 4.6)

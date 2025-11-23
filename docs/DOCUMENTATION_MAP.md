# Project Documentation Map

This document provides a quick reference to all documentation in the workspace.

---

## 📚 Core Documentation

### OpenRender Engine (Generic)
- **[README.md](../README.md)** - Main project overview and API documentation
- **Location**: Root directory
- **Scope**: Generic rendering engine, shared across all projects

### Spyro Game (Game-Specific)
- **[README.md](../src/spyro-game/README.md)** - Game project overview
- **[Terrain Progress Tracker](../src/spyro-game/docs/TERRAIN_PROGRESS.md)** - 🎯 **START HERE** for current work
- **[GPU Terrain Architecture](GPU-Terrain-Architecture.md)** - Technical specification (v2.0)
- **[Original Terrain Requirements](../src/spyro-game/docs/terrain/VoxelWorld-Streaming-Terrain-Doc.md)** - Legacy requirements

---

## 🎯 Quick Navigation

### For Implementation Work
1. **Check current status**: [Terrain Progress Tracker](../src/spyro-game/docs/TERRAIN_PROGRESS.md)
2. **Review architecture**: [GPU Terrain Architecture](GPU-Terrain-Architecture.md)
3. **See original requirements**: [Voxel World Doc](../src/spyro-game/docs/terrain/VoxelWorld-Streaming-Terrain-Doc.md)

### For Understanding the System
1. **High-level overview**: [GPU Architecture § Overview](GPU-Terrain-Architecture.md#overview)
2. **Data structures**: [GPU Architecture § Data Structures](GPU-Terrain-Architecture.md#data-structures)
3. **Pipeline stages**: [GPU Architecture § Pipeline Stages](GPU-Terrain-Architecture.md#pipeline-stages)

### For Progress Tracking
1. **Current sprint**: [Progress § Current Sprint](../src/spyro-game/docs/TERRAIN_PROGRESS.md#current-sprint)
2. **Phase status**: [Progress § Implementation Status](../src/spyro-game/docs/TERRAIN_PROGRESS.md#implementation-status)
3. **Decision log**: [Progress § Decision Log](../src/spyro-game/docs/TERRAIN_PROGRESS.md#decision-log)

---

## 📁 Documentation Structure

```
C:\repos\opentk\
├── README.md                          # Main OpenRender documentation
├── docs/
│   ├── THIS_FILE.md                   # This documentation map
│   └── GPU-Terrain-Architecture.md    # Technical architecture (v2.0)
│
└── src/
    ├── OpenRender/                    # Generic rendering engine
    │   └── (No game-specific docs)
    │
    └── spyro-game/                    # Voxel game project
        ├── README.md                  # Game project overview
        └── docs/
            ├── TERRAIN_PROGRESS.md    # 🎯 Current work tracker
            └── terrain/
                ├── VoxelWorld-Streaming-Terrain-Doc.md  # Original requirements
                └── terrain_generation_architecture.md   # Legacy design doc
```

---

## 🔍 Document Purposes

### [README.md](../README.md) (Root)
**Purpose**: Main project documentation  
**Audience**: Anyone using OpenRender engine  
**Scope**: Generic rendering features, API reference, shader binding conventions  
**When to Read**: Learning OpenRender or integrating it into a project

### [GPU-Terrain-Architecture.md](GPU-Terrain-Architecture.md)
**Purpose**: Complete technical specification of new terrain system  
**Audience**: Developers implementing the GPU terrain system  
**Scope**: Architecture design, data structures, pipeline stages, shaders  
**When to Read**: Before implementing any phase of the new system  
**Sections**:
- Overview & core architecture
- Detailed data structures (GPU/CPU)
- 5 pipeline stages with shader code
- Implementation phases
- Performance targets

### [TERRAIN_PROGRESS.md](../src/spyro-game/docs/TERRAIN_PROGRESS.md)
**Purpose**: Day-to-day progress tracking and task management  
**Audience**: Active developers, project managers  
**Scope**: Sprint goals, task lists, daily updates, metrics  
**When to Read**: **At the start of every work session**  
**Sections**:
- Phase status (6 phases with checkboxes)
- Current sprint goals
- Daily progress log
- Performance metrics
- Known issues

### [VoxelWorld-Streaming-Terrain-Doc.md](../src/spyro-game/docs/terrain/VoxelWorld-Streaming-Terrain-Doc.md)
**Purpose**: Original requirements and feature specifications  
**Audience**: Anyone needing to understand system requirements  
**Scope**: Feature list, constraints, API design  
**When to Read**: Clarifying requirements or verifying feature completeness

---

## 🚀 Development Workflow

### Starting a New Work Session
1. Open [TERRAIN_PROGRESS.md](../src/spyro-game/docs/TERRAIN_PROGRESS.md)
2. Check **Current Sprint** section
3. Review **This Week's Tasks**
4. Update **Daily Progress** as you work
5. Mark tasks complete with ✅

### Implementing a New Phase
1. Read phase description in [GPU-Terrain-Architecture.md](GPU-Terrain-Architecture.md#implementation-phases)
2. Review relevant pipeline stages
3. Check [TERRAIN_PROGRESS.md](../src/spyro-game/docs/TERRAIN_PROGRESS.md) for task breakdown
4. Update progress tracker as tasks complete
5. Update **Decision Log** for significant choices

### Debugging Issues
1. Check [TERRAIN_PROGRESS.md § Known Issues](../src/spyro-game/docs/TERRAIN_PROGRESS.md#known-issues-current-system)
2. Review relevant shader in [GPU-Terrain-Architecture.md § Pipeline Stages](GPU-Terrain-Architecture.md#pipeline-stages)
3. Use buffer diagnostics (Phase 1 deliverable)
4. Log observations in progress tracker

### Completing a Phase
1. Mark all tasks ✅ in [TERRAIN_PROGRESS.md](../src/spyro-game/docs/TERRAIN_PROGRESS.md)
2. Update **Performance Metrics**
3. Record any **Decision Log** entries
4. Update **Next Action** for next phase
5. Commit changes with descriptive message

---

## 📊 Status Legend

| Symbol | Meaning |
|--------|---------|
| 🔴 | Not Started / Critical Issue |
| 🟡 | In Progress / Warning |
| 🟢 | Completed / Working |
| ⬜ | Task not started |
| ✅ | Task completed |
| ⚠️ | Performance below target |

---

## 🔗 External References

### OpenGL Documentation
- [Khronos OpenGL Wiki](https://www.khronos.org/opengl/wiki/)
- [Compute Shaders](https://www.khronos.org/opengl/wiki/Compute_Shader)
- [SSBO Guide](https://www.khronos.org/opengl/wiki/Shader_Storage_Buffer_Object)

### Voxel Engine Resources
- [0fps: Meshing in Minecraft](https://0fps.net/2012/06/30/meshing-in-a-minecraft-game-2/)
- [GPU Gems 3: Procedural Terrains](https://developer.nvidia.com/gpugems/gpugems3/part-i-geometry/chapter-1-generating-complex-procedural-terrains-using-gpu)

---

## ❓ Common Questions

### Where do I start?
→ [TERRAIN_PROGRESS.md § Current Sprint](../src/spyro-game/docs/TERRAIN_PROGRESS.md#current-sprint)

### How does the new system work?
→ [GPU-Terrain-Architecture.md § Core Architecture](GPU-Terrain-Architecture.md#core-architecture)

### What's the implementation timeline?
→ [GPU-Terrain-Architecture.md § Implementation Phases](GPU-Terrain-Architecture.md#implementation-phases)

### What files need to be changed?
→ [TERRAIN_PROGRESS.md § Files to Modify/Create](../src/spyro-game/docs/TERRAIN_PROGRESS.md#files-to-modifycreate)

### What are the performance targets?
→ [GPU-Terrain-Architecture.md § Technical Requirements](GPU-Terrain-Architecture.md#technical-requirements)

### Why was the system redesigned?
→ [TERRAIN_PROGRESS.md § Decision Log](../src/spyro-game/docs/TERRAIN_PROGRESS.md#decision-log)

---

## 📝 Document Maintenance

### When to Update Each Doc

**[TERRAIN_PROGRESS.md](../src/spyro-game/docs/TERRAIN_PROGRESS.md)**
- ✅ Daily: Update progress log
- ✅ Weekly: Review sprint goals
- ✅ Per-phase: Update metrics

**[GPU-Terrain-Architecture.md](GPU-Terrain-Architecture.md)**
- ✅ As needed: Clarify technical details
- ✅ Rarely: Change architecture (with decision log entry)

**[README.md](../README.md) (Root)**
- ✅ When adding major features to OpenRender
- ❌ Never for game-specific features

**[spyro-game/README.md](../src/spyro-game/README.md)**
- ✅ Weekly: Update status
- ✅ When metrics change significantly

---

**Created**: 2025-01-14  
**Purpose**: Navigation aid for project documentation  
**Audience**: All team members and contributors

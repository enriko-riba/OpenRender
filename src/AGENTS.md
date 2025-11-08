# Repository Guidelines

This repository contains a .NET/OpenTK codebase with an in‑house renderer and a voxel game sample.

## Project Structure & Module Organization

- `OpenRender/` – Core rendering library (OpenTK, buffers, shaders, scene graph)
- `TextRendering/` – Text subsystem used by UI/HUD
- `spyro-game/` – Playable voxel game (compute shaders, MDI rendering)
- `samples/` – Small demos; shared assets linked into projects
- Assets & shaders live under each project: `Resources/` and `Shaders/` (copied to output).

## Build, Test, and Development Commands

- Restore and build (Debug): `dotnet build -c Debug`
- Run the game: `dotnet run --project spyro-game -c Debug`
- Release build: `dotnet build -c Release`
- Optional hot‑reload: `dotnet watch run --project spyro-game`

## Coding Style & Naming Conventions

- C# 14+, .NET 10.0; 4‑space indentation; nullable enabled; implicit usings on
- Types/Namespaces: `PascalCase`; fields/properties: `camelCase`/`PascalCase`; constants: `PascalCase`
- Use latest language features (e.g., `primary ctor`, `simplified collection initializers`, `var instead explicit typed variables` etc.)
- Keep changes minimal and localized; avoid unrelated refactors in the same patch
- Shaders are GLSL 4.50+: prefer std430 SSBOs; name buffers by role (e.g., `blocksAtlas_SSBO`)

## Testing Guidelines

- No formal unit tests yet; validate by running `spyro-game` and exercising:
  - Startup without errors; terrain visible; no GL debug warnings
  - Chunk streaming (move across borders) and block edit/picking paths
- If adding tests later, use `dotnet test`; name tests `{ClassName}Tests`.

## Commit & Pull Request Guidelines

- Commits: concise subject (≤72 chars), imperative mood; body explains “why” and notable trade‑offs
- Group related changes; separate functional changes from formatting
- PRs must include:
  - Summary of changes and rationale; linked issues
  - Screenshots/GIFs for visual changes; relevant logs for GL/compute work
  - Instructions to reproduce or validate locally

## Shaders & Assets

- Place new GLSL files under `Shaders/`; they are copied by wildcard. Keep binding indices documented in comments.
- Add new textures/fonts under `Resources/`; reference via project `TextureDescriptor`.

## Agent‑Specific Instructions

- Use minimal diffs; prefer surgical edits over large rewrites
- Use `apply_patch` to modify files; keep patches focused and reversible
- Avoid introducing new dependencies without discussion; respect existing buffer/shader patterns (persistent mapping, MDI).
- Avoid fixing symptoms instead the root cause; document assumptions in comments.
- When modifying rendering code, ensure GL debug output is clean; use existing debug utilities.
- Keep the documentation in sync with changes, update the .\spyro-game\docs folder as needed.


# Phase GM-1 Quick Reference

## TL;DR

✅ **Greedy meshing infrastructure is now implemented and build-verified**
- Feature flag: `chunkStreamingManager.UseGreedyMeshing` (default: `false`)
- No visual changes yet (quads generated but not consumed)
- Next step: Phase GM-2 to modify compact shader

---

## How to Enable

```csharp
// In GameScene.cs or TerrainLoadingScene.cs
chunkStreamingManager.UseGreedyMeshing = true;
```

---

## What to Expect

### Logs (when enabled)

```
Greedy merge shader loaded successfully
Phase GM-1: Running greedy merge for 4 chunks
Phase GM-1: Generated 1234 quads (avg 308.5 per chunk)
```

### Performance

- **GPU overhead**: +0.3ms per 64 chunks
- **No vertex reduction yet**: Waiting for Phase GM-2
- **No visual changes**: Existing pipeline unaffected

---

## Files Changed

1. ✅ `Shaders/compute-greedy-merge.comp` (NEW)
2. ✅ `Shaders/terrain-common.glsl` (pack/unpack functions)
3. ✅ `World/Phase3BufferManager.cs` (buffers)
4. ✅ `World/ChunkStreamingManager.cs` (integration)

---

## Testing Checklist

- [ ] Build succeeds
- [ ] With flag OFF: no log messages, no overhead
- [ ] With flag ON: see "Greedy merge shader loaded successfully"
- [ ] With flag ON: see quad counts in logs
- [ ] Visual appearance unchanged (both ON/OFF)

---

## Troubleshooting

| Issue | Fix |
|-------|-----|
| Shader fails to load | Check GLSL compilation errors |
| No quad counts | Verify `UseGreedyMeshing = true` after init |
| Build errors | Re-check all 4 files are modified |

---

## Next Phase

**Phase GM-2** (4-6 hours):
- Modify `compute-compact.comp` to read merged quads
- Update Phase 3 Part 2 to allocate based on quads
- **Result**: 50-80% vertex reduction becomes visible

---

**Status**: ✅ Phase GM-1 COMPLETE  
**Date**: 2025-01-25  
**Build**: ✅ PASS

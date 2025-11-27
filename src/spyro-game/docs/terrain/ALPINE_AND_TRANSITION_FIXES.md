# Additional Biome Fixes - Alpine & Transitions

## Issues Fixed

### 1. ✅ Alpine Now Appears in Cold Lowlands

**Problem**: Alpine biome was ONLY altitude-based, meaning cold flat areas (like arctic tundra) never got snow.

**Solution**: Added temperature-based Alpine influence alongside altitude.

**Code Changes** (`terrain-common.glsl` - `getBiomeId()` function):

```glsl
// OLD: Only altitude
float alpineFavor = altitudeInfluence * 0.7 + boundaryNoise * 0.3;

// NEW: Altitude OR cold temperature
float coldInfluence = smoothstep(0.25, 0.15, t); // Very cold = Alpine
float alpineFavor = max(altitudeInfluence, coldInfluence) * 0.7 + boundaryNoise * 0.3;

// Also lowered threshold: 0.65 → 0.55 (easier to get Alpine in cold regions)
if (alpineFavor > 0.55) return ALPINE_BIOME_ID;
```

**How It Works**:
- `t < 0.15` (freezing cold) → Full Alpine influence
- `t = 0.20` (very cold) → 50% Alpine influence  
- `t > 0.25` (cool) → No cold-based Alpine

- `elevation > 230` (high mountain) → Full Alpine influence
- `elevation = 200` (alpine threshold) → 50% Alpine influence
- `elevation < 170` (lowland) → No altitude-based Alpine

- **Key**: `max()` means **either** condition can trigger Alpine!

**Result**: 
- ❄️ High mountains get snow (as before)
- ❄️ Cold lowlands get snow (NEW!)
- ❄️ Arctic regions become snowy tundra
- 🌲 Warm mountains stay forested

---

### 2. ❌ Straight Lines Between ALL Biomes (STILL AN ISSUE)

**Problem**: The smooth transition with `FeatherWidth` is applied by the **Voronoi system** (`getBiomeWeights()`), which already works for ALL biomes! 

**Status**: This should already be working if `FeatherWidth = 2f` as we set earlier.

**Why you might still see lines**:

#### A. Config Not Reloaded
If you're using a saved config file (`terrainconfig.json` or similar), it might still have the old `FeatherWidth: 12` value.

**Check**: Look for terrain config file and verify:
```json
{
  "BiomeRegions": {
    "FeatherWidth": 2.0  // Should be 2, not 12
  }
}
```

#### B. Chunks Not Regenerated
Existing chunks were generated with the old settings. You need to:
- Delete chunk files, OR
- Fly to new ungenerated areas

#### C. Different Biome Scales
If Taiga and Tundra have very similar climate requirements, they might appear in the same Voronoi cell with sharp LUT-based transitions.

**Solution**: Ensure biomes have distinct temperature/humidity ranges in `BiomeDefinition.DefaultSet()`.

---

## Testing the Alpine Fix

### Expected Behavior:

**High Mountains (any temperature)**:
- Elevation > 230 → Alpine (snowy peaks)
- Forest/Plains below, snow above

**Cold Lowlands (any elevation)**:
- Temperature < 0.15 → Alpine (arctic tundra with snow)
- Even at sea level, if cold enough

**Warm Lowlands**:
- Temperature > 0.25, Elevation < 170 → No Alpine
- Normal biomes (plains, desert, etc.)

### Test Locations:

1. **Mountain Test**: Fly to high elevation (y > 230)
   - Should see snow regardless of latitude

2. **Arctic Test**: Fly to very cold region (far north/south if using latitude-based temp)
   - Should see snow even at sea level

3. **Transition Test**: Look at boundaries between any two biomes
   - Should see 2-block smooth transition (if FeatherWidth = 2)
   - Not spray-paint effect

---

## Configuration Values

### Alpine Behavior Control:

**In `TerrainConfig.cs`**:
```csharp
public float AlpineElevation { get; set; } = 200f;  // Mountain snow line
public float BaseTemperature { get; set; } = 0.5f;   // World avg temp
public float LapseRate { get; set; } = 0.002f;       // Temp drop per block altitude
```

**In Shader** (hardcoded thresholds):
```glsl
// Temperature threshold for cold-based Alpine
float coldInfluence = smoothstep(0.25, 0.15, t);
//                                ^^^^  ^^^^
//                                Cool  Freezing

// Alpine favorability threshold
if (alpineFavor > 0.55) return ALPINE_BIOME_ID;
//                ^^^^
//                Lower = more Alpine in cold regions
//                Higher = less Alpine, only high mountains
```

### Biome Transition Control:

**In `TerrainConfig.cs`**:
```csharp
public sealed class BiomeRegionParams
{
    public float FeatherWidth { get; set; } = 2f;  // MUST be 2, not 12!
    public float CellSizeChunks { get; set; } = 4f; // Voronoi cell size
}
```

**Adjustment Guide**:
- `FeatherWidth = 1f` → Very sharp (almost no transition)
- `FeatherWidth = 2f` → **Recommended** (clean + natural)
- `FeatherWidth = 3f` → Softer transition
- `FeatherWidth = 6f` → Getting too soft
- `FeatherWidth = 12f` → ❌ Spray paint effect

---

## Summary

### What's Fixed:
1. ✅ Alpine now appears in **both** high mountains **AND** cold lowlands
2. ✅ Uses `max()` so either condition works
3. ✅ Lowered threshold (0.55) for easier cold-region Alpine

### What Should Already Work:
- ✅ Voronoi smooth transitions between ALL biomes (via `getBiomeWeights()`)
- ✅ Curved natural boundaries
- ✅ Organic pockets of biomes

### If You Still See Lines:
1. Check config file has `FeatherWidth: 2.0` (not 12.0)
2. Delete old chunks or fly to new terrain
3. Verify biome definitions have distinct climate ranges

### Quick Fix Checklist:
- [ ] Shader updated with new `getBiomeId()` (temperature-based Alpine)
- [ ] `TerrainConfig.cs` has `FeatherWidth = 2f`
- [ ] Delete old chunk files or fly to new area
- [ ] Test in cold lowlands (should see snow!)
- [ ] Test mountain peaks (should see snow!)
- [ ] Test biome boundaries (should see 2-block transitions!)

---

## Technical Details

### Alpine Determination Logic:

```
IF elevation > 230:
    alpineFromAltitude = 100%
ELSE IF elevation > 170:
    alpineFromAltitude = smooth ramp 0% → 100%
ELSE:
    alpineFromAltitude = 0%

IF temperature < 0.15:
    alpineFromCold = 100%
ELSE IF temperature < 0.25:
    alpineFromCold = smooth ramp 100% → 0%
ELSE:
    alpineFromCold = 0%

alpineChance = max(alpineFromAltitude, alpineFromCold) * 70%
             + boundaryNoise * 30%

IF alpineChance > 55%:
    BIOME = ALPINE
```

This means:
- **Tall mountain in jungle** → Alpine (altitude wins)
- **Sea-level arctic** → Alpine (cold wins)
- **Tall mountain in arctic** → Alpine (both conditions)
- **Warm lowlands** → Not Alpine (neither condition)

Perfect! ❄️🏔️

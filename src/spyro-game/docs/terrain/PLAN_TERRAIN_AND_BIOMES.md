# Terrain generation and biomes plan

Status: design document
Owner: terrain/biomes
Scope: `spyro-game` GPU voxel terrain

Goals
- Add natural large-scale structure using continentalness, erosion, ridge/peaks, temperature, humidity fields
- Support spline-mapped height from continentalness, tweakable at runtime
- Keep CPU as parameter hub only; all generation remains on GPU
- Introduce caves, overhangs, arches via 3D density operations
- Classify biomes from temperature/humidity and assign to non-rectangular regions spanning multiple chunks
- Blend biome transitions without visible chunk borders
- Use biomes only for texturing; geology stays encoded in block types
- Keep the system simple to extend with new biomes and parameters

High-level architecture
- CPU configuration bucket: `TerrainConfig` holds all tweakable parameters (noise scales, weights, spline points, biome defs, texture sets).
- GPU parameter mirrors: bind once per frame
  - 1D textures for splines (height vs continentalness and other curves)
  - 2D texture for biome LUT (temperature × humidity ⇒ biome id/weights)
  - SSBO/UBOs for scalar/vector noise params and biome sets
- GPU terrain pipeline (existing): `compute-generate.comp` → `compute-visibility.comp` → `compute-compact.comp` → render
  - Extend `compute-generate.comp` to evaluate macro fields, density, caves, and biome weights per voxel (or defer biome weights to fragment using world position to save memory)
- Rendering: extend `voxel-terrain.frag` to select textures by block-type and blend by biome weights with tri-planar mapping
- Regionization: derive biome regions from low-frequency Worley/Voronoi and/or region grid with jitter; publish a deterministic field from world position so transitions are seamless across chunks

Data model (CPU)
- `TerrainConfig`
  - `Seed`
  - `WorldScale`
  - `Macro` noise params: continentalnessScale, erosionScale, ridgeScale, warpScale, warpStrength
  - `Climate` params: baseTemperature, lapseRate, baseHumidity, coastDrying, climateScale, climateWarp
  - `HeightSpline` (spline points mapping continentalness→height)
  - `ErosionToSlopeSpline` (optional)
  - `BiomeDefinitions[]` small set initially
    - `Id`, `Name`
    - climate tags (comfort ranges for temp/humidity)
    - texture set indices per `BlockLayer` (air/water/surface/subsurface/deep/shore)
  - `BiomeRegionParams`: region cell size (in chunks), jitter strength, max blend neighbors (2–3), feather width
  - `CaveParams`: cheese freq/amp, spaghetti freq/amp, threshold, ridgeCarve, curl noise params
- CPU uploads
  - Build 1D LUT textures for splines (R32F, N samples)
  - Build 2D LUT texture for biome table (R8UI/R16UI id or RGBA8 weights)
  - Pack params into SSBOs with std430 layout

GPU parameter layout suggestions
- Prefer textures for sampled curves/tables, SSBO for scalar arrays. Example GLSL snippets:

```glsl
// Binding set example
layout(binding = 0) uniform sampler1D uHeightSpline;
layout(binding = 1) uniform sampler1D uErosionToSlope;
layout(binding = 2) uniform usampler2D uBiomeLUT;   // temp x humidity → biome id
layout(std430, binding = 3) buffer TerrainParams {
    uint  uSeed;
    float uWorldScale;
    float uMacroScale;
    float uContScale; float uErodeScale; float uRidgeScale;
    float uWarpScale;  float uWarpStrength;
    float uBaseTemp;   float uLapseRate;  float uBaseHum; float uCoastDry;
    float uClimateScale; float uClimateWarp;
    float uRegionCellSize; float uRegionJitter; float uRegionFeather; uint uMaxRegionMix;
    // Cave params
    float uCheeseFreq; float uCheeseAmp; float uSpaghettiFreq; float uSpaghettiAmp;
    float uCaveThreshold; float uCurlScale; float uCurlStrength; float pad0;
};
```

Primary terrain generation (density function)
- Evaluate macro fields at world position p (meters or voxels):
  - Continentalness C = low-freq FBM + domain warp → [-1,1]
  - Erosion E = low-freq FBM (modulates slopes/cliffs)
  - Ridge/peaks R = ridged-multifractal
  - Domain warp W = curl noise or FBM used to offset p for richness
- Height H: sample `uHeightSpline` at remapped continentalness tC = (C*0.5+0.5)
  - Optionally bias by R and E: H += ridgeGain*R - erodeCut*E
- Density D:
  - Start with vertical gradient: D0 = (H - y) / scale
  - Apply 3D shape by blending R/E into D0 for overhangs
  - Apply warping: p' = p + W(p)*uWarpStrength before noise sampling
- Sea level and water: classify water where y < seaLevel and D < 0 near surface

Terrain refinement layers
- Erosion shaping: use `E` to sharpen slopes (ridge lines where E low → cliffs)
- Strata: add banding by `sin(y*strataFreq + strataNoise(p))` → adjusts material assignment only
- Shore shaping: near H ≈ seaLevel, flatten slightly to create beaches
- Peaks and valleys: ridge noise adds vertical relief beyond heightmap for overhangs

Caves and overhangs
- Use 3D density subtraction and ridged fields:
  - Cheese caves: FBM3D(p*freq) - thresh < 0 ⇒ carve
  - Spaghetti caves: use ridged noise or absolute of sin along warped axis → tubes
  - Flow caves: apply 3D curl noise vector field, integrate small steps to get paths; subtract distance to path < r
  - Overhangs: rely on domain-warped ridge noise added to density, not only heightmap
- Final density: D = D0 - Cheese - Spaghetti - Flow
  - Clamp to range and keep gradients smooth to avoid blocky alias

Material classification (existing geology)
- Keep CPU-minimal types: air, water, surface, subsurface, deep, shore
- GPU assigns material from density and y bands and shore detection

Climate fields and biomes
- Temperature T:
  - Start from `uBaseTemp`
  - Subtract altitude lapse: T -= uLapseRate * max(0, y - seaLevel)
  - Add low-freq noise and domain warp
- Humidity Hm:
  - Start from `uBaseHum`
  - Reduce by coast drying proportional to |C - coastValue| (distance from ocean)
  - Add low-freq noise and warp
- Biome id from LUT:
  - Normalize T,Hm to [0,1]
  - `biomeId = texelFetch(uBiomeLUT, ivec2(Ti, Hi), 0)`
- Extensibility: add more biomes by editing the LUT and texture sets only

Biome regionization (chunk grouping without straight lines)
- Define a region cell size in world units (e.g., 4×4 chunks)
- Use Worley/Voronoi cellular noise on p scaled by region size to get K nearest cell centers
- Assign primary biome per cell by hashing cell id → pick from climate-driven candidate biomes at that location
- Compute smooth blend weights between up to K cells using smooth-min or polynomial smoothstep of distances
- Use feather width in voxels/meters to ensure soft transitions
- Deterministic by world position; no per-chunk state required

Transition handling across chunks
- All fields depend only on world coordinates
- Avoid seams by:
  - Shared parameter buffers/textures per frame
  - Region noise evaluated in world space, not chunk-local
  - Biome weights computed on-the-fly in fragment or in compute using world pos

Rendering with biome-driven textures
- Keep block type minimal as material classifier
- In `voxel-terrain.frag`:
  - Derive biome weights from world position (preferred) or read from a compact attribute buffer
  - For the block’s geology layer, get texture indices for top K biomes
  - Sample textures via array/atlas with tri-planar mapping, blend by biome weights
  - Optional biome color tints and AO modulations

Parameters and editing
- Store `TerrainConfig` in a JSON/TOML asset for quick iteration
- Hot reload on file change; rebuild 1D/2D textures and SSBOs
- Expose common params in an in-game UI (ImGui) for rapid tweaking

Integration steps
1) CPU parameter bucket
- Create `TerrainConfig`, `Spline1D`, `BiomeDefinition`, `BiomeTextureSet`, `BiomeRegionParams`, `CaveParams`
- Implement spline sampling and LUT baking to 1D textures
- Implement biome LUT baking to 2D texture and texture array binding for materials

2) GPU bindings
- Add parameter SSBO and sampler bindings to `compute-generate.comp`
- Add samplers to `voxel-terrain.frag` for biome textures and LUTs

3) Macro fields and height
- In `compute-generate.comp`, compute continentalness, erosion, ridge, warp
- Sample `uHeightSpline` to get base height; compute density

4) Caves and overhangs
- Add 3D noises for cheese/spaghetti caves; subtract from density
- Add curl noise for flow caves (optional pass if too heavy)

5) Climate and biome ids
- Compute temperature/humidity fields
- Sample `uBiomeLUT` for base biome id
- Compute regionization via Worley; mix K cell biome ids into weights
- Option A: store compact biome weights per voxel/face
- Option B (preferred): recompute weights in fragment from world position; store nothing

6) Material classification unchanged
- Keep existing geology types; only add climate flags if needed for edge cases

7) Shading
- Extend `voxel-terrain.frag` to fetch biome texture sets by block layer and blend
- Use tri-planar mapping and height-blend for better results

8) Transitions validation
- Fly across region borders; ensure no seams between chunks
- Tune `uRegionFeather` and K to avoid sharp lines

9) Debug/visualization
- Add debug draw modes to color by C/E/R/T/Hm/biome id/weights
- Add wireframe region cells visualization

10) Performance
- Keep macro fields low-frequency; compute once per voxel in generate pass
- Prefer option B biome evaluation in fragment to reduce memory bandwidth
- Use fast hash/cell noise; limit K to 2–3 neighbors

Milestones
- M1: Parameter bucket + GPU bindings + height spline only
- M2: Macro fields shaping + domain warp
- M3: Caves (cheese + spaghetti)
- M4: Climate fields + biome LUT + regionization (fragment-based weights)
- M5: Shading with biome texture sets and blending
- M6: Polish transitions, add debug UI, tune performance

Biome Debug Colors (F3)
| ID | Biome | Color (RGB) | Description |
| :--- | :--- | :--- | :--- |
| **0** | Ocean | `(0.0, 0.0, 1.0)` | Blue |
| **1** | Beach | `(1.0, 1.0, 0.0)` | Yellow |
| **2** | Plains | `(0.0, 1.0, 0.0)` | Green |
| **3** | Savanna | `(1.0, 0.5, 0.0)` | Orange |
| **4** | Desert | `(1.0, 0.0, 0.0)` | Red |
| **5** | Rainforest | `(0.0, 0.5, 0.0)` | Dark Green |
| **6** | Taiga | `(0.0, 1.0, 1.0)` | Cyan |
| **7** | Tundra | `(1.0, 1.0, 1.0)` | White |
| **8** | Highlands | `(0.5, 0.5, 0.5)` | Grey |
| **9** | Alpine | `(0.5, 0.0, 0.5)` | Purple |

Appendix: minimal GLSL for continentalness→height
```glsl
float continentalness(vec3 p) {
    vec3 pw = p + fbm(p * uWarpScale) * uWarpStrength;
    return fbm(pw * uContScale); // [-1,1]
}

float baseHeight(vec3 p) {
    float C = continentalness(p) * 0.5 + 0.5; // [0,1]
    float H = texture(uHeightSpline, C).r;     // meters
    return H;
}

float density(vec3 p) {
    float H = baseHeight(p);
    float rid = ridged(p * uRidgeScale);
    float ero = fbm(p * uErodeScale);
    float d0 = (H - p.y) / 4.0;
    d0 += rid * 0.6 - ero * 0.4; // overhangs + erosion
    // caves
    float cheese = fbm(p * uCheeseFreq) * uCheeseAmp;
    float spag   = (1.0 - abs(sin(p.x*uSpaghettiFreq) * sin(p.y*uSpaghettiFreq))) * uSpaghettiAmp;
    return d0 - smoothstep(uCaveThreshold-0.05, uCaveThreshold+0.05, cheese+spag);
}
```

Appendix: biome selection in fragment (preferred)
```glsl
vec3 biomeWeights(vec3 worldPos){
    // Worley regions
    RegionMix mix = regionMix(worldPos, uRegionCellSize, uRegionJitter, uMaxRegionMix, uRegionFeather);
    // Climate-based base biome
    float t = computeTemperature(worldPos);
    float h = computeHumidity(worldPos);
    uint baseId = texelFetch(uBiomeLUT, ivec2(t*255.0, h*255.0), 0).r;
    // Map region cells to specific biomes around the base one using hash
    return blendCellBiomes(mix, baseId);
}
```

Notes
- Keep hash functions integer-based for determinism
- Use world coordinates everywhere; never depend on chunk-local coords for noise
- Prefer bindless or texture arrays to keep state changes low

using static SpyroGame.Server.World.Generation.NoiseUtilities;
using SpyroGame.Server.World;
using SpyroGame.World;

namespace SpyroGame.Server.World.Generation;

/// <summary>
/// Stage 5: Cave Carving System
/// 
/// Architecture: Carvers MODIFY existing terrain - they don't generate it.
/// The solid terrain must exist first (from Stage 2-4), then caves are carved through it.
/// 
/// Cave Types:
/// 1. Cheese caves: Large irregular chambers using 3D FBM noise
/// 2. Spaghetti caves: Long winding tunnels using distance-from-2-noise-fields
/// 
/// Cave Entrances (Global System):
/// - Entrances are generated as global structures independent of chunk boundaries
/// - A "Global Entrance" is a tunnel connecting a cave to the surface
/// - Each chunk carves the portion of the tunnel that intersects it
/// - This ensures seamless cross-chunk entrances without vertical shafts
/// </summary>
/// <param name="config">Terrain configuration.</param>
internal sealed class CaveCarver(TerrainConfig config)
{
    // Sparse 3D sampling constants (Minecraft-style optimization)
    private const int SparseStep = 4;
    private const int SparseSamplesXZ = VoxelHelper.ChunkSideSize / SparseStep + 1; // 5 samples
    private const int SparseSamplesY = VoxelHelper.ChunkYSize / SparseStep + 1;      // 97 samples
    private const int SparseSampleCount = SparseSamplesXZ * SparseSamplesXZ;         // 25 per Y slice
    private const int SparseVolumeSize = SparseSampleCount * SparseSamplesY;         // 2425 total
    private const int ColumnCount = VoxelHelper.ChunkSideSizeSquare;
    private const int ColumnHeightWords = ColumnCount * VoxelHelper.ChunkYSize;

    // Configuration
    private readonly TerrainConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private TerrainConfig.TerrainGenerationParams _params = config.GetGenerationParams();

    // Sparse sampling buffers (reused per chunk)
    private readonly float[] _sparseSampleX = new float[SparseSampleCount];
    private readonly float[] _sparseSampleZ = new float[SparseSampleCount];
    private readonly float[] _sparseCheeseGrid = new float[SparseVolumeSize];
    private readonly float[] _sparseSpaghettiA = new float[SparseVolumeSize];
    private readonly float[] _sparseSpaghettiB = new float[SparseVolumeSize];
    private readonly float[] _sparseSliceScratch = new float[SparseSampleCount];

    // Full-resolution cave volumes (interpolated from sparse)
    private readonly float[] _cheeseVolume = new float[ColumnHeightWords];
    private readonly float[] _spaghettiVolume = new float[ColumnHeightWords];

    // Output cave mask: 1 = carved (air), 0 = solid
    private readonly byte[] _caveMask = new byte[ColumnHeightWords];

    /// <summary>Gets the cave mask for the current chunk.</summary>
    public ReadOnlySpan<byte> CaveMask => _caveMask;

    /// <summary>
    /// Update configuration when terrain config changes.
    /// </summary>
    public void UpdateConfig(TerrainConfig config) => _params = config.GetGenerationParams();

    /// <summary>
    /// Carve caves for a chunk. This is the main entry point.
    /// 
    /// Process:
    /// 1. Sample sparse 3D noise for cheese and spaghetti caves
    /// 2. Interpolate to full resolution
    /// 3. Build base cave mask with depth attenuation
    /// 4. Process Global Entrances (Cross-Chunk)
    /// </summary>
    /// <param name="chunkX">Chunk X coordinate.</param>
    /// <param name="chunkZ">Chunk Z coordinate.</param>
    /// <param name="columnHeights">Per-column terrain heights (256 values).</param>
    /// <param name="isLandColumn">Per-column land/ocean flag (256 values).</param>
    /// <param name="globalHeightProvider">Function to get terrain height at any global (x, z).</param>
    public void CarveChunk(
        int chunkX,
        int chunkZ,
        ReadOnlySpan<int> columnHeights,
        ReadOnlySpan<bool> isLandColumn,
        Func<int, int, float> globalHeightProvider)
    {
        // Clear previous data
        Array.Clear(_caveMask);

        // Step 1-2: Sample and interpolate 3D cave noise
        SampleSparse3DNoise(chunkX, chunkZ);
        InterpolateSparseToFull();

        // Step 3: Build base cave mask (no surface breaches)
        BuildBaseCaveMask(columnHeights, isLandColumn);

        // Step 4: Process Global Entrances (Cross-Chunk)
        ProcessGlobalEntrances(chunkX, chunkZ, globalHeightProvider);
    }

    /// <summary>
    /// Get the cave mask value for a specific voxel.
    /// </summary>
    public bool IsCarved(int lx, int y, int lz)
    {
        var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;
        var voxelIndex = columnIndex * VoxelHelper.ChunkYSize + y;
        return _caveMask[voxelIndex] != 0;
    }

    /// <summary>
    /// Get a span of the cave mask for a single column.
    /// </summary>
    public Span<byte> GetColumnMask(int columnIndex)
        => _caveMask.AsSpan(columnIndex * VoxelHelper.ChunkYSize, VoxelHelper.ChunkYSize);

    #region Sparse 3D Noise Sampling

    /// <summary>
    /// Sample sparse 3D noise grids for cheese and spaghetti caves.
    /// Uses Minecraft-style optimization: sample every 4 blocks and interpolate.
    /// </summary>
    private void SampleSparse3DNoise(int chunkX, int chunkZ)
    {
        var baseX = chunkX * VoxelHelper.ChunkSideSize;
        var baseZ = chunkZ * VoxelHelper.ChunkSideSize;
        var yStretch = _config.Caves.SpaghettiYStretch;

        // Build sparse sample coordinates (5x5 grid at 4-block intervals)
        var sparseIdx = 0;
        for (var sz = 0; sz < SparseSamplesXZ; sz++)
        {
            var worldZ = baseZ + sz * SparseStep;
            for (var sx = 0; sx < SparseSamplesXZ; sx++)
            {
                _sparseSampleX[sparseIdx] = baseX + sx * SparseStep;
                _sparseSampleZ[sparseIdx] = worldZ;
                sparseIdx++;
            }
        }

        // Sample each Y layer
        for (var sy = 0; sy < SparseSamplesY; sy++)
        {
            var worldY = sy * SparseStep;
            var sliceOffset = sy * SparseSampleCount;

            // Y-stretch for horizontal bias in spaghetti caves
            var stretchedY = worldY * yStretch;

            // Cheese caves: large chambers (no Y-stretch)
            SampleNoiseSlice(
                _sparseSliceScratch,
                _sparseSampleX.AsSpan(),
                worldY,
                _sparseSampleZ.AsSpan(),
                _params.CheeseFrequency,
                _params.Seed + 300u,
                octaves: 2, persistence: 0.6f, lacunarity: 1.9f);
            _sparseSliceScratch.AsSpan().CopyTo(_sparseCheeseGrid.AsSpan(sliceOffset, SparseSampleCount));

            // Spaghetti caves: tunnels (with Y-stretch for horizontal bias)
            SampleNoiseSlice(
                _sparseSliceScratch,
                _sparseSampleX.AsSpan(),
                stretchedY,
                _sparseSampleZ.AsSpan(),
                _params.SpaghettiFrequency,
                _params.Seed + 400u,
                octaves: 1, persistence: 1f, lacunarity: 2f);
            _sparseSliceScratch.AsSpan().CopyTo(_sparseSpaghettiA.AsSpan(sliceOffset, SparseSampleCount));

            SampleNoiseSlice(
                _sparseSliceScratch,
                _sparseSampleX.AsSpan(),
                stretchedY,
                _sparseSampleZ.AsSpan(),
                _params.SpaghettiFrequency,
                _params.Seed + 500u,
                octaves: 1, persistence: 1f, lacunarity: 2f);
            _sparseSliceScratch.AsSpan().CopyTo(_sparseSpaghettiB.AsSpan(sliceOffset, SparseSampleCount));
        }
    }

    /// <summary>
    /// Sample 3D value noise at sparse grid points for one Y slice.
    /// </summary>
    private static void SampleNoiseSlice(
        Span<float> destination,
        ReadOnlySpan<float> xCoords,
        float yCoord,
        ReadOnlySpan<float> zCoords,
        float baseFrequency,
        uint seed,
        int octaves,
        float persistence,
        float lacunarity)
    {
        var count = destination.Length;
        for (var i = 0; i < count; i++)
        {
            var amplitude = 1f;
            var frequency = baseFrequency;
            var accum = 0f;
            var totalAmp = 0f;

            for (var octave = 0; octave < octaves; octave++)
            {
                var sample = ValueNoise3D(
                    xCoords[i] * frequency,
                    yCoord * frequency,
                    zCoords[i] * frequency,
                    seed + (uint)(octave * 1013));
                accum += sample * amplitude;
                totalAmp += amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            destination[i] = totalAmp > 0f ? accum / totalAmp : 0f;
        }
    }

    #endregion

    #region Interpolation

    /// <summary>
    /// Trilinear interpolate sparse 3D samples into full-resolution volumes.
    /// </summary>
    private void InterpolateSparseToFull()
    {
        var chunkY = VoxelHelper.ChunkYSize;

        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            var sz0 = lz / SparseStep;
            var sz1 = Math.Min(sz0 + 1, SparseSamplesXZ - 1);
            var tz = (lz % SparseStep) / (float)SparseStep;

            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;
                var sx0 = lx / SparseStep;
                var sx1 = Math.Min(sx0 + 1, SparseSamplesXZ - 1);
                var tx = (lx % SparseStep) / (float)SparseStep;

                // Precompute XZ corner indices
                var idx00 = sz0 * SparseSamplesXZ + sx0;
                var idx10 = sz0 * SparseSamplesXZ + sx1;
                var idx01 = sz1 * SparseSamplesXZ + sx0;
                var idx11 = sz1 * SparseSamplesXZ + sx1;

                for (var sy = 0; sy < SparseSamplesY - 1; sy++)
                {
                    var yOffset0 = sy * SparseSampleCount;
                    var yOffset1 = (sy + 1) * SparseSampleCount;

                    // Bilinear sample at both Y levels
                    var cheese0 = BilinearSample(_sparseCheeseGrid, yOffset0, idx00, idx10, idx01, idx11, tx, tz);
                    var cheese1 = BilinearSample(_sparseCheeseGrid, yOffset1, idx00, idx10, idx01, idx11, tx, tz);

                    var spagA0 = BilinearSample(_sparseSpaghettiA, yOffset0, idx00, idx10, idx01, idx11, tx, tz);
                    var spagA1 = BilinearSample(_sparseSpaghettiA, yOffset1, idx00, idx10, idx01, idx11, tx, tz);

                    var spagB0 = BilinearSample(_sparseSpaghettiB, yOffset0, idx00, idx10, idx01, idx11, tx, tz);
                    var spagB1 = BilinearSample(_sparseSpaghettiB, yOffset1, idx00, idx10, idx01, idx11, tx, tz);

                    // Interpolate Y for each voxel in this segment
                    var yBase = sy * SparseStep;
                    for (var dy = 0; dy < SparseStep; dy++)
                    {
                        var y = yBase + dy;
                        if (y >= chunkY) break;

                        var ty = dy / (float)SparseStep;
                        var voxelIndex = columnIndex * chunkY + y;

                        // Cheese caves: direct amplitude
                        var cheeseSample = (Lerp(cheese0, cheese1, ty) * 2f - 1f) * _params.CheeseAmplitude;
                        _cheeseVolume[voxelIndex] = Math.Clamp(cheeseSample, -1f, 1f);

                        // Spaghetti caves: tunnel width based on distance from two noise fields
                        var n1 = Lerp(spagA0, spagA1, ty) * 2f - 1f;
                        var n2 = Lerp(spagB0, spagB1, ty) * 2f - 1f;
                        var dist = MathF.Sqrt(n1 * n1 + n2 * n2);

                        // Width factor: higher amplitude = wider tunnels
                        var amp = Math.Clamp(_params.SpaghettiAmplitude, 0.2f, 4f);
                        var ampT = (amp - 0.2f) / 3.8f;
                        var widthFactor = Lerp(2.8f, 1.1f, ampT);
                        var tunnelWidth = 1f - dist * widthFactor;
                        _spaghettiVolume[voxelIndex] = Math.Clamp(tunnelWidth, -1f, 1f);
                    }
                }
            }
        }
    }

    #endregion

    #region Base Cave Mask

    /// <summary>
    /// Build the base cave mask from noise volumes.
    /// Applies depth attenuation to prevent surface breaches.
    /// </summary>
    private void BuildBaseCaveMask(ReadOnlySpan<int> columnHeights, ReadOnlySpan<bool> isLandColumn)
    {
        var minCaveDepth = Math.Max(4, _config.Caves.MinBreachCaveDepth);
        var depthFade = _params.CaveDepthFade;
        var carveThreshold = _params.CaveCarveThreshold;

        for (var columnIndex = 0; columnIndex < ColumnCount; columnIndex++)
        {
            var mask = GetColumnMask(columnIndex);

            // Skip ocean columns
            if (!isLandColumn[columnIndex])
            {
                mask.Clear();
                continue;
            }

            var surfaceHeight = columnHeights[columnIndex];
            var cheeseSlice = _cheeseVolume.AsSpan(columnIndex * VoxelHelper.ChunkYSize, VoxelHelper.ChunkYSize);
            var spaghettiSlice = _spaghettiVolume.AsSpan(columnIndex * VoxelHelper.ChunkYSize, VoxelHelper.ChunkYSize);

            for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
            {
                var depth = surfaceHeight - y;

                // Don't carve near surface (this prevents accidental breaches)
                if (depth < minCaveDepth)
                {
                    continue;
                }

                // Combine cheese and spaghetti caves
                var combinedDensity = MathF.Max(cheeseSlice[y], spaghettiSlice[y]);

                // Depth attenuation: caves fade out near surface
                var depthAtten = Smoothstep(minCaveDepth, minCaveDepth + depthFade, depth);

                if (combinedDensity * depthAtten > carveThreshold)
                {
                    mask[y] = 1;
                }
            }
        }
    }

    #endregion

    #region Global Entrance System

    /// <summary>
    /// Process global entrances that might intersect this chunk.
    /// Checks a 3x3 region grid around the current chunk.
    /// </summary>
    private void ProcessGlobalEntrances(int chunkX, int chunkZ, Func<int, int, float> heightProvider)
    {
        // Region size = 32 blocks (2x2 chunks)
        const int RegionSize = 32;

        var worldX = chunkX * VoxelHelper.ChunkSideSize;
        var worldZ = chunkZ * VoxelHelper.ChunkSideSize;

        // Calculate region coordinates
        var regionX = (int)MathF.Floor(worldX / (float)RegionSize);
        var regionZ = (int)MathF.Floor(worldZ / (float)RegionSize);

        // Check 3x3 regions to catch entrances that might cross into this chunk
        for (var rz = regionZ - 1; rz <= regionZ + 1; rz++)
        {
            for (var rx = regionX - 1; rx <= regionX + 1; rx++)
            {
                var entrance = GenerateGlobalEntrance(rx, rz, RegionSize, heightProvider);
                if (entrance.HasValue)
                {
                    CarveGlobalTunnel(entrance.Value, chunkX, chunkZ);
                }
            }
        }
    }

    /// <summary>
    /// Deterministically generate an entrance definition for a region.
    /// Returns null if no entrance exists in this region.
    /// </summary>
    private GlobalEntrance? GenerateGlobalEntrance(int rx, int rz, int regionSize, Func<int, int, float> heightProvider)
    {
        var seed = _params.Seed + 9000u;
        var rHash = Hash2D(rx, rz, seed);

        // 20% chance per region
        if (rHash > 0.2f) return null;

        // Pick random spot in region
        var rRand = Hash2D(rx, rz, seed + 1);
        var wx = rx * regionSize + (int)(rRand * regionSize);
        var wz = rz * regionSize + (int)(Hash2D(rx, rz, seed + 2) * regionSize);

        // Get surface height (Global Lookup)
        var surfaceY = heightProvider(wx, wz);

        // Find cave below
        // Scan down from surface to find a cave ceiling
        var caveY = -1;
        var foundCeiling = false;

        // Scan down from surface
        for (var y = (int)surfaceY - 5; y > 10; y--)
        {
            var density = GetGlobalCaveDensity(wx, y, wz);

            // Density > Threshold means AIR (Carved)
            // We use a slightly lower threshold for detection to be safe
            if (density > _params.CaveCarveThreshold)
            {
                if (!foundCeiling)
                {
                    foundCeiling = true; // Hit ceiling
                }
            }
            else // Solid
            {
                if (foundCeiling)
                {
                    // Hit floor (transition from Air to Solid)
                    caveY = y + 1;
                    break;
                }
            }
        }

        if (caveY == -1) return null;

        // Found a cave! Now find an exit.
        // Pick random direction
        var angle = Hash2D(rx, rz, seed + 3) * MathF.PI * 2;
        var dirX = MathF.Cos(angle);
        var dirZ = MathF.Sin(angle);

        // Scan ahead for exit
        float exitDist = -1;
        float exitSlope = 0;

        // Scan up to 80 blocks
        for (float d = 5; d < 80; d += 2)
        {
            var tx = wx + dirX * d;
            var tz = wz + dirZ * d;
            var th = heightProvider((int)tx, (int)tz);

            var rise = th - caveY;
            var slope = rise / d;

            // Valid walkable slope
            if (slope is > 0.2f and < 0.8f)
            {
                exitDist = d;
                exitSlope = slope;
                break;
            }
        }

        return exitDist < 0
            ? null
            : new GlobalEntrance
            {
                StartX = wx,
                StartY = caveY,
                StartZ = wz,
                EndX = wx + dirX * exitDist,
                EndY = caveY + exitSlope * exitDist,
                EndZ = wz + dirZ * exitDist,
                RadiusH = 2.5f,
                RadiusV = 3.5f
            };
    }

    /// <summary>
    /// Carve the portion of a global tunnel that intersects the current chunk.
    /// </summary>
    private void CarveGlobalTunnel(GlobalEntrance e, int chunkX, int chunkZ)
    {
        // Carve segment from Start to End
        var dx = e.EndX - e.StartX;
        var dy = e.EndY - e.StartY;
        var dz = e.EndZ - e.StartZ;
        var len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        var stepSize = 0.5f;
        var steps = (int)(len / stepSize);

        var dirX = dx / len;
        var dirY = dy / len;
        var dirZ = dz / len;

        var chunkWX = chunkX * VoxelHelper.ChunkSideSize;
        var chunkWZ = chunkZ * VoxelHelper.ChunkSideSize;

        var px = e.StartX;
        var py = e.StartY;
        var pz = e.StartZ;

        var rH = e.RadiusH;
        var rV = e.RadiusV;

        // Carve path + extra for exit clearing
        for (var i = 0; i <= steps + 12; i++)
        {
            // Check if point is in current chunk (with radius buffer)
            // We use a generous buffer to ensure smooth carving across borders
            if (px >= chunkWX - rH - 1 && px <= chunkWX + 16 + rH + 1 &&
                pz >= chunkWZ - rH - 1 && pz <= chunkWZ + 16 + rH + 1)
            {
                // Convert to local coords
                var lx = px - chunkWX;
                var lz = pz - chunkWZ;

                // Carve spheroid (ignoring terrain height check to force the tunnel)
                CarveSpheroid(lx, py + rV, lz, rH, rV, default);
            }

            px += dirX * stepSize;
            py += dirY * stepSize;
            pz += dirZ * stepSize;

            // Widen near end (last 15 steps)
            if (i > steps - 15)
            {
                rH += 0.05f;
                rV += 0.03f;
            }
        }
    }

    /// <summary>
    /// Calculate cave density at a specific global point.
    /// Replicates the logic of SampleSparse3DNoise but for a single point.
    /// </summary>
    private float GetGlobalCaveDensity(float x, float y, float z)
    {
        // Cheese
        var cheese = 0f;
        var amp = 1f;
        var freq = _params.CheeseFrequency;
        var totalAmp = 0f;
        for (var i = 0; i < 2; i++)
        {
            cheese += ValueNoise3D(x * freq, y * freq, z * freq, _params.Seed + 300u + (uint)(i * 1013)) * amp;
            totalAmp += amp;
            amp *= 0.6f;
            freq *= 1.9f;
        }
        cheese = (cheese / totalAmp) * 2f - 1f;
        cheese *= _params.CheeseAmplitude;

        // Spaghetti
        var yStretch = _config.Caves.SpaghettiYStretch;
        var sy = y * yStretch;

        var spagA = ValueNoise3D(x * _params.SpaghettiFrequency, sy * _params.SpaghettiFrequency, z * _params.SpaghettiFrequency, _params.Seed + 400u);
        var spagB = ValueNoise3D(x * _params.SpaghettiFrequency, sy * _params.SpaghettiFrequency, z * _params.SpaghettiFrequency, _params.Seed + 500u);

        var n1 = spagA * 2f - 1f;
        var n2 = spagB * 2f - 1f;
        var dist = MathF.Sqrt(n1 * n1 + n2 * n2);

        var sAmp = Math.Clamp(_params.SpaghettiAmplitude, 0.2f, 4f);
        var ampT = (sAmp - 0.2f) / 3.8f;
        var widthFactor = Lerp(2.8f, 1.1f, ampT);
        var tunnelWidth = 1f - dist * widthFactor;

        return MathF.Max(cheese, tunnelWidth);
    }

    /// <summary>
    /// Carve a spheroid into the cave mask.
    /// </summary>
    private void CarveSpheroid(float cx, float cy, float cz, float rH, float rV, ReadOnlySpan<int> columnHeights)
    {
        var minX = Math.Max(0, (int)(cx - rH - 1));
        var maxX = Math.Min(VoxelHelper.ChunkSideSize - 1, (int)(cx + rH + 1));
        var minZ = Math.Max(0, (int)(cz - rH - 1));
        var maxZ = Math.Min(VoxelHelper.ChunkSideSize - 1, (int)(cz + rH + 1));
        var minY = Math.Max(0, (int)(cy - rV - 1));
        var maxY = Math.Min(VoxelHelper.ChunkYSize - 1, (int)(cy + rV + 1));

        var rHSq = rH * rH;
        var rVSq = rV * rV;

        var checkHeights = !columnHeights.IsEmpty;

        for (var lz = minZ; lz <= maxZ; lz++)
        {
            var dz = lz + 0.5f - cz;
            var dzSq = (dz * dz) / rHSq;

            for (var lx = minX; lx <= maxX; lx++)
            {
                var dx = lx + 0.5f - cx;
                var dxSq = (dx * dx) / rHSq;
                if (dxSq + dzSq > 1f) continue;

                var colIdx = lz * VoxelHelper.ChunkSideSize + lx;
                var mask = GetColumnMask(colIdx);

                // Optimization: if checking heights, get surface once
                var surface = checkHeights ? columnHeights[colIdx] : 0;

                for (var y = minY; y <= maxY; y++)
                {
                    if (checkHeights && y > surface) continue; // Don't carve above terrain

                    var dy = y + 0.5f - cy;
                    var dySq = (dy * dy) / rVSq;

                    if (dxSq + dySq + dzSq <= 1f)
                        mask[y] = 1;
                }
            }
        }
    }

    private struct GlobalEntrance
    {
        public float StartX, StartY, StartZ;
        public float EndX, EndY, EndZ;
        public float RadiusH, RadiusV;
    }

    #endregion
}


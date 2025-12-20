using SpyroGame.World.Registry;

namespace SpyroGame.World;

/// <summary>
/// Stores biome and climate data for a chunk using both:
/// - Per-column biome IDs (256 columns per chunk) for accurate voxel-resolution borders
/// - Coarse 4x4 grid (16 cells) for climate interpolation (legacy compatibility)
/// Also stores 3D cave biome data using a 4×4×24 grid for underground biome variation.
/// </summary>
public sealed class ChunkBiomeData
{
    /// <summary>
    /// Number of columns per chunk (16×16 = 256).
    /// </summary>
    public const int ColumnCount = VoxelHelper.ChunkSideSizeSquare; // 256
    
    /// <summary>
    /// Grid resolution: 4x4 cells per chunk (each cell = 4x4 blocks).
    /// Used for climate interpolation, NOT for biome lookup.
    /// </summary>
    public const int GridSize = 4;
    public const int CellCount = GridSize * GridSize; // 16
    public const int BlocksPerCell = VoxelHelper.ChunkSideSize / GridSize; // 4
    
    /// <summary>
    /// Y-axis grid resolution for 3D cave biomes.
    /// Minecraft uses 24 Y-cells (16 blocks each = 384 total height).
    /// </summary>
    public const int GridSizeY = VoxelHelper.ChunkYSize / BlocksPerCellY; // 24 (384 / 16)
    public const int BlocksPerCellY = 16;
    public const int CaveBiomeCellCount = GridSize * GridSize * GridSizeY; // 4×4×24 = 384
    
    /// <summary>
    /// Per-column biome ID for voxel-resolution borders.
    /// Index = localZ * 16 + localX where localX, localZ are [0,15].
    /// This is the PRIMARY biome storage used for rendering.
    /// Initialized to Plains (not Ocean=0) to prevent spurious ocean biomes on uninitialized columns.
    /// </summary>
    public readonly BiomeId[] ColumnBiomes;
    
    /// <summary>
    /// Creates a new ChunkBiomeData with all columns initialized to Plains.
    /// </summary>
    public ChunkBiomeData()
    {
        ColumnBiomes = new BiomeId[ColumnCount];
        // Initialize to Plains (the fallback biome) instead of default Ocean (0)
        Array.Fill(ColumnBiomes, BiomeId.Plains);
    }
    
    /// <summary>
    /// Biome ID for each cell in the 4x4 grid (legacy, for climate interpolation).
    /// Index = z * GridSize + x where x,z are cell coordinates [0,3].
    /// </summary>
    public readonly BiomeId[] BiomeIds = new BiomeId[CellCount];
    
    /// <summary>
    /// Cave biome ID for each cell in the 4×4×24 3D grid.
    /// Index = y * (GridSize * GridSize) + z * GridSize + x.
    /// </summary>
    public readonly CaveBiomeId[] CaveBiomeIds = new CaveBiomeId[CaveBiomeCellCount];
    
    /// <summary>
    /// Temperature values [0,1] for each cell. 0=frozen, 1=hot.
    /// </summary>
    public readonly float[] Temperature = new float[CellCount];
    
    /// <summary>
    /// Humidity values [0,1] for each cell. 0=arid, 1=wet.
    /// </summary>
    public readonly float[] Humidity = new float[CellCount];
    
    /// <summary>
    /// Continentalness values [-1,1] for each cell. -1=ocean, +1=inland.
    /// </summary>
    public readonly float[] Continentalness = new float[CellCount];
    
    /// <summary>
    /// Erosion values [-1,1] for each cell. High erosion = flat terrain.
    /// </summary>
    public readonly float[] Erosion = new float[CellCount];
    
    /// <summary>
    /// Peaks/Valleys (PV) values [-1,1] for each cell. Controls height variation.
    /// </summary>
    public readonly float[] PeaksValleys = new float[CellCount];
    
    /// <summary>
    /// Weirdness values [-1,1] for each cell. Creates unusual terrain transitions.
    /// </summary>
    public readonly float[] Weirdness = new float[CellCount];
    
    /// <summary>
    /// Get the biome at a specific block position within the chunk.
    /// Uses per-column biome storage for voxel-resolution borders.
    /// </summary>
    public BiomeId GetBiomeAt(int localX, int localZ)
    {
        // Use per-column storage for accurate borders
        localX = Math.Clamp(localX, 0, VoxelHelper.ChunkSideSize - 1);
        localZ = Math.Clamp(localZ, 0, VoxelHelper.ChunkSideSize - 1);
        return ColumnBiomes[localZ * VoxelHelper.ChunkSideSize + localX];
    }
    
    /// <summary>
    /// Set the biome for a specific column.
    /// </summary>
    public void SetBiomeAt(int localX, int localZ, BiomeId biome)
    {
        localX = Math.Clamp(localX, 0, VoxelHelper.ChunkSideSize - 1);
        localZ = Math.Clamp(localZ, 0, VoxelHelper.ChunkSideSize - 1);
        ColumnBiomes[localZ * VoxelHelper.ChunkSideSize + localX] = biome;
    }
    
    /// <summary>
    /// Get the biome from the 4x4 grid cell (legacy, for climate interpolation).
    /// </summary>
    public BiomeId GetCellBiome(int cellX, int cellZ)
    {
        cellX = Math.Clamp(cellX, 0, GridSize - 1);
        cellZ = Math.Clamp(cellZ, 0, GridSize - 1);
        return BiomeIds[cellZ * GridSize + cellX];
    }
    
    /// <summary>
    /// Get the cave biome at a specific 3D block position within the chunk.
    /// Used for underground biome variation (lush caves, dripstone caves, etc.).
    /// </summary>
    public CaveBiomeId GetCaveBiomeAt(int localX, int localY, int localZ)
    {
        var cellX = localX / BlocksPerCell;
        var cellY = localY / BlocksPerCellY;
        var cellZ = localZ / BlocksPerCell;
        
        // Clamp to valid range
        cellX = Math.Clamp(cellX, 0, GridSize - 1);
        cellY = Math.Clamp(cellY, 0, GridSizeY - 1);
        cellZ = Math.Clamp(cellZ, 0, GridSize - 1);
        
        return CaveBiomeIds[GetCaveBiomeCellIndex(cellX, cellY, cellZ)];
    }
    
    /// <summary>
    /// Set the cave biome at a specific cell position in the 3D grid.
    /// </summary>
    public void SetCaveBiomeAt(int cellX, int cellY, int cellZ, CaveBiomeId biome)
    {
        var index = GetCaveBiomeCellIndex(cellX, cellY, cellZ);
        CaveBiomeIds[index] = biome;
    }
    
    /// <summary>
    /// Get the 3D cell index for cave biomes.
    /// Index = cellY * (GridSize * GridSize) + cellZ * GridSize + cellX
    /// </summary>
    public static int GetCaveBiomeCellIndex(int cellX, int cellY, int cellZ)
    {
        return cellY * (GridSize * GridSize) + cellZ * GridSize + cellX;
    }
    
    /// <summary>
    /// Get cell coordinates from a 3D cave biome cell index.
    /// </summary>
    public static (int cellX, int cellY, int cellZ) GetCaveBiomeCellCoords(int cellIndex)
    {
        var cellY = cellIndex / (GridSize * GridSize);
        var remainder = cellIndex % (GridSize * GridSize);
        var cellZ = remainder / GridSize;
        var cellX = remainder % GridSize;
        return (cellX, cellY, cellZ);
    }
    
    /// <summary>
    /// Get interpolated temperature at a specific block position.
    /// </summary>
    public float GetTemperatureAt(int localX, int localZ)
    {
        return GetInterpolatedValue(Temperature, localX, localZ);
    }
    
    /// <summary>
    /// Get interpolated humidity at a specific block position.
    /// </summary>
    public float GetHumidityAt(int localX, int localZ)
    {
        return GetInterpolatedValue(Humidity, localX, localZ);
    }
    
    /// <summary>
    /// Get all interpolated climate values at a specific block position.
    /// Returns: (continentalness, temperature, humidity, erosion, peaksValleys)
    /// </summary>
    public (float continentalness, float temperature, float humidity, float erosion, float peaksValleys) 
        GetInterpolatedClimate(int localX, int localZ)
    {
        return (
            GetInterpolatedValue(Continentalness, localX, localZ),
            GetInterpolatedValue(Temperature, localX, localZ),
            GetInterpolatedValue(Humidity, localX, localZ),
            GetInterpolatedValue(Erosion, localX, localZ),
            GetInterpolatedValue(PeaksValleys, localX, localZ)
        );
    }
    
    /// <summary>
    /// Get the cell index for a local block position.
    /// </summary>
    public static int GetCellIndex(int localX, int localZ)
    {
        var cellX = localX / BlocksPerCell;
        var cellZ = localZ / BlocksPerCell;
        return cellZ * GridSize + cellX;
    }
    
    /// <summary>
    /// Get cell coordinates from cell index.
    /// </summary>
    public static (int cellX, int cellZ) GetCellCoords(int cellIndex)
    {
        return (cellIndex % GridSize, cellIndex / GridSize);
    }
    
    /// <summary>
    /// Get raw (non-interpolated) climate data for a specific cell.
    /// </summary>
    public (float C, float T, float H, float E, float PV, float W) GetCellClimate(int cellX, int cellZ)
    {
        var idx = cellZ * GridSize + cellX;
        return (Continentalness[idx], Temperature[idx], Humidity[idx], Erosion[idx], PeaksValleys[idx], Weirdness[idx]);
    }
    
    /// <summary>
    /// Get biome blend weights using Worley-style regionization.
    /// Returns the K nearest biomes and their blend weights for smooth transitions.
    /// Weights are computed based on distance to cell centers (Voronoi-like).
    /// </summary>
    /// <param name="localX">Local X coordinate within chunk [0,15].</param>
    /// <param name="localZ">Local Z coordinate within chunk [0,15].</param>
    /// <param name="k">Number of neighbor biomes to return (2 or 3 recommended).</param>
    /// <returns>Array of (biomeId, weight) tuples, sorted by weight descending. Weights sum to 1.</returns>
    public (BiomeId biome, float weight)[] GetBiomeBlendWeights(int localX, int localZ, int k = 3)
    {
        // Position within the grid (fractional cell coordinates)
        var fx = (localX + 0.5f) / BlocksPerCell;
        var fz = (localZ + 0.5f) / BlocksPerCell;
        
        // Collect distances to all cell centers and their biomes
        var cellData = new (float distance, BiomeId biome, int cellX, int cellZ)[CellCount];
        
        for (var cellZ = 0; cellZ < GridSize; cellZ++)
        {
            for (var cellX = 0; cellX < GridSize; cellX++)
            {
                var cellIndex = cellZ * GridSize + cellX;
                
                // Cell center position
                var centerX = cellX + 0.5f;
                var centerZ = cellZ + 0.5f;
                
                // Distance from query point to cell center
                var dx = fx - centerX;
                var dz = fz - centerZ;
                var dist = MathF.Sqrt(dx * dx + dz * dz);
                
                cellData[cellIndex] = (dist, BiomeIds[cellIndex], cellX, cellZ);
            }
        }
        
        // Sort by distance to find K nearest
        Array.Sort(cellData, (a, b) => a.distance.CompareTo(b.distance));
        
        // Take K nearest unique biomes
        var results = new List<(BiomeId biome, float weight)>();
        var usedBiomes = new HashSet<BiomeId>();
        var totalInverseDistance = 0f;
        
        // First pass: collect K nearest unique biomes and compute inverse distance sum
        for (var i = 0; i < cellData.Length && results.Count < k; i++)
        {
            var (dist, biome, _, _) = cellData[i];
            
            if (!usedBiomes.Contains(biome))
            {
                usedBiomes.Add(biome);
                // Use inverse distance weighting with smoothing to avoid division by zero
                var invDist = 1f / (dist + 0.1f);
                results.Add((biome, invDist));
                totalInverseDistance += invDist;
            }
        }
        
        // Normalize weights to sum to 1
        if (totalInverseDistance > 0.001f)
        {
            for (var i = 0; i < results.Count; i++)
            {
                var (biome, weight) = results[i];
                results[i] = (biome, weight / totalInverseDistance);
            }
        }
        else if (results.Count > 0)
        {
            // Edge case: all at same distance
            var equalWeight = 1f / results.Count;
            for (var i = 0; i < results.Count; i++)
            {
                results[i] = (results[i].biome, equalWeight);
            }
        }
        
        // Sort by weight descending
        results.Sort((a, b) => b.weight.CompareTo(a.weight));
        
        return [.. results];
    }
    
    /// <summary>
    /// Get the blended temperature value at a position using K-nearest biome weights.
    /// Useful for smooth climate transitions that affect block placement or rendering.
    /// </summary>
    public float GetBlendedTemperature(int localX, int localZ, int k = 3)
    {
        var weights = GetBiomeBlendWeights(localX, localZ, k);
        var blended = 0f;
        
        foreach (var (_, weight) in weights)
        {
            // Use the cell's temperature weighted by blend weight
            var cellIndex = GetCellIndex(localX, localZ);
            blended += Temperature[cellIndex] * weight;
        }
        
        return blended;
    }
    
    /// <summary>
    /// Bilinear interpolation of a value at a block position.
    /// </summary>
    private static float GetInterpolatedValue(float[] values, int localX, int localZ)
    {
        // Cell coordinates and position within cell
        var cellX = localX / BlocksPerCell;
        var cellZ = localZ / BlocksPerCell;
        var fx = (localX % BlocksPerCell + 0.5f) / BlocksPerCell;
        var fz = (localZ % BlocksPerCell + 0.5f) / BlocksPerCell;
        
        // Get the 4 surrounding cell values
        var x0 = Math.Clamp(cellX, 0, GridSize - 1);
        var x1 = Math.Clamp(cellX + 1, 0, GridSize - 1);
        var z0 = Math.Clamp(cellZ, 0, GridSize - 1);
        var z1 = Math.Clamp(cellZ + 1, 0, GridSize - 1);
        
        var v00 = values[z0 * GridSize + x0];
        var v10 = values[z0 * GridSize + x1];
        var v01 = values[z1 * GridSize + x0];
        var v11 = values[z1 * GridSize + x1];
        
        // Bilinear interpolation
        var v0 = v00 + (v10 - v00) * fx;
        var v1 = v01 + (v11 - v01) * fx;
        return v0 + (v1 - v0) * fz;
    }

    public void Serialize(System.IO.BinaryWriter writer)
    {
        // ColumnBiomes
        writer.Write(ColumnBiomes.Length);
        for (var i = 0; i < ColumnBiomes.Length; i++) writer.Write((byte)ColumnBiomes[i]);

        // BiomeIds
        writer.Write(BiomeIds.Length);
        for (var i = 0; i < BiomeIds.Length; i++) writer.Write((byte)BiomeIds[i]);

        // CaveBiomeIds
        writer.Write(CaveBiomeIds.Length);
        for (var i = 0; i < CaveBiomeIds.Length; i++) writer.Write((byte)CaveBiomeIds[i]);

        // Climate arrays
        writer.Write(Temperature.Length);
        foreach (var v in Temperature) writer.Write(v);
        
        writer.Write(Humidity.Length);
        foreach (var v in Humidity) writer.Write(v);

        writer.Write(Continentalness.Length);
        foreach (var v in Continentalness) writer.Write(v);

        writer.Write(Erosion.Length);
        foreach (var v in Erosion) writer.Write(v);

        writer.Write(PeaksValleys.Length);
        foreach (var v in PeaksValleys) writer.Write(v);

        writer.Write(Weirdness.Length);
        foreach (var v in Weirdness) writer.Write(v);
    }

    public static ChunkBiomeData Deserialize(System.IO.BinaryReader reader)
    {
        var data = new ChunkBiomeData();
        
        // ColumnBiomes
        var len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.ColumnBiomes[i] = (BiomeId)reader.ReadByte();

        // BiomeIds
        len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.BiomeIds[i] = (BiomeId)reader.ReadByte();

        // CaveBiomeIds
        len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.CaveBiomeIds[i] = (CaveBiomeId)reader.ReadByte();

        // Climate arrays
        len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.Temperature[i] = reader.ReadSingle();

        len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.Humidity[i] = reader.ReadSingle();

        len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.Continentalness[i] = reader.ReadSingle();

        len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.Erosion[i] = reader.ReadSingle();

        len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.PeaksValleys[i] = reader.ReadSingle();

        len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.Weirdness[i] = reader.ReadSingle();

        return data;
    }
}

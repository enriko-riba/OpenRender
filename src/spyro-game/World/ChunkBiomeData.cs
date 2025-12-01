namespace SpyroGame.World;

/// <summary>
/// Stores biome and climate data for a chunk using a coarse 4x4 grid (like Minecraft).
/// Each cell covers a 4x4 block area, so 16 cells cover the 16x16 chunk.
/// </summary>
public sealed class ChunkBiomeData
{
    /// <summary>
    /// Grid resolution: 4x4 cells per chunk (each cell = 4x4 blocks).
    /// </summary>
    public const int GridSize = 4;
    public const int CellCount = GridSize * GridSize; // 16
    public const int BlocksPerCell = VoxelHelper.ChunkSideSize / GridSize; // 4
    
    /// <summary>
    /// Biome ID for each cell in the 4x4 grid.
    /// Index = z * GridSize + x where x,z are cell coordinates [0,3].
    /// </summary>
    public readonly BiomeId[] BiomeIds = new BiomeId[CellCount];
    
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
    /// </summary>
    public BiomeId GetBiomeAt(int localX, int localZ)
    {
        var cellX = localX / BlocksPerCell;
        var cellZ = localZ / BlocksPerCell;
        return BiomeIds[cellZ * GridSize + cellX];
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
    public (float C, float T, float H, float E, float PV) GetCellClimate(int cellX, int cellZ)
    {
        var idx = cellZ * GridSize + cellX;
        return (Continentalness[idx], Temperature[idx], Humidity[idx], Erosion[idx], PeaksValleys[idx]);
    }
    
    /// <summary>
    /// Bilinear interpolation of a value at a block position.
    /// </summary>
    private float GetInterpolatedValue(float[] values, int localX, int localZ)
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
}

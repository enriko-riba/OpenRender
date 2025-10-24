using SpyroGame.Noise;

namespace SpyroGame.World;

public class TerrainBuilder
{
    /// <summary>
    /// Normalized height data in range [0..1]
    /// </summary>
    private readonly float[] heightData;
    private readonly float[] landData;
    private readonly ChunkBiome[] chunkBiomes;
    private readonly int seed;

    private const string heightDataEncoding = "GQANAAQAAADD9eg/BwAAzcxMPwCPwrW/AQ0AAwAAAAAAAEAHAAAAAAA/AAAAAAA=";
    private const string landDataEncoding = "BwA=";
    private const string climateDataEncoding = "DQAFAAAAAAAAQAgAAAAAAD8AAAAAAA==";

    public TerrainBuilder(int seed)
    {
        this.seed = seed;
        
        heightData = NoiseData.CreateFromEncoding(heightDataEncoding, 0, 0, VoxelHelper.WorldChunksXZ * VoxelHelper.ChunkSideSize, VoxelHelper.NoiseFrequency, seed, out var minmax);
        
        //  normallize the range to 0 .. 1
        MapRange(heightData, minmax.min, minmax.max, 0f, 1f);

        landData = NoiseData.CreateFromEncoding(landDataEncoding, 0, 0, VoxelHelper.WorldChunksXZ * VoxelHelper.ChunkSideSize, 0.005f, seed, out _);
        chunkBiomes = new ChunkBiome[landData.Length];
        for (var i = 0; i < heightData.Length; i++)
        {
            var landType = landData[i] switch
            {
                < -0.66f => LandType.DeepOcean,
                < -0.33f => LandType.Ocean,
                < -0.1f => LandType.ShallowOcean,
                < 0.15f => LandType.Beach,
                < 0.3f => LandType.LowLand,
                < 0.45f => LandType.Land,
                < 0.6f => LandType.Hills,
                < 0.8f => LandType.Mountain,
                < 0.9f => LandType.HighMountain,
                _ => LandType.Peaks,
            };
            chunkBiomes[i] = new ChunkBiome(landType, 0, Climate.Temperate);
        }
    }

    private static void MapRange(float[] input, float min, float max, float newMin, float newMax)
    {
        var diff = newMax - newMin;
        var divisor = max - min;
        var koeficient = diff / divisor;
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (input[i] - min) * koeficient + newMin;
        }
    }

    /// <summary>
    /// Raw height data in range [0, 1].
    /// </summary>
    public float[] HeightData => heightData;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="globalX"></param>
    /// <param name="globalZ"></param>
    /// <returns></returns>
    public int GetHeightNormalizedGlobal(int globalX, int globalZ)
    {
        var noiseIndex = globalX + globalZ * VoxelHelper.WorldChunksXZ * VoxelHelper.ChunkSideSize;
        var height = heightData[noiseIndex];
        return (int)Math.Round(height * (VoxelHelper.ChunkYSize-1));

        switch (chunkBiomes[noiseIndex].LandType)
        {
            case LandType.DeepOcean:
                height = height * VoxelHelper.WaterLevel * 0.33f;
                break;

            case LandType.Ocean:
                height = (VoxelHelper.WaterLevel * 0.33f) + (height * VoxelHelper.WaterLevel * 0.3f);
                break;

            case LandType.ShallowOcean:
                height = (VoxelHelper.WaterLevel * 0.6f) + (height * VoxelHelper.WaterLevel * 0.4f) - 1;
                break;

            case LandType.Beach:
                height = VoxelHelper.WaterLevel + height * 4;       //  water level .. water level + 4
                break;

            case LandType.LowLand:
                height = VoxelHelper.WaterLevel + 2 + height * 8;   //  water level + 2 .. water level + 10
                break;

            case LandType.Land:
                height = VoxelHelper.WaterLevel + 4 + height * 10;  //  water level + 4 .. water level + 14
                break;

            case LandType.Hills:
                height = VoxelHelper.WaterLevel + 10 + height * 15;  //  water level + 10 .. water level + 25
                break;

            case LandType.Mountain:
                height = VoxelHelper.WaterLevel + 15 + height * (VoxelHelper.ChunkYSize * 0.5f);       //  water level + 15 .. max height * 0.5
                break;

            case LandType.HighMountain:
                height = VoxelHelper.ChunkYSize * 0.5f + height * VoxelHelper.ChunkYSize * 0.3f;      //  max height * 0.5 .. max height * 0.8
                break;

            case LandType.Peaks:
                height = VoxelHelper.ChunkYSize * 0.75f + height * (VoxelHelper.ChunkYSize * 0.25f);    //  max height * 0.75 .. max height
                break;
        }
        return (int)Math.Round(height);
    }

    public int GetHeightNormalizedChunkLocal(int chunkIndex, int x, int z)
    {
        //  get chunks world position
        var worldX = chunkIndex % VoxelHelper.WorldChunksXZ;
        var worldZ = chunkIndex / VoxelHelper.WorldChunksXZ;
        var cx = worldX * VoxelHelper.ChunkSideSize;
        var cz = worldZ * VoxelHelper.ChunkSideSize;

        //  get height data from global position
        var height = GetHeightNormalizedGlobal(x + cx, z + cz);
        
        return height;
    }

    public float[] GetChunkHeightMap(int chunkIndex)
    {
        var worldX = chunkIndex % VoxelHelper.WorldChunksXZ;
        var worldZ = chunkIndex / VoxelHelper.WorldChunksXZ;
        var heightMap = NoiseData.CreateFromEncoding(heightDataEncoding, worldX * VoxelHelper.ChunkSideSize, worldZ * VoxelHelper.ChunkSideSize, VoxelHelper.ChunkSideSize, VoxelHelper.NoiseFrequency, seed, out _);
        return heightMap;
    }

    /// <summary>
    /// Generates a block from chunk local coordinates.
    /// </summary>
    /// <param name="chunkIndex"></param>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="z"></param>
    /// <returns></returns>
    public BlockType GenerateChunkBlockLocal(int chunkIndex, int x, int y, int z)
    {
        var height = GetHeightNormalizedChunkLocal(chunkIndex, x, z);
        return GenerateChunkBlockType(height, x, y, z);
    }

    /// <summary>
    /// Calculates the block type from height and local coordinates.
    /// </summary>
    /// <param name="maxHeight"></param>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="z"></param>
    /// <returns></returns>
    public static BlockType GenerateChunkBlockType(int maxHeight, int x, int y, int z)
    {
        var blockAltitude = y;

        BlockType bt;
        if (blockAltitude > maxHeight)
        {
            bt = BlockType.None;
        }
        else if (blockAltitude == maxHeight)
        {
            bt = BlockType.GrassDirt;
        }
        else if (blockAltitude < maxHeight && blockAltitude >= maxHeight - 2)
        {
            bt = BlockType.Dirt;
        }
        else if (blockAltitude < maxHeight - 2)
        {
            bt = BlockType.Rock;
        }
        else
        {
            bt = BlockType.None;
        }

        //  special cases
        if (blockAltitude <= (int)VoxelHelper.WaterLevel)
        {
            if ((bt == BlockType.None) && blockAltitude == (int)VoxelHelper.WaterLevel)
            {
                bt = BlockType.WaterLevel;
            }
            else if ((bt != BlockType.None) && (blockAltitude >= VoxelHelper.WaterLevel - 1))
            {
                bt = BlockType.Sand;
            }
            else if ((bt != BlockType.None) && (blockAltitude < VoxelHelper.WaterLevel - 1))
            {
                //  replace top layer underwater solid blocks with bedrock
                bt = BlockType.BedRock;
            }
            else if (blockAltitude < 3)
            {
                bt = BlockType.BedRock;
            }
        }

        return bt;
    }
}

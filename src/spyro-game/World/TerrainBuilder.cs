using SpyroGame.Noise;

namespace SpyroGame.World;

public class TerrainBuilder(int seed)
{
    private readonly float[] heightData = NoiseData.CreateFromEncoding("BwA=", 0, 0, VoxelHelper.WorldChunksXZ * VoxelHelper.ChunkSideSize, VoxelHelper.NoiseFrequency, seed, out var minmax);

    private int GetHeightNormalizedGlobal(int globalX, int globalZ)
    {
        var noiseIndex = globalX + globalZ * VoxelHelper.WorldChunksXZ * VoxelHelper.ChunkSideSize;
        var height = heightData[noiseIndex];
        height = height * VoxelHelper.HeightAmplitude + VoxelHelper.WaterLevel;
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
        return GetHeightNormalizedGlobal(x + cx, z + cz);
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
    public BlockType GenerateChunkBlockType(int maxHeight, int x, int y, int z)
    {
        //var maxHeight = GetHeightNormalizedChunkLocal(index, x, z);
        var blockAltitude = y;

        BlockType bt;
        if (blockAltitude > maxHeight)
        {
            bt = BlockType.None;
        }
        else if (blockAltitude >= maxHeight && blockAltitude <= maxHeight + 1)
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
            if ((bt != BlockType.None) && (bt != BlockType.WaterLevel) && (blockAltitude >= VoxelHelper.WaterLevel - 1))
            {
                bt = BlockType.Sand;
            }
            else if ((bt != BlockType.None) && (bt != BlockType.WaterLevel) && (blockAltitude < VoxelHelper.WaterLevel - 1))
            {
                //  replace top layer underwater solid blocks with bedrock
                bt = BlockType.Rock;
            }
            else if ((bt == BlockType.None) && blockAltitude == (int)VoxelHelper.WaterLevel)
            {
                bt = BlockType.WaterLevel;
            }
            else
            {
                bt = BlockType.None;
            }
        }

        /*
        // for debugging        
        bt = blockAltitude <= height + 1 && blockAltitude >= height ? x == 0 ? BlockType.Sand :
            x == VoxelHelper.ChunkSizeXZMinusOne ? BlockType.Dirt :
            z == 0 ? BlockType.Rock :
            z == VoxelHelper.ChunkSizeXZMinusOne ? BlockType.Snow :
            BlockType.Grass : BlockType.None;
        */

        //var blockIdx = x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare;
        //var block = new BlockState
        //{
        //    Index = blockIdx,
        //    BlockType = bt,
        //};
        //return block;

        return bt;
    }
}

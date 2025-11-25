using OpenTK.Mathematics;

namespace SpyroGame.World;

/// <summary>
/// GPU-only chunk container. Stores only metadata and GPU collision data.
/// All terrain generation happens on GPU via compute shaders.
/// </summary>
public class Chunk(int index)
{
    private readonly int[,] maxHeights = new int[VoxelHelper.ChunkSideSize, VoxelHelper.ChunkSideSize];

  
    /// <summary>
    /// Returns the height of the top face of the highest block.
    /// </summary>
    public int GetTerrainHeightAt(int x, int z) => maxHeights[x, z] + 1;

    public Vector2i ChunkPosition { get; internal set; }

    public AABB Aabb { get; internal set; }


    public int Index => index;

    #region GPU Rendering & Collision Data
    
    public Vector3i Position => Aabb.Min;
    
    // GPU vertex buffers for rendering
    public volatile uint BlocksSSBO;
    public int SolidCount;
    public int SolidCapacity;
    public volatile uint TransparentBlocksSSBO;
    public int TransparentCount;
    public int TransparentCapacity;
    
    // Visibility & streaming state
    public bool Visible;
    internal byte VisibleLinger;
    internal volatile bool PendingUpload;
    internal volatile bool PendingCompute;
    internal volatile bool ComputeInProgress;
   

    // GPU column heights for heightmap-based collision
    public bool HasGpuColumns { get; internal set; }

    // GPU-provided collision spans per XZ column: up to MaxSpans pairs (yStart,yEnd) per column
    private int[]? columnSpanPairs;
    private byte[]? columnSpanTypes;
    private byte[]? columnSpanCounts;
    public bool HasGpuSpans { get; private set; }

    /// <summary>
    /// Apply GPU-generated collision spans for accurate collision detection (caves, overhangs).
    /// </summary>
    internal void ApplyColumnSpansForCollision(int[] spansPairs, byte[] counts, byte[] types)
    {
        columnSpanPairs = spansPairs;
        columnSpanCounts = counts;
        columnSpanTypes = types;
        HasGpuSpans = true;

        // Update maxHeights for compatibility with simple heightmap queries
        var size = VoxelHelper.ChunkSideSize;
        for (var z = 0; z < size; z++)
        {
            for (var x = 0; x < size; x++)
            {
                var col = x + z * size;
                var c = counts[col];
                var maxH = 0;
                var baseIdx = col * 16 * 2; // MaxSpansPerColumn = 16
                for (var i = 0; i < c && i < 16; i++)
                {
                    var y1 = spansPairs[baseIdx + i * 2 + 1];
                    if (y1 > maxH) maxH = y1;
                }
                maxHeights[x, z] = maxH - 1;
            }
        }
        HasGpuColumns = true;
    }

    /// <summary>
    /// Query block type from GPU collision spans (supports caves/overhangs).
    /// </summary>
    internal BlockType GetBlockTypeFromSpans(int lx, int ly, int lz, int maxSpans)
    {
        if (!HasGpuSpans || columnSpanPairs is null || columnSpanCounts is null || columnSpanTypes is null) 
            return BlockType.None;
        
        var size = VoxelHelper.ChunkSideSize;
        var col = lx + lz * size;
        var c = columnSpanCounts[col];
        if (c == 0) return BlockType.None;
        
        var baseIdx = col * maxSpans * 2;
        var typeBaseIdx = col * maxSpans;
        for (var i = 0; i < c && i < maxSpans; i++)
        {
            var y0 = columnSpanPairs[baseIdx + i * 2 + 0];
            var y1 = columnSpanPairs[baseIdx + i * 2 + 1];
            if (ly >= y0 && ly < y1)
            {
                return (BlockType)columnSpanTypes[typeBaseIdx + i];
            }
        }
        return BlockType.None;
    }

    #endregion

    public override string ToString() => $"{ChunkPosition}";
}
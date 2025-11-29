using System;
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
        columnSpanTypes = types; // BlockDescriptor bytes from GPU
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

    internal bool TryGetSpanData(out int[] spansPairs, out byte[] counts, out byte[] types)
    {
        if (columnSpanPairs != null && columnSpanCounts != null && columnSpanTypes != null)
        {
            spansPairs = columnSpanPairs;
            counts = columnSpanCounts;
            types = columnSpanTypes;
            return true;
        }

        spansPairs = [];
        counts = [];
        types = [];
        return false;
    }

    /// <summary>
    /// Query block descriptor from GPU collision spans (supports caves/overhangs).
    /// Returns the BlockDescriptor at the specified local position.
    /// </summary>
    internal BlockDescriptor GetDescriptorFromSpans(int lx, int ly, int lz, int maxSpans)
    {
        if (!HasGpuSpans || columnSpanPairs is null || columnSpanCounts is null || columnSpanTypes is null) 
            return BlockDescriptor.Air;
        
        var size = VoxelHelper.ChunkSideSize;
        var col = lx + lz * size;
        var c = columnSpanCounts[col];
        if (c == 0) return BlockDescriptor.Air;
        
        var baseIdx = col * maxSpans * 2;
        var typeBaseIdx = col * maxSpans;
        for (var i = 0; i < c && i < maxSpans; i++)
        {
            var y0 = columnSpanPairs[baseIdx + i * 2 + 0];
            var y1 = columnSpanPairs[baseIdx + i * 2 + 1];
            if (ly >= y0 && ly < y1)
            {
                return (BlockDescriptor)columnSpanTypes[typeBaseIdx + i];
            }
        }
        return BlockDescriptor.Air;
    }

    /// <summary>
    /// DEPRECATED: Legacy method for backward compatibility.
    /// Use GetDescriptorFromSpans() instead.
    /// </summary>
    [Obsolete("Use GetDescriptorFromSpans() instead. GeologyLayer has been renamed to BlockDescriptor.")]
    internal BlockDescriptor GetGeologyFromSpans(int lx, int ly, int lz, int maxSpans)
    {
        return GetDescriptorFromSpans(lx, ly, lz, maxSpans);
    }

    /// <summary>
    /// DEPRECATED: Legacy method for backward compatibility.
    /// Use GetDescriptorFromSpans() instead.
    /// </summary>
    [Obsolete("Use GetDescriptorFromSpans() instead. BlockType is being phased out in favor of BlockDescriptor.")]
    internal BlockType GetBlockTypeFromSpans(int lx, int ly, int lz, int maxSpans)
    {
        var descriptor = GetDescriptorFromSpans(lx, ly, lz, maxSpans);
        return descriptor switch
        {
            BlockDescriptor.Air => BlockType.None,
            BlockDescriptor.Water => BlockType.WaterLevel,
            BlockDescriptor.Surface => BlockType.GrassDirt,
            BlockDescriptor.Subsurface => BlockType.Dirt,
            BlockDescriptor.DeepSubsurface => BlockType.Rock,
            BlockDescriptor.UnderwaterSurface => BlockType.Sand,
            BlockDescriptor.UnderwaterSubsurface => BlockType.Sand,
            BlockDescriptor.ShoreLine => BlockType.Sand,
            _ => BlockType.Rock
        };
    }

    #endregion

    public override string ToString() => $"{ChunkPosition}";
}
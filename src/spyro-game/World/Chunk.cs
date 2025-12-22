using System;
using OpenTK.Mathematics;
using SpyroGame.Components;
using SpyroGame.World.Registry;

namespace SpyroGame.World;

/// <summary>
/// Chunk container. Stores metadata and collision data.
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

    #region Collision Data

    public Vector3i Position => Aabb.Min;

    // Column heights for heightmap-based collision
    public bool HasCollisionData { get; internal set; }

    // Collision spans per XZ column: up to MaxSpans pairs (yStart,yEnd) per column
    private int[]? columnSpanPairs;
    private BlockId[]? columnSpanTypes;
    private byte[]? columnSpanCounts;
    public bool HasSpanData { get; private set; }

    /// <summary>
    /// Apply generated collision spans for accurate collision detection (caves, overhangs).
    /// </summary>
    internal void ApplyColumnSpansForCollision(int[] spansPairs, byte[] counts, BlockId[] types)
    {
        columnSpanPairs = spansPairs;
        columnSpanCounts = counts;
        columnSpanTypes = types;
        HasSpanData = true;

        // Update maxHeights for compatibility with simple heightmap queries
        var size = VoxelHelper.ChunkSideSize;
        for (var z = 0; z < size; z++)
        {
            for (var x = 0; x < size; x++)
            {
                var col = x + z * size;
                var c = counts[col];
                var maxH = 0;
                var baseIdx = col * ChunkCollisionData.MaxSpansPerColumn * 2;
                for (var i = 0; i < c && i < ChunkCollisionData.MaxSpansPerColumn; i++)
                {
                    var y1 = spansPairs[baseIdx + i * 2 + 1];
                    if (y1 > maxH) maxH = y1;
                }
                maxHeights[x, z] = maxH - 1;
            }
        }
        HasCollisionData = true;
    }

    /// <summary>
    /// Rebuild collision spans for a single column from voxel data.
    /// Used for immediate block edit updates.
    /// </summary>
    internal void RebuildColumnSpans(int localX, int localZ, ChunkData voxelData)
    {
        // Initialize arrays if needed
        columnSpanPairs ??= new int[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn * 2];
        columnSpanCounts ??= new byte[VoxelHelper.ChunkSideSizeSquare];
        columnSpanTypes ??= new BlockId[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn];
        
        var col = localX + localZ * VoxelHelper.ChunkSideSize;
        var pairBase = col * ChunkCollisionData.MaxSpansPerColumn * 2;
        var typeBase = col * ChunkCollisionData.MaxSpansPerColumn;
        
        // Rebuild spans by scanning the column
        // Include ALL non-air blocks (not just solid) so torches etc. can be picked
        var spanCount = 0;
        var inSpan = false;
        var spanStart = 0;
        var spanBlock = BlockId.Air;
        
        for (var y = 0; y < VoxelHelper.ChunkYSize && spanCount < ChunkCollisionData.MaxSpansPerColumn; y++)
        {
            var block = voxelData.GetBlock(localX, y, localZ);
            var isNonAir = !block.IsAir();  // Include ALL non-air blocks for picking
            
            if (isNonAir && !inSpan)
            {
                inSpan = true;
                spanStart = y;
                spanBlock = block;
            }
            else if (!isNonAir && inSpan)
            {
                // Commit span
                columnSpanPairs[pairBase + spanCount * 2] = spanStart;
                columnSpanPairs[pairBase + spanCount * 2 + 1] = y; // exclusive end
                columnSpanTypes[typeBase + spanCount] = spanBlock;
                spanCount++;
                inSpan = false;
            }
            else if (isNonAir && inSpan && block != spanBlock)
            {
                // Block type changed
                columnSpanPairs[pairBase + spanCount * 2] = spanStart;
                columnSpanPairs[pairBase + spanCount * 2 + 1] = y;
                columnSpanTypes[typeBase + spanCount] = spanBlock;
                spanCount++;
                if (spanCount < ChunkCollisionData.MaxSpansPerColumn)
                {
                    spanStart = y;
                    spanBlock = block;
                }
                else
                {
                    inSpan = false;
                }
            }
        }
        
        // Close final span
        if (inSpan && spanCount < ChunkCollisionData.MaxSpansPerColumn)
        {
            columnSpanPairs[pairBase + spanCount * 2] = spanStart;
            columnSpanPairs[pairBase + spanCount * 2 + 1] = VoxelHelper.ChunkYSize;
            columnSpanTypes[typeBase + spanCount] = spanBlock;
            spanCount++;
        }
        
        columnSpanCounts[col] = (byte)spanCount;
        
        // Update maxHeights for this column
        var maxH = 0;
        for (var i = 0; i < spanCount; i++)
        {
            var y1 = columnSpanPairs[pairBase + i * 2 + 1];
            if (y1 > maxH) maxH = y1;
        }
        maxHeights[localX, localZ] = maxH - 1;
        
        HasSpanData = true;
        HasCollisionData = true;
    }

    /// <summary>
    /// Rebuilds collision spans for the entire chunk from voxel data.
    /// Used when loading full chunk state from disk.
    /// </summary>
    internal void RebuildAllCollisionSpans(ChunkData voxelData)
    {
        var size = VoxelHelper.ChunkSideSize;
        for (var z = 0; z < size; z++)
        {
            for (var x = 0; x < size; x++)
            {
                RebuildColumnSpans(x, z, voxelData);
            }
        }
        HasCollisionData = true;
    }

    /// <summary>
    /// Try to get the raw span data arrays.
    /// </summary>
    internal bool TryGetSpanData(out int[] spansPairs, out byte[] counts, out BlockId[] types)
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
    /// Convert internal span data to ChunkCollisionData for the CollisionManager.
    /// </summary>
    public ChunkCollisionData ToChunkCollisionData()
    {
        var data = new ChunkCollisionData();
        if (!HasSpanData || columnSpanPairs == null || columnSpanCounts == null || columnSpanTypes == null)
            return data;

        Array.Copy(columnSpanCounts, data.SpanCounts, columnSpanCounts.Length);

        // Both arrays are flattened: ColumnsPerChunk * MaxSpansPerColumn
        // columnSpanPairs has 2 ints per span (StartY is inclusive, EndY is EXCLUSIVE)
        // columnSpanTypes has 1 BlockId per span
        // ChunkCollisionData.Spans has 1 struct per span (StartY and EndY are both INCLUSIVE)
        
        var totalSpans = columnSpanTypes.Length;
        for (var i = 0; i < totalSpans; i++)
        {
            // Convert exclusive EndY to inclusive by subtracting 1
            data.Spans[i] = new ColumnSpan
            {
                StartY = (short)columnSpanPairs[i * 2],
                EndY = (short)(columnSpanPairs[i * 2 + 1] - 1),
                Block = (ushort)columnSpanTypes[i]
            };
        }
        
        return data;
    }

    /// <summary>
    /// Query block type from collision spans (supports caves/overhangs).
    /// Returns the BlockId at the specified local position.
    /// </summary>
    internal BlockId GetBlockFromSpans(int lx, int ly, int lz, int maxSpans)
    {
        if (!HasSpanData || columnSpanPairs is null || columnSpanCounts is null || columnSpanTypes is null)
            return BlockId.Air;

        var size = VoxelHelper.ChunkSideSize;
        var col = lx + lz * size;
        var c = columnSpanCounts[col];
        if (c == 0) return BlockId.Air;

        var baseIdx = col * maxSpans * 2;
        var typeBaseIdx = col * maxSpans;
        for (var i = 0; i < c && i < maxSpans; i++)
        {
            var y0 = columnSpanPairs[baseIdx + i * 2 + 0];
            var y1 = columnSpanPairs[baseIdx + i * 2 + 1];
            if (ly >= y0 && ly < y1)
            {
                return columnSpanTypes[typeBaseIdx + i];
            }
        }
        return BlockId.Air;
    }

    #endregion

    public override string ToString() => $"{ChunkPosition}";
}
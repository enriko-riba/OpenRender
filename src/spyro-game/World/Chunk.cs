using System;
using OpenTK.Mathematics;

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
                var baseIdx = col * 16 * 2; // MaxSpansPerColumn = 16
                for (var i = 0; i < c && i < 16; i++)
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
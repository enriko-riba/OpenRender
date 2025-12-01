using OpenTK.Mathematics;

namespace SpyroGame.World;

/// <summary>
/// Represents a vertical span of blocks in a column.
/// Used for efficient collision detection with caves and overhangs.
/// </summary>
public struct ColumnSpan
{
    public short StartY;
    public short EndY;
    /// <summary>
    /// The block type for this span (stores full BlockId as ushort).
    /// </summary>
    public ushort Block;
}

public class ChunkCollisionData
{
    public const int ColumnsPerChunk = 16 * 16;
    public const int MaxSpansPerColumn = 16;

    // Flattened array of spans for all columns
    // Indexing: columnIdx * MaxSpansPerColumn + spanIdx
    public ColumnSpan[] Spans;
    public byte[] SpanCounts; // Number of spans per column

    public ChunkCollisionData()
    {
        Spans = new ColumnSpan[ColumnsPerChunk * MaxSpansPerColumn];
        SpanCounts = new byte[ColumnsPerChunk];
    }
}

public class CollisionManager
{
    private readonly Dictionary<int, ChunkCollisionData> chunkCollisionData = [];
    private readonly object lockObj = new();

    public void UpdateChunkData(int chunkIndex, ChunkCollisionData data)
    {
        lock (lockObj)
        {
            chunkCollisionData[chunkIndex] = data;
        }
    }

    public void RemoveChunkData(int chunkIndex)
    {
        lock (lockObj)
        {
            chunkCollisionData.Remove(chunkIndex);
        }
    }

    public bool TryGetChunkData(int chunkIndex, out ChunkCollisionData data)
    {
        lock (lockObj)
        {
            return chunkCollisionData.TryGetValue(chunkIndex, out data);
        }
    }

    // Raycasting and collision logic
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 hitPoint, out Vector3i blockPos, out Vector3 normal, out BlockId block)
    {
        hitPoint = Vector3.Zero;
        blockPos = Vector3i.Zero;
        normal = Vector3.Zero;
        block = BlockId.Air;

        direction = Vector3.Normalize(direction);
        var t = 0.0f;

        // Current voxel position
        var x = (int)Math.Floor(origin.X);
        var y = (int)Math.Floor(origin.Y);
        var z = (int)Math.Floor(origin.Z);

        // Step direction
        var stepX = Math.Sign(direction.X);
        var stepY = Math.Sign(direction.Y);
        var stepZ = Math.Sign(direction.Z);

        // tMax: distance to next voxel boundary
        var tMaxX = (stepX > 0) ? (x + 1 - origin.X) / direction.X : (origin.X - x) / -direction.X;
        var tMaxY = (stepY > 0) ? (y + 1 - origin.Y) / direction.Y : (origin.Y - y) / -direction.Y;
        var tMaxZ = (stepZ > 0) ? (z + 1 - origin.Z) / direction.Z : (origin.Z - z) / -direction.Z;

        // tDelta: distance to traverse one voxel
        var tDeltaX = Math.Abs(1.0f / direction.X);
        var tDeltaY = Math.Abs(1.0f / direction.Y);
        var tDeltaZ = Math.Abs(1.0f / direction.Z);

        // Avoid division by zero issues
        if (Math.Abs(direction.X) < 1e-6) { tMaxX = float.MaxValue; tDeltaX = float.MaxValue; }
        if (Math.Abs(direction.Y) < 1e-6) { tMaxY = float.MaxValue; tDeltaY = float.MaxValue; }
        if (Math.Abs(direction.Z) < 1e-6) { tMaxZ = float.MaxValue; tDeltaZ = float.MaxValue; }

        var lastPos = new Vector3i(x, y, z);

        while (t < maxDistance)
        {
            // Check if current voxel is solid
            if (IsSolid(x, y, z, out block))
            {
                // Ignore water blocks for picking (only solid blocks matter)
                if (!block.IsWater())
                {
                    hitPoint = origin + direction * t;
                    blockPos = new Vector3i(x, y, z);

                    // Calculate normal based on which face we entered
                    if (lastPos.X != x) normal = new Vector3(-stepX, 0, 0);
                    else if (lastPos.Y != y) normal = new Vector3(0, -stepY, 0);
                    else if (lastPos.Z != z) normal = new Vector3(0, 0, -stepZ);
                    else normal = -direction; // Should not happen if we step correctly

                    return true;
                }
            }

            // Advance to next voxel
            if (tMaxX < tMaxY)
            {
                if (tMaxX < tMaxZ)
                {
                    x += stepX;
                    t = tMaxX;
                    tMaxX += tDeltaX;
                }
                else
                {
                    z += stepZ;
                    t = tMaxZ;
                    tMaxZ += tDeltaZ;
                }
            }
            else
            {
                if (tMaxY < tMaxZ)
                {
                    y += stepY;
                    t = tMaxY;
                    tMaxY += tDeltaY;
                }
                else
                {
                    z += stepZ;
                    t = tMaxZ;
                    tMaxZ += tDeltaZ;
                }
            }
        }

        return false;
    }

    private bool IsSolid(int x, int y, int z, out BlockId block)
    {
        block = BlockId.Air;
        if (y < 0 || y >= VoxelHelper.ChunkYSize) return false;

        var chunkX = (int)Math.Floor((float)x / 16.0f);
        var chunkZ = (int)Math.Floor((float)z / 16.0f);
        var chunkIdx = chunkZ * VoxelHelper.WorldChunksXZ + chunkX;

        if (TryGetChunkData(chunkIdx, out var data))
        {
            var localX = x % 16;
            var localZ = z % 16;
            if (localX < 0) localX += 16;
            if (localZ < 0) localZ += 16;

            var colIdx = localZ * 16 + localX;
            var count = data.SpanCounts[colIdx];
            var offset = colIdx * 16;       // MaxSpansPerColumn

            for (var i = 0; i < count; i++)
            {
                var span = data.Spans[offset + i];
                if (y >= span.StartY && y <= span.EndY)
                {
                    // Span stores full BlockId as ushort
                    block = (BlockId)span.Block;
                    // Solid if the block has the Solid flag
                    return block.IsSolid();
                }
            }
        }

        return false;
    }
}

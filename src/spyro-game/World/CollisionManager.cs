using OpenTK.Mathematics;
using SpyroGame.World;

namespace SpyroGame.World
{
    public struct ColumnSpan
    {
        public byte StartY;
        public byte EndY;
        public byte BlockType;
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
        private readonly Dictionary<int, ChunkCollisionData> chunkCollisionData = new();
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
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 hitPoint, out Vector3i blockPos, out Vector3 normal, out BlockType blockType)
        {
            hitPoint = Vector3.Zero;
            blockPos = Vector3i.Zero;
            normal = Vector3.Zero;
            blockType = BlockType.None;

            direction = Vector3.Normalize(direction);
            float t = 0.0f;
            
            // Current voxel position
            int x = (int)Math.Floor(origin.X);
            int y = (int)Math.Floor(origin.Y);
            int z = (int)Math.Floor(origin.Z);

            // Step direction
            int stepX = Math.Sign(direction.X);
            int stepY = Math.Sign(direction.Y);
            int stepZ = Math.Sign(direction.Z);

            // tMax: distance to next voxel boundary
            float tMaxX = (stepX > 0) ? (x + 1 - origin.X) / direction.X : (origin.X - x) / -direction.X;
            float tMaxY = (stepY > 0) ? (y + 1 - origin.Y) / direction.Y : (origin.Y - y) / -direction.Y;
            float tMaxZ = (stepZ > 0) ? (z + 1 - origin.Z) / direction.Z : (origin.Z - z) / -direction.Z;

            // tDelta: distance to traverse one voxel
            float tDeltaX = Math.Abs(1.0f / direction.X);
            float tDeltaY = Math.Abs(1.0f / direction.Y);
            float tDeltaZ = Math.Abs(1.0f / direction.Z);
            
            // Avoid division by zero issues
            if (Math.Abs(direction.X) < 1e-6) { tMaxX = float.MaxValue; tDeltaX = float.MaxValue; }
            if (Math.Abs(direction.Y) < 1e-6) { tMaxY = float.MaxValue; tDeltaY = float.MaxValue; }
            if (Math.Abs(direction.Z) < 1e-6) { tMaxZ = float.MaxValue; tDeltaZ = float.MaxValue; }

            Vector3i lastPos = new Vector3i(x, y, z);

            while (t < maxDistance)
            {
                // Check if current voxel is solid
                if (IsSolid(x, y, z, out blockType))
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

                lastPos = new Vector3i(x, y, z);

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

        private bool IsSolid(int x, int y, int z, out BlockType blockType)
        {
            blockType = BlockType.None;
            if (y < 0 || y >= 128) return false;

            int chunkX = (int)Math.Floor((float)x / 16.0f);
            int chunkZ = (int)Math.Floor((float)z / 16.0f);
            int chunkIdx = chunkZ * VoxelHelper.WorldChunksXZ + chunkX;
            
            if (TryGetChunkData(chunkIdx, out var data))
            {
                int localX = x % 16;
                int localZ = z % 16;
                if (localX < 0) localX += 16;
                if (localZ < 0) localZ += 16;
                
                int colIdx = localZ * 16 + localX;
                int count = data.SpanCounts[colIdx];
                int offset = colIdx * 16; // MaxSpansPerColumn
                
                for (int i = 0; i < count; i++)
                {
                    var span = data.Spans[offset + i];
                    if (y >= span.StartY && y <= span.EndY)
                    {
                        blockType = (BlockType)span.BlockType;
                        return true;
                    }
                }
            }
            
            return false;
        }
    }
}

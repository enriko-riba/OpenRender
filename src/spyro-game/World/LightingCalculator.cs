using System;
using System.Collections.Generic;
using OpenRender;

namespace SpyroGame.World;

public static class LightingCalculator
{
    private const int MaxLight = 15;
    
    // Queue for BFS flood fill
    // Storing packed coordinates: x | (y << 4) | (z << 13)
    // This fits in int since x,z are 0-15 (4 bits), y is 0-383 (9 bits)
    // 4 + 9 + 4 = 17 bits. Plenty of space.
    [ThreadStatic]
    private static Queue<int>? t_lightQueue;

    private static Queue<int> LightQueue
    {
        get
        {
            t_lightQueue ??= new Queue<int>(4096);
            return t_lightQueue;
        }
    }

    public static void CalculateLighting(ChunkData chunk)
    {
        // 1. Clear Light Data
        Array.Clear(chunk.LightData, 0, chunk.LightData.Length);

        // 2. Initialize Sky Light
        InitializeSkyLight(chunk);

        // 3. Propagate Sky Light
        PropagateLight(chunk, isSkyLight: true);

        // 4. Initialize Block Light (scan for emissive blocks like Torch, Glowstone, Lava)
        InitializeBlockLight(chunk);
        
        // 5. Propagate Block Light
        PropagateLight(chunk, isSkyLight: false);
    }

    /// <summary>
    /// Updates lighting after a block is placed or removed at the specified position.
    /// This is more efficient than recalculating the entire chunk.
    /// </summary>
    /// <param name="chunk">The chunk containing the block.</param>
    /// <param name="x">Local X coordinate (0-15).</param>
    /// <param name="y">Local Y coordinate (0-383).</param>
    /// <param name="z">Local Z coordinate (0-15).</param>
    /// <param name="oldBlock">The previous block at this position.</param>
    /// <param name="newBlock">The new block at this position.</param>
    public static void UpdateLightingAtBlock(ChunkData chunk, int x, int y, int z, BlockId oldBlock, BlockId newBlock)
    {
        // For now, use a simpler approach: recalculate the entire chunk
        // A more optimized approach would use light removal + re-propagation only in affected area
        // But that's more complex and can be implemented later if needed
        
        var oldLightValue = BlockRegistry.GetLightValue(oldBlock);
        var newLightValue = BlockRegistry.GetLightValue(newBlock);
        var oldWasOpaque = oldBlock.IsOpaque();
        var newIsOpaque = newBlock.IsOpaque();
        
        // If light emission changed or opacity changed, recalculate lighting
        if (oldLightValue != newLightValue || oldWasOpaque != newIsOpaque)
        {
            CalculateLighting(chunk);
        }
    }

    /// <summary>
    /// Propagate light between two neighboring chunks.
    /// Scans the shared boundary and propagates light from brighter to darker blocks.
    /// </summary>
    public static void PropagateNeighborLight(ChunkData chunkA, ChunkData chunkB, int dx, int dz)
    {
        // Determine boundary coordinates
        // dx=1: A is left, B is right. Boundary: A(15,y,z) <-> B(0,y,z)
        // dx=-1: A is right, B is left. Boundary: A(0,y,z) <-> B(15,y,z)
        // dz=1: A is back, B is front. Boundary: A(x,y,15) <-> B(x,y,0)
        // dz=-1: A is front, B is back. Boundary: A(x,y,0) <-> B(x,y,15)

        int xA = 0, xB = 0, zA = 0, zB = 0;
        int loopX = 1, loopZ = 1;

        if (dx == 1) { xA = VoxelHelper.ChunkSideSize - 1; xB = 0; loopZ = VoxelHelper.ChunkSideSize; }
        else if (dx == -1) { xA = 0; xB = VoxelHelper.ChunkSideSize - 1; loopZ = VoxelHelper.ChunkSideSize; }
        else if (dz == 1) { zA = VoxelHelper.ChunkSideSize - 1; zB = 0; loopX = VoxelHelper.ChunkSideSize; }
        else if (dz == -1) { zA = 0; zB = VoxelHelper.ChunkSideSize - 1; loopX = VoxelHelper.ChunkSideSize; }
        else return;

        // We do two passes:
        // 1. Propagate B -> A (update A, enqueue A)
        // 2. Propagate A -> B (update B, enqueue B)
        // This ensures both chunks are updated based on the other's light.

        // Pass 1: B -> A
        var queue = LightQueue;
        queue.Clear();
        bool changedA = false;

        for (int y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (int i = 0; i < (loopX == 1 ? loopZ : loopX); i++)
            {
                int cx = (loopX == 1) ? xA : i;
                int cz = (loopX == 1) ? i : zA;
                int nx = (loopX == 1) ? xB : i;
                int nz = (loopX == 1) ? i : zB;

                // Check B -> A
                if (PropagateSingleBlock(chunkB, nx, y, nz, chunkA, cx, y, cz, true))
                {
                    queue.Enqueue(PackPos(cx, y, cz));
                    changedA = true;
                }
            }
        }

        if (changedA)
        {
            PropagateLight(chunkA, isSkyLight: true);
        }

        // Pass 2: A -> B
        queue.Clear();
        bool changedB = false;

        for (int y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (int i = 0; i < (loopX == 1 ? loopZ : loopX); i++)
            {
                int cx = (loopX == 1) ? xA : i;
                int cz = (loopX == 1) ? i : zA;
                int nx = (loopX == 1) ? xB : i;
                int nz = (loopX == 1) ? i : zB;

                // Check A -> B
                if (PropagateSingleBlock(chunkA, cx, y, cz, chunkB, nx, y, nz, true))
                {
                    queue.Enqueue(PackPos(nx, y, nz));
                    changedB = true;
                }
            }
        }

        if (changedB)
        {
            PropagateLight(chunkB, isSkyLight: true);
        }

        // Also propagate block light between chunks
        queue.Clear();
        bool changedABlock = false;
        for (int y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (int i = 0; i < (loopX == 1 ? loopZ : loopX); i++)
            {
                int cx = (loopX == 1) ? xA : i;
                int cz = (loopX == 1) ? i : zA;
                int nx = (loopX == 1) ? xB : i;
                int nz = (loopX == 1) ? i : zB;

                if (PropagateSingleBlock(chunkB, nx, y, nz, chunkA, cx, y, cz, isSkyLight: false))
                {
                    queue.Enqueue(PackPos(cx, y, cz));
                    changedABlock = true;
                }
            }
        }
        if (changedABlock)
        {
            PropagateLight(chunkA, isSkyLight: false);
        }

        queue.Clear();
        bool changedBBlock = false;
        for (int y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (int i = 0; i < (loopX == 1 ? loopZ : loopX); i++)
            {
                int cx = (loopX == 1) ? xA : i;
                int cz = (loopX == 1) ? i : zA;
                int nx = (loopX == 1) ? xB : i;
                int nz = (loopX == 1) ? i : zB;

                if (PropagateSingleBlock(chunkA, cx, y, cz, chunkB, nx, y, nz, isSkyLight: false))
                {
                    queue.Enqueue(PackPos(nx, y, nz));
                    changedBBlock = true;
                }
            }
        }
        if (changedBBlock)
        {
            PropagateLight(chunkB, isSkyLight: false);
        }
    }

    private static bool PropagateSingleBlock(ChunkData sourceChunk, int sx, int sy, int sz, ChunkData targetChunk, int tx, int ty, int tz, bool isSkyLight)
    {
        int targetIndex = GetIndex(tx, ty, tz);
        BlockId targetBlock = targetChunk.GetBlock(tx, ty, tz);

        if (targetBlock.IsOpaque()) return false;

        int sourceIndex = GetIndex(sx, sy, sz);
        int sourceLight = isSkyLight ? GetSkyLight(sourceChunk, sourceIndex) : GetBlockLight(sourceChunk, sourceIndex);
        int targetLight = isSkyLight ? GetSkyLight(targetChunk, targetIndex) : GetBlockLight(targetChunk, targetIndex);

        int decay = Math.Max(1, (int)BlockRegistry.GetProperties(targetBlock).LightFilter);
        int newLight = sourceLight - decay;

        if (newLight > targetLight)
        {
            if (isSkyLight)
                SetSkyLight(targetChunk, targetIndex, newLight);
            else
                SetBlockLight(targetChunk, targetIndex, newLight);
            return true;
        }
        return false;
    }

    private static void InitializeSkyLight(ChunkData chunk)
    {
        var queue = LightQueue;
        queue.Clear();

        for (int x = 0; x < VoxelHelper.ChunkSideSize; x++)
        {
            for (int z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                bool hitSolid = false;
                for (int y = VoxelHelper.ChunkYSize - 1; y >= 0; y--)
                {
                    int index = GetIndex(x, y, z);
                    BlockId block = chunk.GetBlock(x, y, z);

                    if (!hitSolid)
                    {
                        if (block.IsOpaque())
                        {
                            hitSolid = true;
                            // The opaque block itself blocks light, so it stays 0 (or maybe gets some light if we want soft shadows?)
                            // In MC, the top surface of the opaque block gets light.
                            // But here we store light IN the voxel.
                            // If the block is opaque, it usually has 0 internal light.
                            // But for rendering the face, we use neighbor light (AO style) or the block's own light?
                            // Usually we use the light of the air block adjacent to the face.
                            // So setting the opaque block to 0 is fine.
                            
                            // However, we need to propagate into caves.
                            // So if we hit a solid, we stop setting 15.
                        }
                        else
                        {
                            // Set Sky Light to 15
                            SetSkyLight(chunk, index, MaxLight);
                            queue.Enqueue(PackPos(x, y, z));
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Scans the chunk for light-emitting blocks (torches, glowstone, lava, etc.)
    /// and initializes their block light values using BlockRegistry.
    /// </summary>
    private static void InitializeBlockLight(ChunkData chunk)
    {
        var queue = LightQueue;
        queue.Clear();

        for (int y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (int z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                for (int x = 0; x < VoxelHelper.ChunkSideSize; x++)
                {
                    BlockId block = chunk.GetBlock(x, y, z);
                    byte lightValue = BlockRegistry.GetLightValue(block);
                    
                    if (lightValue > 0)
                    {
                        int index = GetIndex(x, y, z);
                        SetBlockLight(chunk, index, lightValue);
                        queue.Enqueue(PackPos(x, y, z));
                    }
                }
            }
        }
    }

    public static void PropagateLight(ChunkData chunk, bool isSkyLight)
    {
        var queue = LightQueue;
        while (queue.Count > 0)
        {
            int packedPos = queue.Dequeue();
            UnpackPos(packedPos, out int x, out int y, out int z);

            int index = GetIndex(x, y, z);
            int currentLight = isSkyLight ? GetSkyLight(chunk, index) : GetBlockLight(chunk, index);

            if (currentLight <= 0) continue;

            // Check 6 neighbors
            CheckNeighbor(chunk, x + 1, y, z, currentLight, isSkyLight, queue);
            CheckNeighbor(chunk, x - 1, y, z, currentLight, isSkyLight, queue);
            CheckNeighbor(chunk, x, y + 1, z, currentLight, isSkyLight, queue);
            CheckNeighbor(chunk, x, y - 1, z, currentLight, isSkyLight, queue);
            CheckNeighbor(chunk, x, y, z + 1, currentLight, isSkyLight, queue);
            CheckNeighbor(chunk, x, y, z - 1, currentLight, isSkyLight, queue);
        }
    }

    private static void CheckNeighbor(ChunkData chunk, int x, int y, int z, int currentLight, bool isSkyLight, Queue<int> queue)
    {
        if (x < 0 || x >= VoxelHelper.ChunkSideSize ||
            y < 0 || y >= VoxelHelper.ChunkYSize ||
            z < 0 || z >= VoxelHelper.ChunkSideSize)
        {
            return; // Skip out of bounds (Phase 1: Intra-chunk only)
        }

        int index = GetIndex(x, y, z);
        BlockId block = chunk.GetBlock(x, y, z);

        if (block.IsOpaque()) return; // Light doesn't pass through opaque blocks

        int neighborLight = isSkyLight ? GetSkyLight(chunk, index) : GetBlockLight(chunk, index);
        
        // Decay
        int decay = Math.Max(1, (int)BlockRegistry.GetProperties(block).LightFilter);

        int newLight = currentLight - decay;

        if (newLight > neighborLight)
        {
            if (isSkyLight)
                SetSkyLight(chunk, index, newLight);
            else
                SetBlockLight(chunk, index, newLight);

            queue.Enqueue(PackPos(x, y, z));
        }
    }

    private static int GetIndex(int x, int y, int z)
    {
        return y * VoxelHelper.ChunkSideSizeSquare + z * VoxelHelper.ChunkSideSize + x;
    }

    private static int PackPos(int x, int y, int z)
    {
        return x | (y << 4) | (z << 13);
    }

    private static void UnpackPos(int packed, out int x, out int y, out int z)
    {
        x = packed & 0xF;
        y = (packed >> 4) & 0x1FF;
        z = (packed >> 13) & 0xF;
    }

    private static int GetSkyLight(ChunkData chunk, int index)
    {
        return chunk.LightData[index] & 0xF;
    }

    private static void SetSkyLight(ChunkData chunk, int index, int value)
    {
        chunk.LightData[index] = (byte)((chunk.LightData[index] & 0xF0) | (value & 0xF));
    }

    private static int GetBlockLight(ChunkData chunk, int index)
    {
        return (chunk.LightData[index] >> 4) & 0xF;
    }

    private static void SetBlockLight(ChunkData chunk, int index, int value)
    {
        chunk.LightData[index] = (byte)((chunk.LightData[index] & 0x0F) | ((value & 0xF) << 4));
    }
}

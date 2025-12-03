using System;
using System.Collections.Generic;
using OpenRender;

namespace SpyroGame.World;

/// <summary>
/// Delegate for getting chunk data by chunk index for cross-chunk light operations.
/// </summary>
public delegate ChunkData? ChunkDataProvider(int chunkIdx);

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
    
    // Secondary queue for light removal (stores position + old light value)
    [ThreadStatic]
    private static Queue<(int chunkIdx, int x, int y, int z, int lightLevel)>? t_removalQueue;
    
    private static Queue<(int chunkIdx, int x, int y, int z, int lightLevel)> RemovalQueue
    {
        get
        {
            t_removalQueue ??= new Queue<(int, int, int, int, int)>(4096);
            return t_removalQueue;
        }
    }
    
    // Queue for re-propagation after removal (positions that need to spread light again)
    [ThreadStatic]
    private static Queue<(int chunkIdx, int x, int y, int z)>? t_repropagateQueue;
    
    private static Queue<(int chunkIdx, int x, int y, int z)> RepropagateQueue
    {
        get
        {
            t_repropagateQueue ??= new Queue<(int, int, int, int)>(4096);
            return t_repropagateQueue;
        }
    }
    
    // Track which chunks were modified during cross-chunk operations
    [ThreadStatic]
    private static HashSet<int>? t_modifiedChunks;
    
    private static HashSet<int> ModifiedChunks
    {
        get
        {
            t_modifiedChunks ??= new HashSet<int>();
            return t_modifiedChunks;
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
    /// Removes block light from a position and propagates the removal through the affected volume.
    /// Works across chunk boundaries using the provided chunk data provider.
    /// Returns the set of chunk indices that were modified and need remeshing.
    /// </summary>
    /// <param name="sourceChunkIdx">The chunk index where the light source was removed.</param>
    /// <param name="localX">Local X coordinate within the chunk (0-15).</param>
    /// <param name="localY">Local Y coordinate (0-383).</param>
    /// <param name="localZ">Local Z coordinate within the chunk (0-15).</param>
    /// <param name="removedLightLevel">The light level of the removed source (e.g., 14 for torch).</param>
    /// <param name="getChunkData">Function to get chunk data by chunk index.</param>
    /// <returns>Set of chunk indices that were modified.</returns>
    public static HashSet<int> RemoveBlockLight(
        int sourceChunkIdx,
        int localX, int localY, int localZ,
        int removedLightLevel,
        ChunkDataProvider getChunkData)
    {
        var removalQueue = RemovalQueue;
        var repropagateQueue = RepropagateQueue;
        var modifiedChunks = ModifiedChunks;
        
        removalQueue.Clear();
        repropagateQueue.Clear();
        modifiedChunks.Clear();
        
        // Get the source chunk and clear the light at the source position
        var sourceChunk = getChunkData(sourceChunkIdx);
        if (sourceChunk == null)
            return modifiedChunks;
            
        int sourceIndex = GetIndex(localX, localY, localZ);
        SetBlockLight(sourceChunk, sourceIndex, 0);
        modifiedChunks.Add(sourceChunkIdx);
        
        // Seed the removal queue with the source position
        removalQueue.Enqueue((sourceChunkIdx, localX, localY, localZ, removedLightLevel));
        
        // Phase 1: BFS removal - clear light that came from the removed source
        while (removalQueue.Count > 0)
        {
            var (chunkIdx, x, y, z, lightLevel) = removalQueue.Dequeue();
            
            // Check all 6 neighbors
            CheckRemovalNeighbor(chunkIdx, x - 1, y, z, lightLevel, getChunkData, removalQueue, repropagateQueue, modifiedChunks);
            CheckRemovalNeighbor(chunkIdx, x + 1, y, z, lightLevel, getChunkData, removalQueue, repropagateQueue, modifiedChunks);
            CheckRemovalNeighbor(chunkIdx, x, y - 1, z, lightLevel, getChunkData, removalQueue, repropagateQueue, modifiedChunks);
            CheckRemovalNeighbor(chunkIdx, x, y + 1, z, lightLevel, getChunkData, removalQueue, repropagateQueue, modifiedChunks);
            CheckRemovalNeighbor(chunkIdx, x, y, z - 1, lightLevel, getChunkData, removalQueue, repropagateQueue, modifiedChunks);
            CheckRemovalNeighbor(chunkIdx, x, y, z + 1, lightLevel, getChunkData, removalQueue, repropagateQueue, modifiedChunks);
        }
        
        // Phase 2: Re-propagate from edges where we found other light sources
        while (repropagateQueue.Count > 0)
        {
            var (chunkIdx, x, y, z) = repropagateQueue.Dequeue();
            
            var chunk = getChunkData(chunkIdx);
            if (chunk == null) continue;
            
            var index = GetIndex(x, y, z);
            var currentLight = GetBlockLight(chunk, index);
            
            if (currentLight <= 0) continue;
            
            // Propagate to neighbors
            PropagateToNeighbor(chunkIdx, x - 1, y, z, currentLight, getChunkData, repropagateQueue, modifiedChunks);
            PropagateToNeighbor(chunkIdx, x + 1, y, z, currentLight, getChunkData, repropagateQueue, modifiedChunks);
            PropagateToNeighbor(chunkIdx, x, y - 1, z, currentLight, getChunkData, repropagateQueue, modifiedChunks);
            PropagateToNeighbor(chunkIdx, x, y + 1, z, currentLight, getChunkData, repropagateQueue, modifiedChunks);
            PropagateToNeighbor(chunkIdx, x, y, z - 1, currentLight, getChunkData, repropagateQueue, modifiedChunks);
            PropagateToNeighbor(chunkIdx, x, y, z + 1, currentLight, getChunkData, repropagateQueue, modifiedChunks);
        }
        
        return [.. modifiedChunks];
    }
    
    /// <summary>
    /// Check a neighbor during light removal BFS.
    /// </summary>
    private static void CheckRemovalNeighbor(
        int chunkIdx, int x, int y, int z, int parentLightLevel,
        ChunkDataProvider getChunkData,
        Queue<(int, int, int, int, int)> removalQueue,
        Queue<(int, int, int, int)> repropagateQueue,
        HashSet<int> modifiedChunks)
    {
        // Handle Y bounds
        if (y < 0 || y >= VoxelHelper.ChunkYSize)
            return;
            
        // Handle chunk boundary crossing
        var targetChunkIdx = chunkIdx;
        var targetX = x;
        var targetZ = z;
        
        if (x < 0)
        {
            var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
            if (chunkX == 0) return; // World edge
            targetChunkIdx = chunkIdx - 1;
            targetX = VoxelHelper.ChunkSideSize - 1;
        }
        else if (x >= VoxelHelper.ChunkSideSize)
        {
            var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
            if (chunkX >= VoxelHelper.WorldChunksXZ - 1) return; // World edge
            targetChunkIdx = chunkIdx + 1;
            targetX = 0;
        }
        
        if (z < 0)
        {
            var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;
            if (chunkZ == 0) return; // World edge
            targetChunkIdx = targetChunkIdx - VoxelHelper.WorldChunksXZ;
            targetZ = VoxelHelper.ChunkSideSize - 1;
        }
        else if (z >= VoxelHelper.ChunkSideSize)
        {
            var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;
            if (chunkZ >= VoxelHelper.WorldChunksXZ - 1) return; // World edge
            targetChunkIdx = targetChunkIdx + VoxelHelper.WorldChunksXZ;
            targetZ = 0;
        }
        
        var chunk = getChunkData(targetChunkIdx);
        if (chunk == null) return;

        var index = GetIndex(targetX, y, targetZ);
        var block = chunk.GetBlock(targetX, y, targetZ);
        
        if (block.IsOpaque()) return;

        var neighborLight = GetBlockLight(chunk, index);
        
        if (neighborLight == 0) return; // Already dark
        
        // Expected light if it came from the removed source
        var decay = Math.Max(1, (int)BlockRegistry.GetProperties(block).LightFilter);
        var expectedLight = parentLightLevel - decay;

        if (neighborLight != 0 && neighborLight < parentLightLevel)
        {
            // This light likely came from the removed source - remove it
            SetBlockLight(chunk, index, 0);
            modifiedChunks.Add(targetChunkIdx);
            removalQueue.Enqueue((targetChunkIdx, targetX, y, targetZ, neighborLight));
        }
        else if (neighborLight >= parentLightLevel)
        {
            // This block has light from another source - add to re-propagation queue
            repropagateQueue.Enqueue((targetChunkIdx, targetX, y, targetZ));
        }
    }
    
    /// <summary>
    /// Propagate light to a neighbor during re-propagation phase.
    /// </summary>
    private static void PropagateToNeighbor(
        int chunkIdx, int x, int y, int z, int parentLight,
        ChunkDataProvider getChunkData,
        Queue<(int, int, int, int)> repropagateQueue,
        HashSet<int> modifiedChunks)
    {
        // Handle Y bounds
        if (y < 0 || y >= VoxelHelper.ChunkYSize)
            return;
            
        // Handle chunk boundary crossing
        var targetChunkIdx = chunkIdx;
        var targetX = x;
        var targetZ = z;
        
        if (x < 0)
        {
            var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
            if (chunkX == 0) return;
            targetChunkIdx = chunkIdx - 1;
            targetX = VoxelHelper.ChunkSideSize - 1;
        }
        else if (x >= VoxelHelper.ChunkSideSize)
        {
            var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
            if (chunkX >= VoxelHelper.WorldChunksXZ - 1) return;
            targetChunkIdx = chunkIdx + 1;
            targetX = 0;
        }
        
        if (z < 0)
        {
            var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;
            if (chunkZ == 0) return;
            targetChunkIdx = targetChunkIdx - VoxelHelper.WorldChunksXZ;
            targetZ = VoxelHelper.ChunkSideSize - 1;
        }
        else if (z >= VoxelHelper.ChunkSideSize)
        {
            var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;
            if (chunkZ >= VoxelHelper.WorldChunksXZ - 1) return;
            targetChunkIdx = targetChunkIdx + VoxelHelper.WorldChunksXZ;
            targetZ = 0;
        }
        
        var chunk = getChunkData(targetChunkIdx);
        if (chunk == null) return;

        var index = GetIndex(targetX, y, targetZ);
        var block = chunk.GetBlock(targetX, y, targetZ);

        if (block.IsOpaque()) return;

        var neighborLight = GetBlockLight(chunk, index);
        var decay = Math.Max(1, (int)BlockRegistry.GetProperties(block).LightFilter);
        var newLight = parentLight - decay;

        if (newLight > neighborLight)
        {
            SetBlockLight(chunk, index, newLight);
            modifiedChunks.Add(targetChunkIdx);
            repropagateQueue.Enqueue((targetChunkIdx, targetX, y, targetZ));
        }
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
        var changedA = false;

        for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (var i = 0; i < (loopX == 1 ? loopZ : loopX); i++)
            {
                var cx = (loopX == 1) ? xA : i;
                var cz = (loopX == 1) ? i : zA;
                var nx = (loopX == 1) ? xB : i;
                var nz = (loopX == 1) ? i : zB;

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
        var changedB = false;

        for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (var i = 0; i < (loopX == 1 ? loopZ : loopX); i++)
            {
                var cx = (loopX == 1) ? xA : i;
                var cz = (loopX == 1) ? i : zA;
                var nx = (loopX == 1) ? xB : i;
                var nz = (loopX == 1) ? i : zB;

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
        var changedABlock = false;
        for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (var i = 0; i < (loopX == 1 ? loopZ : loopX); i++)
            {
                var cx = (loopX == 1) ? xA : i;
                var cz = (loopX == 1) ? i : zA;
                var nx = (loopX == 1) ? xB : i;
                var nz = (loopX == 1) ? i : zB;

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
        var changedBBlock = false;
        for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (var i = 0; i < (loopX == 1 ? loopZ : loopX); i++)
            {
                var cx = (loopX == 1) ? xA : i;
                var cz = (loopX == 1) ? i : zA;
                var nx = (loopX == 1) ? xB : i;
                var nz = (loopX == 1) ? i : zB;

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
        var targetIndex = GetIndex(tx, ty, tz);
        var targetBlock = targetChunk.GetBlock(tx, ty, tz);

        // Target must be non-opaque to receive light
        if (targetBlock.IsOpaque()) return false;

        var sourceIndex = GetIndex(sx, sy, sz);
        var sourceBlock = sourceChunk.GetBlock(sx, sy, sz);
        
        // Source must be non-opaque to transmit light (light doesn't pass through solid blocks)
        // Exception: emissive blocks can emit light even if "opaque"
        if (sourceBlock.IsOpaque() && !sourceBlock.IsEmissive())
            return false;

        var sourceLight = isSkyLight ? GetSkyLight(sourceChunk, sourceIndex) : GetBlockLight(sourceChunk, sourceIndex);
        var targetLight = isSkyLight ? GetSkyLight(targetChunk, targetIndex) : GetBlockLight(targetChunk, targetIndex);

        var decay = Math.Max(1, (int)BlockRegistry.GetProperties(targetBlock).LightFilter);
        var newLight = sourceLight - decay;

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

        for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                var hitSolid = false;
                for (var y = VoxelHelper.ChunkYSize - 1; y >= 0; y--)
                {
                    var index = GetIndex(x, y, z);
                    var block = chunk.GetBlock(x, y, z);

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

        for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
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

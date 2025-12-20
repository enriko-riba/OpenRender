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
    // Initial queue capacity sized for chunk volume: 16×384×16 ÷ 8 ≈ 12k
    // This prevents reallocation during BFS for large cave systems
    private const int QueueInitialCapacity = 12288;

    [ThreadStatic]
    private static Queue<int>? t_lightQueue;

    private static Queue<int> LightQueue
    {
        get
        {
            t_lightQueue ??= new Queue<int>(QueueInitialCapacity);
            return t_lightQueue;
        }
    }

    private static void BuildSkyVisibilityMap(ChunkData chunk, byte[] skyVisibility)
    {
        for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                var columnIndex = z * VoxelHelper.ChunkSideSize + x;
                var surfaceHeight = chunk.SurfaceHeights[columnIndex];
                
                // Optimization: Start from surface height + light radius instead of top of world
                var startY = surfaceHeight > 0 
                    ? Math.Min(surfaceHeight + MaxLight, VoxelHelper.ChunkYSize - 1) 
                    : VoxelHelper.ChunkYSize - 1;
                    
                var currentLight = MaxLight;
                for (var y = startY; y >= 0; y--)
                {
                    var index = GetIndex(x, y, z);
                    // Defensive: chunk voxel/palette data can be temporarily malformed during streaming/generation.
                    // Treat invalid reads as Air so boundary propagation can't crash background meshing.
                    if (!TryGetBlockSafe(chunk, x, y, z, out var block))
                    {
                        skyVisibility[index] = (byte)(currentLight > 0 ? 1 : 0);
                        continue;
                    }

                    if (block.IsOpaque())
                    {
                        currentLight = 0;
                        skyVisibility[index] = 0;
                        continue;
                    }

                    var filter = BlockRegistry.GetLightFilter(block);
                    if (filter > 0 && currentLight > 0)
                    {
                        currentLight = Math.Max(0, currentLight - filter);
                    }

                    skyVisibility[index] = (byte)(currentLight > 0 ? 1 : 0);
                }
            }
        }
    }
    
    // Secondary queue for light removal (stores position + old light value)
    [ThreadStatic]
    private static Queue<(int chunkIdx, int x, int y, int z, int lightLevel)>? t_removalQueue;
    
    private static Queue<(int chunkIdx, int x, int y, int z, int lightLevel)> RemovalQueue
    {
        get
        {
            t_removalQueue ??= new Queue<(int, int, int, int, int)>(QueueInitialCapacity);
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
            t_repropagateQueue ??= new Queue<(int, int, int, int)>(QueueInitialCapacity);
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
            t_modifiedChunks ??= [];
            return t_modifiedChunks;
        }
    }

    [ThreadStatic]
    private static byte[]? t_skyVisibility;

    private static byte[] RentSkyVisibilityBuffer()
    {
        var buffer = t_skyVisibility;
        if (buffer == null || buffer.Length != VoxelHelper.ChunkVoxelCount)
        {
            buffer = new byte[VoxelHelper.ChunkVoxelCount];
            t_skyVisibility = buffer;
        }
        else
        {
            Array.Clear(buffer, 0, buffer.Length);
        }

        return buffer;
    }

    public static void CalculateLighting(ChunkData chunk)
    {
        // 1. Clear Light Data
        Array.Clear(chunk.LightData, 0, chunk.LightData.Length);

        // 2. Initialize Sky Light (track direct-sky visibility)
        var skyVisibility = RentSkyVisibilityBuffer();
        InitializeSkyLight(chunk, skyVisibility);

        // 3. Propagate Sky Light (respect visibility map)
        PropagateLight(chunk, isSkyLight: true, skyVisibility);

        // 4. Initialize Block Light (scan for emissive blocks like Torch, Glowstone, Lava)
        InitializeBlockLight(chunk);
        
        // 5. Propagate Block Light
        PropagateLight(chunk, isSkyLight: false);
    }

    /// <summary>
    /// Adds block light from a newly placed light source and propagates it across chunk boundaries.
    /// This is used when placing torches, glowstone, etc.
    /// </summary>
    /// <param name="sourceChunkIdx">The chunk index where the light source was placed.</param>
    /// <param name="localX">Local X coordinate within the chunk (0-15).</param>
    /// <param name="localY">Local Y coordinate (0-383).</param>
    /// <param name="localZ">Local Z coordinate within the chunk (0-15).</param>
    /// <param name="lightLevel">The light level of the source (e.g., 14 for torch).</param>
    /// <param name="getChunkData">Function to get chunk data by chunk index.</param>
    /// <returns>Set of chunk indices that were modified.</returns>
    public static HashSet<int> AddBlockLight(
        int sourceChunkIdx,
        int localX, int localY, int localZ,
        int lightLevel,
        ChunkDataProvider getChunkData)
    {
        var propagateQueue = RepropagateQueue;  // Reuse the queue
        var modifiedChunks = ModifiedChunks;
        
        propagateQueue.Clear();
        modifiedChunks.Clear();
        
        if (lightLevel <= 0) return modifiedChunks;
        
        // Get the source chunk and set the light at the source position
        var sourceChunk = getChunkData(sourceChunkIdx);
        if (sourceChunk == null)
            return modifiedChunks;
            
        var sourceIndex = GetIndex(localX, localY, localZ);
        var oldLight = sourceChunk.LightData[sourceIndex];
        SetBlockLight(sourceChunk, sourceIndex, lightLevel);
        var newLight = sourceChunk.LightData[sourceIndex];
        Log.Debug($"AddBlockLight: source chunk={sourceChunkIdx} pos=({localX},{localY},{localZ}) idx={sourceIndex} lightLevel={lightLevel} oldLightData=0x{oldLight:X2} newLightData=0x{newLight:X2}");
        modifiedChunks.Add(sourceChunkIdx);
        
        // Seed the propagation queue with the source position
        propagateQueue.Enqueue((sourceChunkIdx, localX, localY, localZ));
        
        // BFS propagation with cross-chunk support
        while (propagateQueue.Count > 0)
        {
            var (chunkIdx, x, y, z) = propagateQueue.Dequeue();
            
            var chunk = getChunkData(chunkIdx);
            if (chunk == null) continue;
            
            var index = GetIndex(x, y, z);
            var currentLight = GetBlockLight(chunk, index);
            
            if (currentLight <= 1) continue;  // Can't propagate further
            
            // Propagate to all 6 neighbors
            PropagateToNeighbor(chunkIdx, x - 1, y, z, currentLight, getChunkData, propagateQueue, modifiedChunks);
            PropagateToNeighbor(chunkIdx, x + 1, y, z, currentLight, getChunkData, propagateQueue, modifiedChunks);
            PropagateToNeighbor(chunkIdx, x, y - 1, z, currentLight, getChunkData, propagateQueue, modifiedChunks);
            PropagateToNeighbor(chunkIdx, x, y + 1, z, currentLight, getChunkData, propagateQueue, modifiedChunks);
            PropagateToNeighbor(chunkIdx, x, y, z - 1, currentLight, getChunkData, propagateQueue, modifiedChunks);
            PropagateToNeighbor(chunkIdx, x, y, z + 1, currentLight, getChunkData, propagateQueue, modifiedChunks);
        }
        
        // Debug: Verify light values after propagation
        if (sourceChunk != null)
        {
            var srcLight = sourceChunk.LightData[sourceIndex];
            var srcBlockLight = (srcLight >> 4) & 0xF;
            Log.Info($"AddBlockLight DONE: source pos=({localX},{localY},{localZ}) finalLight=0x{srcLight:X2} blockLight={srcBlockLight}");
            
            // Check a few neighbors
            void LogNeighbor(int dx, int dy, int dz)
            {
                var nx = localX + dx;
                var ny = localY + dy;
                var nz = localZ + dz;
                if (nx >= 0 && nx < VoxelHelper.ChunkSideSize && 
                    ny >= 0 && ny < VoxelHelper.ChunkYSize &&
                    nz >= 0 && nz < VoxelHelper.ChunkSideSize)
                {
                    var nIdx = GetIndex(nx, ny, nz);
                    var nLight = sourceChunk.LightData[nIdx];
                    Log.Info($"  Neighbor ({nx},{ny},{nz}): light=0x{nLight:X2} block={(nLight >> 4) & 0xF} sky={nLight & 0xF}");
                }
            }
            LogNeighbor(-1, 0, 0);
            LogNeighbor(1, 0, 0);
            LogNeighbor(0, -1, 0);
            LogNeighbor(0, 1, 0);
            LogNeighbor(0, 0, -1);
        }
        
        return modifiedChunks;
    }

    /// <summary>
    /// Recalculates lighting when an opaque block is placed.
    /// This finds all light sources (block lights and sky light columns) within the max light radius (15),
    /// clears their light, and re-propagates from scratch.
    /// </summary>
    /// <param name="placedChunkIdx">The chunk where the block was placed.</param>
    /// <param name="localX">Local X within chunk (0-15).</param>
    /// <param name="localY">Local Y (0-383).</param>
    /// <param name="localZ">Local Z within chunk (0-15).</param>
    /// <param name="getChunkData">Function to get chunk data by index.</param>
    /// <returns>Set of chunk indices that were modified and need remeshing.</returns>
    public static HashSet<int> RecalculateLightingAroundBlock(
        int placedChunkIdx,
        int localX, int localY, int localZ,
        ChunkDataProvider getChunkData)
    {
        var modifiedChunks = ModifiedChunks;
        modifiedChunks.Clear();
        
        var placedChunk = getChunkData(placedChunkIdx);
        if (placedChunk == null)
            return modifiedChunks;
        
        // Convert to world coordinates for radius search
        var placedChunkX = placedChunkIdx % VoxelHelper.WorldChunksXZ;
        var placedChunkZ = placedChunkIdx / VoxelHelper.WorldChunksXZ;
        var worldX = placedChunkX * VoxelHelper.ChunkSideSize + localX;
        var worldZ = placedChunkZ * VoxelHelper.ChunkSideSize + localZ;
        
        // Find all chunks that could contain light sources affecting this position
        // Light radius is 15, so we need to check chunks within ceil(15/16) = 1 chunk in each direction
        var affectedChunks = new List<(int chunkIdx, ChunkData data)>();
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var cx = placedChunkX + dx;
                var cz = placedChunkZ + dz;
                if (cx < 0 || cx >= VoxelHelper.WorldChunksXZ || cz < 0 || cz >= VoxelHelper.WorldChunksXZ)
                    continue;
                    
                var idx = cz * VoxelHelper.WorldChunksXZ + cx;
                var data = getChunkData(idx);
                if (data != null)
                {
                    affectedChunks.Add((idx, data));
                }
            }
        }
        
        // SIMPLIFIED APPROACH: Fully recalculate lighting for all affected chunks
        // This is more reliable than incremental updates and handles cross-chunk propagation correctly
        foreach (var (chunkIdx, chunkData) in affectedChunks)
        {
            CalculateLighting(chunkData);
            modifiedChunks.Add(chunkIdx);
        }
        
        // After recalculating each chunk independently, propagate light across chunk boundaries
        // This ensures light flows correctly between chunks (e.g., doorway spanning two chunks)
        // IMPORTANT: We must also recalculate neighbor chunks that receive propagated light,
        // otherwise stale light from neighbors can flow back into sealed areas.
        var neighborsToRecalculate = new HashSet<int>();
        foreach (var (chunkIdx, chunkData) in affectedChunks)
        {
            var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
            var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;
            
            // Identify neighbors that need recalculation
            (int dx, int dz)[] neighbors = [(-1, 0), (1, 0), (0, -1), (0, 1)];
            foreach (var (dx, dz) in neighbors)
            {
                var nx = chunkX + dx;
                var nz = chunkZ + dz;
                if (nx < 0 || nx >= VoxelHelper.WorldChunksXZ || nz < 0 || nz >= VoxelHelper.WorldChunksXZ)
                    continue;
                    
                var neighborIdx = nz * VoxelHelper.WorldChunksXZ + nx;
                
                // Skip if already in affected chunks (already recalculated)
                if (affectedChunks.Exists(c => c.chunkIdx == neighborIdx))
                    continue;
                    
                var neighborData = getChunkData(neighborIdx);
                if (neighborData != null)
                {
                    neighborsToRecalculate.Add(neighborIdx);
                }
            }
        }
        
        // Recalculate lighting for all identified neighbor chunks
        // This ensures they don't have stale light that could propagate back
        foreach (var neighborIdx in neighborsToRecalculate)
        {
            var neighborData = getChunkData(neighborIdx);
            if (neighborData != null)
            {
                CalculateLighting(neighborData);
                modifiedChunks.Add(neighborIdx);
            }
        }
        
        // Now propagate light between all affected chunks (including the newly recalculated neighbors)
        var allChunksToPropagate = new List<(int chunkIdx, ChunkData data)>(affectedChunks);
        foreach (var neighborIdx in neighborsToRecalculate)
        {
            var neighborData = getChunkData(neighborIdx);
            if (neighborData != null)
            {
                allChunksToPropagate.Add((neighborIdx, neighborData));
            }
        }
        
        foreach (var (chunkIdx, chunkData) in allChunksToPropagate)
        {
            var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
            var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;
            
            // Propagate to each neighbor
            (int dx, int dz)[] neighbors = [(-1, 0), (1, 0), (0, -1), (0, 1)];
            foreach (var (dx, dz) in neighbors)
            {
                var nx = chunkX + dx;
                var nz = chunkZ + dz;
                if (nx < 0 || nx >= VoxelHelper.WorldChunksXZ || nz < 0 || nz >= VoxelHelper.WorldChunksXZ)
                    continue;
                    
                var neighborIdx = nz * VoxelHelper.WorldChunksXZ + nx;
                var neighborData = getChunkData(neighborIdx);
                if (neighborData != null)
                {
                    PropagateNeighborLight(chunkData, neighborData, dx, dz);
                    modifiedChunks.Add(neighborIdx);
                }
            }
        }
        
        return modifiedChunks;
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
            
        var sourceIndex = GetIndex(localX, localY, localZ);
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
        
        // Return the static modified chunks directly - callers must iterate immediately
        // or copy the result if they need to store it across calls.
        return modifiedChunks;
    }
    
    /// <summary>
    /// Removes sky light from a position and propagates the removal through the affected volume.
    /// Used when placing an opaque block that blocks sky light from flowing through.
    /// Works across chunk boundaries using the provided chunk data provider.
    /// Returns the set of chunk indices that were modified and need remeshing.
    /// </summary>
    /// <param name="sourceChunkIdx">The chunk index where the block was placed.</param>
    /// <param name="localX">Local X coordinate within the chunk (0-15).</param>
    /// <param name="localY">Local Y coordinate (0-383).</param>
    /// <param name="localZ">Local Z coordinate within the chunk (0-15).</param>
    /// <param name="blockedLightLevel">The sky light level that was at this position before blocking.</param>
    /// <param name="getChunkData">Function to get chunk data by chunk index.</param>
    /// <returns>Set of chunk indices that were modified.</returns>
    public static HashSet<int> RemoveSkyLight(
        int sourceChunkIdx,
        int localX, int localY, int localZ,
        int blockedLightLevel,
        ChunkDataProvider getChunkData)
    {
        var removalQueue = RemovalQueue;
        var repropagateQueue = RepropagateQueue;
        var modifiedChunks = ModifiedChunks;
        
        removalQueue.Clear();
        repropagateQueue.Clear();
        modifiedChunks.Clear();
        
        // Get the source chunk - note: the block is already placed, so we don't set light here
        var sourceChunk = getChunkData(sourceChunkIdx);
        if (sourceChunk == null)
            return modifiedChunks;
        
        modifiedChunks.Add(sourceChunkIdx);
        
        // Seed the removal queue with the source position
        // The opaque block is already placed, so we start BFS from this position with the OLD light level
        removalQueue.Enqueue((sourceChunkIdx, localX, localY, localZ, blockedLightLevel));
        
        // Phase 1: BFS removal - clear sky light that was flowing through the now-blocked position
        while (removalQueue.Count > 0)
        {
            var (chunkIdx, x, y, z, lightLevel) = removalQueue.Dequeue();
            
            // Check all 6 neighbors
            CheckSkyLightRemovalNeighbor(chunkIdx, x - 1, y, z, lightLevel, getChunkData, removalQueue, repropagateQueue, modifiedChunks);
            CheckSkyLightRemovalNeighbor(chunkIdx, x + 1, y, z, lightLevel, getChunkData, removalQueue, repropagateQueue, modifiedChunks);
            CheckSkyLightRemovalNeighbor(chunkIdx, x, y - 1, z, lightLevel, getChunkData, removalQueue, repropagateQueue, modifiedChunks);
            CheckSkyLightRemovalNeighbor(chunkIdx, x, y + 1, z, lightLevel, getChunkData, removalQueue, repropagateQueue, modifiedChunks);
            CheckSkyLightRemovalNeighbor(chunkIdx, x, y, z - 1, lightLevel, getChunkData, removalQueue, repropagateQueue, modifiedChunks);
            CheckSkyLightRemovalNeighbor(chunkIdx, x, y, z + 1, lightLevel, getChunkData, removalQueue, repropagateQueue, modifiedChunks);
        }
        
        // Phase 2: Re-propagate from edges where we found other sky light sources
        while (repropagateQueue.Count > 0)
        {
            var (chunkIdx, x, y, z) = repropagateQueue.Dequeue();
            
            var chunk = getChunkData(chunkIdx);
            if (chunk == null) continue;
            
            var index = GetIndex(x, y, z);
            var currentLight = GetSkyLight(chunk, index);
            
            if (currentLight <= 0) continue;
            
            // Propagate to neighbors
            PropagateSkyLightToNeighbor(chunkIdx, x - 1, y, z, currentLight, getChunkData, repropagateQueue, modifiedChunks);
            PropagateSkyLightToNeighbor(chunkIdx, x + 1, y, z, currentLight, getChunkData, repropagateQueue, modifiedChunks);
            PropagateSkyLightToNeighbor(chunkIdx, x, y - 1, z, currentLight, getChunkData, repropagateQueue, modifiedChunks);
            PropagateSkyLightToNeighbor(chunkIdx, x, y + 1, z, currentLight, getChunkData, repropagateQueue, modifiedChunks);
            PropagateSkyLightToNeighbor(chunkIdx, x, y, z - 1, currentLight, getChunkData, repropagateQueue, modifiedChunks);
            PropagateSkyLightToNeighbor(chunkIdx, x, y, z + 1, currentLight, getChunkData, repropagateQueue, modifiedChunks);
        }
        
        // Return the static modified chunks directly - callers must iterate immediately
        // or copy the result if they need to store it across calls.
        return modifiedChunks;
    }
    
    /// <summary>
    /// Check a neighbor during sky light removal BFS.
    /// </summary>
    private static void CheckSkyLightRemovalNeighbor(
        int chunkIdx, int x, int y, int z, int parentLightLevel,
        ChunkDataProvider getChunkData,
        Queue<(int, int, int, int, int)> removalQueue,
        Queue<(int, int, int, int)> repropagateQueue,
        HashSet<int> modifiedChunks)
    {
        // Handle Y bounds
        if (y is < 0 or >= VoxelHelper.ChunkYSize)
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
            targetChunkIdx -= VoxelHelper.WorldChunksXZ;
            targetZ = VoxelHelper.ChunkSideSize - 1;
        }
        else if (z >= VoxelHelper.ChunkSideSize)
        {
            var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;
            if (chunkZ >= VoxelHelper.WorldChunksXZ - 1) return; // World edge
            targetChunkIdx += VoxelHelper.WorldChunksXZ;
            targetZ = 0;
        }
        
        var chunk = getChunkData(targetChunkIdx);
        if (chunk == null) return;

        var index = GetIndex(targetX, y, targetZ);
        if (!TryGetBlockSafe(chunk, targetX, y, targetZ, out var block))
        {
            block = BlockId.Air;
        }
        
        if (block.IsOpaque()) return;

        var neighborLight = GetSkyLight(chunk, index);
        
        if (neighborLight == 0) return; // Already dark

        if (neighborLight != 0 && neighborLight < parentLightLevel)
        {
            // This light likely came from the blocked source - remove it
            SetSkyLight(chunk, index, 0);
            modifiedChunks.Add(targetChunkIdx);
            removalQueue.Enqueue((targetChunkIdx, targetX, y, targetZ, neighborLight));
        }
        else if (neighborLight >= parentLightLevel)
        {
            // This block has light from another source (direct sky access) - add to re-propagation queue
            repropagateQueue.Enqueue((targetChunkIdx, targetX, y, targetZ));
        }
    }
    
    /// <summary>
    /// Propagate sky light to a neighbor during re-propagation phase.
    /// </summary>
    private static void PropagateSkyLightToNeighbor(
        int chunkIdx, int x, int y, int z, int parentLight,
        ChunkDataProvider getChunkData,
        Queue<(int, int, int, int)> repropagateQueue,
        HashSet<int> modifiedChunks)
    {
        // Handle Y bounds
        if (y is < 0 or >= VoxelHelper.ChunkYSize)
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
            targetChunkIdx -= VoxelHelper.WorldChunksXZ;
            targetZ = VoxelHelper.ChunkSideSize - 1;
        }
        else if (z >= VoxelHelper.ChunkSideSize)
        {
            var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;
            if (chunkZ >= VoxelHelper.WorldChunksXZ - 1) return;
            targetChunkIdx += VoxelHelper.WorldChunksXZ;
            targetZ = 0;
        }
        
        var chunk = getChunkData(targetChunkIdx);
        if (chunk == null) return;

        var index = GetIndex(targetX, y, targetZ);
        if (!TryGetBlockSafe(chunk, targetX, y, targetZ, out var block))
        {
            block = BlockId.Air;
        }

        if (block.IsOpaque()) return;

        var neighborLight = GetSkyLight(chunk, index);
        var decay = BlockRegistry.GetLightDecay(block);
        var newLight = parentLight - decay;

        if (newLight > neighborLight)
        {
            SetSkyLight(chunk, index, newLight);
            modifiedChunks.Add(targetChunkIdx);
            repropagateQueue.Enqueue((targetChunkIdx, targetX, y, targetZ));
        }
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
        if (y is < 0 or >= VoxelHelper.ChunkYSize)
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
            targetChunkIdx -= VoxelHelper.WorldChunksXZ;
            targetZ = VoxelHelper.ChunkSideSize - 1;
        }
        else if (z >= VoxelHelper.ChunkSideSize)
        {
            var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;
            if (chunkZ >= VoxelHelper.WorldChunksXZ - 1) return; // World edge
            targetChunkIdx += VoxelHelper.WorldChunksXZ;
            targetZ = 0;
        }
        
        var chunk = getChunkData(targetChunkIdx);
        if (chunk == null) return;

        var index = GetIndex(targetX, y, targetZ);
        if (!TryGetBlockSafe(chunk, targetX, y, targetZ, out var block))
        {
            block = BlockId.Air;
        }
        
        if (block.IsOpaque()) return;

        var neighborLight = GetBlockLight(chunk, index);
        
        if (neighborLight == 0) return; // Already dark
        
        // Expected light if it came from the removed source
        //var decay = Math.Max(1, (int)BlockRegistry.GetProperties(block).LightFilter);
        //var expectedLight = parentLightLevel - decay;

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
        if (y is < 0 or >= VoxelHelper.ChunkYSize)
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
            targetChunkIdx -= VoxelHelper.WorldChunksXZ;
            targetZ = VoxelHelper.ChunkSideSize - 1;
        }
        else if (z >= VoxelHelper.ChunkSideSize)
        {
            var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;
            if (chunkZ >= VoxelHelper.WorldChunksXZ - 1) return;
            targetChunkIdx += VoxelHelper.WorldChunksXZ;
            targetZ = 0;
        }
        
        var chunk = getChunkData(targetChunkIdx);
        if (chunk == null) return;

        var index = GetIndex(targetX, y, targetZ);
        if (!TryGetBlockSafe(chunk, targetX, y, targetZ, out var block))
        {
            block = BlockId.Air;
        }

        if (block.IsOpaque()) return;

        var neighborLight = GetBlockLight(chunk, index);
        var decay = BlockRegistry.GetLightDecay(block);
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
            var skyVisibility = RentSkyVisibilityBuffer();
            BuildSkyVisibilityMap(chunkA, skyVisibility);
            PropagateLight(chunkA, isSkyLight: true, skyVisibility);
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
            var skyVisibility = RentSkyVisibilityBuffer();
            BuildSkyVisibilityMap(chunkB, skyVisibility);
            PropagateLight(chunkB, isSkyLight: true, skyVisibility);
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
        if (!ChunkData.IsWithinBounds(sx, sy, sz) || !ChunkData.IsWithinBounds(tx, ty, tz))
        {
            return false;
        }

        var targetIndex = GetIndex(tx, ty, tz);
        if (!TryGetBlockSafe(targetChunk, tx, ty, tz, out var targetBlock))
        {
            return false;
        }

        if (!TryGetLightIndexSafe(targetChunk, targetIndex))
        {
            return false;
        }

        // Target must be non-opaque to receive light
        if (targetBlock.IsOpaque()) return false;

        var sourceIndex = GetIndex(sx, sy, sz);
        if (!TryGetBlockSafe(sourceChunk, sx, sy, sz, out var sourceBlock))
        {
            return false;
        }

        if (!TryGetLightIndexSafe(sourceChunk, sourceIndex))
        {
            return false;
        }
        
        // Source must be non-opaque to transmit light (light doesn't pass through solid blocks)
        // Exception: emissive blocks can emit light even if "opaque"
        if (sourceBlock.IsOpaque() && !sourceBlock.IsEmissive())
            return false;

        var sourceLight = isSkyLight ? GetSkyLight(sourceChunk, sourceIndex) : GetBlockLight(sourceChunk, sourceIndex);
        var targetLight = isSkyLight ? GetSkyLight(targetChunk, targetIndex) : GetBlockLight(targetChunk, targetIndex);

        var decay = BlockRegistry.GetLightDecay(targetBlock);
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

    private static bool TryGetLightIndexSafe(ChunkData chunk, int index)
    {
        var light = chunk.LightData;
        return light is not null && (uint)index < (uint)light.Length;
    }

    private static bool TryGetBlockSafe(ChunkData chunk, int x, int y, int z, out BlockId block)
    {
        if (!ChunkData.IsWithinBounds(x, y, z))
        {
            block = BlockId.Air;
            return false;
        }

        var voxels = chunk.VoxelData;
        if (voxels is null)
        {
            block = BlockId.Air;
            return false;
        }

        var idx = GetIndex(x, y, z);
        if ((uint)idx >= (uint)voxels.Length)
        {
            block = BlockId.Air;
            return false;
        }

        var paletteIndex = voxels[idx];
        var palette = chunk.Palette;
        if (palette is null || (uint)paletteIndex >= (uint)palette.Length)
        {
            block = BlockId.Air;
            return false;
        }

        block = palette[paletteIndex];
        return true;
    }

    /// <summary>
    /// Minecraft-style sky light initialization:
    /// 1. Iterate downward from build height limit
    /// 2. Air blocks that can "see" the top of the world get sky light 15 (NO DECAY going down through air)
    /// 3. Translucent blocks (water, leaves, ice) apply their light filter even during vertical descent
    /// 4. Opaque blocks stop downward propagation in that column
    /// </summary>
    private static void InitializeSkyLight(ChunkData chunk, byte[] skyVisibility)
    {
        var queue = LightQueue;
        queue.Clear();

        for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                var columnIndex = z * VoxelHelper.ChunkSideSize + x;
                var surfaceHeight = chunk.SurfaceHeights[columnIndex];
                
                // Performance optimization: Use surface height to start from a reasonable position
                // surfaceHeight > 0 means it was computed by terrain generation
                // surfaceHeight = 0 could be uninitialized OR actual surface at y=0, so start from top to be safe
                // surfaceHeight < 0 means column is entirely air (start from top)
                var startY = surfaceHeight > 0 
                    ? Math.Min(surfaceHeight + MaxLight, VoxelHelper.ChunkYSize - 1) 
                    : VoxelHelper.ChunkYSize - 1;
                
                // Minecraft-style: direct sky column
                // - Air: level 15 with NO decay (sunlight goes straight through)
                // - Translucent (water, ice, leaves): apply light filter during descent
                // - Opaque: blocks completely, column below loses direct sky access
                var currentLight = MaxLight;
                for (var y = startY; y >= 0; y--)
                {
                    var index = GetIndex(x, y, z);
                    // Same defensive read as BuildSkyVisibilityMap.
                    if (!TryGetBlockSafe(chunk, x, y, z, out var block))
                    {
                        // Invalid voxel/palette => treat as Air.
                        skyVisibility[index] = (byte)(currentLight > 0 ? 1 : 0);
                        if (currentLight > 0)
                        {
                            SetSkyLight(chunk, index, currentLight);
                            queue.Enqueue(PackPos(x, y, z));
                        }
                        continue;
                    }

                    if (block.IsOpaque())
                    {
                        // Opaque block completely blocks sky light - column below is no longer direct sky
                        currentLight = 0;
                        skyVisibility[index] = 0;
                    }
                    else
                    {
                        // Apply light filter for translucent blocks (water, ice, leaves, etc.)
                        // Air has filter=0 so no decay; water has filter=2 so decays
                        var filter = BlockRegistry.GetLightFilter(block);
                        if (filter > 0 && currentLight > 0)
                        {
                            currentLight = Math.Max(0, currentLight - filter);
                        }

                        if (currentLight > 0)
                        {
                            SetSkyLight(chunk, index, currentLight);
                            // Mark as direct-sky if we still have max light (pure air column)
                            skyVisibility[index] = (byte)(currentLight == MaxLight ? 1 : 0);
                            
                            // Queue for BFS lateral propagation
                            queue.Enqueue(PackPos(x, y, z));
                        }
                        else
                        {
                            skyVisibility[index] = 0;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Scans the chunk for light-emitting blocks (torches, glowstone, lava, etc.)
    /// and initializes their block light values using BlockRegistry.
    /// Performance optimized: Uses surface heights to only scan columns up to their surface.
    /// </summary>
    private static void InitializeBlockLight(ChunkData chunk)
    {
        var queue = LightQueue;
        queue.Clear();

        // Optimization: Scan column by column using surface heights to limit Y iteration
        // Light sources (torches, lava) only exist at or below the surface
        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        {
            for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
            {
                var columnIndex = z * VoxelHelper.ChunkSideSize + x;
                var surfaceHeight = chunk.SurfaceHeights[columnIndex];
                
                // Skip empty columns or scan up to surface + small margin (vegetation may have light sources)
                if (surfaceHeight < 0) continue;
                var maxY = Math.Min(surfaceHeight + 2, VoxelHelper.ChunkYSize);
                
                for (var y = 0; y < maxY; y++)
                {
                    if (!TryGetBlockSafe(chunk, x, y, z, out var block))
                    {
                        continue;
                    }
                    var lightValue = BlockRegistry.GetLightValue(block);

                    if (lightValue > 0)
                    {
                        var index = GetIndex(x, y, z);
                        SetBlockLight(chunk, index, lightValue);
                        queue.Enqueue(PackPos(x, y, z));
                    }
                }
            }
        }
    }

    public static void PropagateLight(ChunkData chunk, bool isSkyLight, byte[]? skyVisibility = null)
    {
        var queue = LightQueue;

        if (isSkyLight)
        {
            if (skyVisibility == null)
            {
                skyVisibility = RentSkyVisibilityBuffer();
                BuildSkyVisibilityMap(chunk, skyVisibility);
            }
        }

        while (queue.Count > 0)
        {
            var packedPos = queue.Dequeue();
            UnpackPos(packedPos, out var x, out var y, out var z);

            var index = GetIndex(x, y, z);
            var currentLight = isSkyLight ? GetSkyLight(chunk, index) : GetBlockLight(chunk, index);

            if (currentLight <= 0) continue;

            // Check 6 neighbors - Minecraft-style flood fill
            CheckNeighbor(chunk, x + 1, y, z, currentLight, isSkyLight, queue, skyVisibility, index, isVerticalDown: false);
            CheckNeighbor(chunk, x - 1, y, z, currentLight, isSkyLight, queue, skyVisibility, index, isVerticalDown: false);
            CheckNeighbor(chunk, x, y + 1, z, currentLight, isSkyLight, queue, skyVisibility, index, isVerticalDown: false);
            CheckNeighbor(chunk, x, y - 1, z, currentLight, isSkyLight, queue, skyVisibility, index, isVerticalDown: true);
            CheckNeighbor(chunk, x, y, z + 1, currentLight, isSkyLight, queue, skyVisibility, index, isVerticalDown: false);
            CheckNeighbor(chunk, x, y, z - 1, currentLight, isSkyLight, queue, skyVisibility, index, isVerticalDown: false);
        }
    }

    /// <summary>
    /// Minecraft-style light propagation rules:
    /// - Sky light going DOWN from a pure-air direct-sky column (level 15): NO DECAY (stays 15)
    /// - Sky light going DOWN through translucent blocks: applies their light filter
    /// - Sky light going HORIZONTAL or UP: normal decay (1 per block, or more for light filters)
    /// - Block light: always decays by 1 (or more for light filters)
    /// </summary>
    private static void CheckNeighbor(
        ChunkData chunk,
        int x,
        int y,
        int z,
        int currentLight,
        bool isSkyLight,
        Queue<int> queue,
        byte[]? skyVisibility,
        int sourceIndex,
        bool isVerticalDown)
    {
        if (x < 0 || x >= VoxelHelper.ChunkSideSize ||
            y < 0 || y >= VoxelHelper.ChunkYSize ||
            z < 0 || z >= VoxelHelper.ChunkSideSize)
        {
            return; // Skip out of bounds (Phase 1: Intra-chunk only)
        }

        var index = GetIndex(x, y, z);
        if (!TryGetBlockSafe(chunk, x, y, z, out var block))
        {
            block = BlockId.Air;
        }

        if (block.IsOpaque()) return; // Light doesn't pass through opaque blocks

        var neighborLight = isSkyLight ? GetSkyLight(chunk, index) : GetBlockLight(chunk, index);
        
        // Minecraft-style decay rules for sky light:
        // - Downward from pure-air direct-sky column (level 15 source, skyVisibility=1): 
        //   - Through AIR: NO decay (continue at 15)
        //   - Through translucent: apply filter
        // - Horizontal/upward OR from non-direct-sky source: normal decay of 1 per block
        int decay;
        var blockFilter = (int)BlockRegistry.GetLightFilter(block);
        
        if (isSkyLight && isVerticalDown && currentLight == MaxLight && skyVisibility != null && skyVisibility[sourceIndex] != 0)
        {
            // Minecraft behavior: sky light propagates straight down through AIR with NO decay
            // But translucent blocks (water, leaves) still apply their filter
            if (blockFilter == 0)
            {
                // Air block - no decay, continue direct-sky column
                decay = 0;
                skyVisibility[index] = 1; // Mark target as direct-sky
            }
            else
            {
                // Translucent block (water, leaves, ice) - apply filter
                decay = blockFilter;
                skyVisibility[index] = 0; // No longer pure direct-sky
            }
        }
        else
        {
            // Minecraft-style: lateral/upward sky light OR block light decays by 1 per block
            // (or more if the block has a light filter like water)
            decay = Math.Max(1, blockFilter);
        }

        var newLight = currentLight - decay;

        if (newLight > neighborLight)
        {
            if (isSkyLight)
                SetSkyLight(chunk, index, newLight);
            else
                SetBlockLight(chunk, index, newLight);

            queue.Enqueue(PackPos(x, y, z));
        }
    }

    private static int GetIndex(int x, int y, int z) => y * VoxelHelper.ChunkSideSizeSquare + z * VoxelHelper.ChunkSideSize + x;

    private static int PackPos(int x, int y, int z) => x | (y << 4) | (z << 13);

    private static void UnpackPos(int packed, out int x, out int y, out int z)
    {
        x = packed & 0xF;
        y = (packed >> 4) & 0x1FF;
        z = (packed >> 13) & 0xF;
    }

    private static int GetSkyLight(ChunkData chunk, int index) => chunk.LightData[index] & 0xF;

    private static void SetSkyLight(ChunkData chunk, int index, int value) => chunk.LightData[index] = (byte)((chunk.LightData[index] & 0xF0) | (value & 0xF));

    private static int GetBlockLight(ChunkData chunk, int index) => (chunk.LightData[index] >> 4) & 0xF;

    private static void SetBlockLight(ChunkData chunk, int index, int value) => chunk.LightData[index] = (byte)((chunk.LightData[index] & 0x0F) | ((value & 0xF) << 4));
}

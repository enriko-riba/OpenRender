using System;
using System.Collections.Generic;

namespace SpyroGame.World;

/// <summary>
/// Represents the data for a single chunk, including voxel data (palette-compressed) and biome data.
/// </summary>
public class ChunkData
{
    /// <summary>
    /// Block palette: maps byte index -> BlockId.
    /// Most chunks use 5-15 unique blocks; max 128.
    /// </summary>
    public BlockId[] Palette;

    /// <summary>
    /// Voxel data: 7-bit palette index per block.
    /// To get actual block: Palette[VoxelData[i]]
    /// </summary>
    public byte[] VoxelData;

    /// <summary>
    /// Biome storage: coarse grid (4x4 horizontal, 1 per column).
    /// </summary>
    public BiomeId[] Biomes;

    /// <summary>
    /// Optional: cached surface heights per column.
    /// </summary>
    public int[] SurfaceHeights;

    /// <summary>
    /// Light data: 4 bits sky light, 4 bits block light per voxel.
    /// </summary>
    public byte[] LightData;

    public int ChunkIndex;
    public long Version;

    private int paletteCount;
    
    /// <summary>
    /// Performance optimization: Reverse lookup from BlockId to palette index.
    /// Avoids O(n) linear search in GetOrAddPaletteEntry.
    /// Key = BlockId.GetId(), Value = palette index.
    /// </summary>
    private readonly Dictionary<int, byte> reversePalette = new(32);

    public ChunkData()
    {
        // Initialize with reasonable defaults
        Palette = new BlockId[16]; // Start small, resize as needed
        VoxelData = new byte[VoxelHelper.ChunkVoxelCount];
        Biomes = new BiomeId[16]; // 4x4 grid
        SurfaceHeights = new int[VoxelHelper.ChunkSideSizeSquare];
        LightData = new byte[VoxelHelper.ChunkVoxelCount];

        // Always add Air as index 0
        Palette[0] = BlockId.Air;
        reversePalette[BlockId.Air.GetId()] = 0;
        paletteCount = 1;
    }

    /// <summary>
    /// Get BlockId at position (resolves palette indirection).
    /// </summary>
    public BlockId GetBlock(int x, int y, int z)
    {
        int idx = y * VoxelHelper.ChunkSideSizeSquare + z * VoxelHelper.ChunkSideSize + x;
        byte paletteIndex = VoxelData[idx];
        return Palette[paletteIndex];
    }

    /// <summary>
    /// Set block at position, updating palette if necessary.
    /// </summary>
    public void SetBlock(int x, int y, int z, BlockId block)
    {
        byte paletteIndex = GetOrAddPaletteEntry(block);
        int idx = y * VoxelHelper.ChunkSideSizeSquare + z * VoxelHelper.ChunkSideSize + x;
        VoxelData[idx] = paletteIndex;

        // Update surface height if we placed a block above the current surface
        if (!block.IsAir())
        {
            int colIdx = z * VoxelHelper.ChunkSideSize + x;
            if (y > SurfaceHeights[colIdx])
            {
                SurfaceHeights[colIdx] = y;
            }
        }
        // Note: If we remove a block (set to Air), we don't strictly need to lower SurfaceHeights immediately.
        // Leaving it high is safe for meshing (just iterates a bit of air), whereas scanning down is expensive.
    }

    /// <summary>
    /// Get or add a palette entry, returns the palette index.
    /// Uses O(1) dictionary lookup instead of O(n) linear search.
    /// </summary>
    public byte GetOrAddPaletteEntry(BlockId blockId)
    {
        var blockIdValue = blockId.GetId();
        
        // O(1) lookup via reverse palette dictionary
        if (reversePalette.TryGetValue(blockIdValue, out var existingIndex))
        {
            return existingIndex;
        }

        // Not found, add new entry
        if (paletteCount >= 128)
        {
            // Fallback or error - for now just return 0 (Air) or last valid
            // In a real scenario we might need to handle palette overflow
            // But 128 unique blocks per chunk is extremely rare in Minecraft-like terrain
            return 0; 
        }

        if (paletteCount >= Palette.Length)
        {
            Array.Resize(ref Palette, Palette.Length * 2);
        }

        var newIndex = (byte)paletteCount;
        Palette[paletteCount] = blockId;
        reversePalette[blockIdValue] = newIndex;
        paletteCount++;
        
        return newIndex;
    }
    
    /// <summary>
    /// Returns the active palette as an array (trimmed to actual count).
    /// </summary>
    public BlockId[] GetActivePalette()
    {
        var result = new BlockId[paletteCount];
        Array.Copy(Palette, result, paletteCount);
        return result;
    }
}

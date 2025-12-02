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

    public int ChunkIndex;
    public long Version;

    private int paletteCount;

    public ChunkData()
    {
        // Initialize with reasonable defaults
        Palette = new BlockId[16]; // Start small, resize as needed
        VoxelData = new byte[VoxelHelper.ChunkVoxelCount];
        Biomes = new BiomeId[16]; // 4x4 grid
        SurfaceHeights = new int[VoxelHelper.ChunkSideSizeSquare];
        
        // Always add Air as index 0
        Palette[0] = BlockId.Air;
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
    }

    /// <summary>
    /// Get or add a palette entry, returns the palette index.
    /// </summary>
    public byte GetOrAddPaletteEntry(BlockId blockId)
    {
        // Linear search is fine for small palettes (typically < 20 entries)
        for (int i = 0; i < paletteCount; i++)
        {
            if (Palette[i] == blockId)
                return (byte)i;
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

        Palette[paletteCount] = blockId;
        return (byte)paletteCount++;
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

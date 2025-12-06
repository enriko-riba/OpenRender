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

    public void Serialize(System.IO.BinaryWriter writer)
    {
        writer.Write(Version);
        writer.Write(ChunkIndex);
        
        // Palette
        writer.Write(paletteCount);
        for (int i = 0; i < paletteCount; i++)
        {
            writer.Write((ushort)Palette[i]);
        }

        // VoxelData
        writer.Write(VoxelData.Length);
        writer.Write(VoxelData);

        // LightData
        writer.Write(LightData.Length);
        writer.Write(LightData);

        // Biomes (ChunkData.Biomes)
        writer.Write(Biomes.Length);
        for (int i = 0; i < Biomes.Length; i++)
        {
            writer.Write((byte)Biomes[i]);
        }

        // SurfaceHeights
        writer.Write(SurfaceHeights.Length);
        for (int i = 0; i < SurfaceHeights.Length; i++)
        {
            writer.Write(SurfaceHeights[i]);
        }
    }

    public static ChunkData Deserialize(System.IO.BinaryReader reader)
    {
        var data = new ChunkData();
        data.Version = reader.ReadInt64();
        data.ChunkIndex = reader.ReadInt32();

        // Palette
        data.paletteCount = reader.ReadInt32();
        if (data.Palette.Length < data.paletteCount)
        {
            data.Palette = new BlockId[data.paletteCount];
        }
        
        // Clear default palette (Air at 0) before reading
        data.reversePalette.Clear();
        
        for (int i = 0; i < data.paletteCount; i++)
        {
            var blockId = (BlockId)reader.ReadUInt16();
            data.Palette[i] = blockId;
            data.reversePalette[blockId.GetId()] = (byte)i;
        }

        // Ensure Palette[0] is Air if the file didn't enforce it (sanity check)
        // If the file has something else at 0, we are in trouble because VoxelData 0s will map to it.
        // But we trust the file to be consistent with itself.
        // If the file was saved with Grass at 0, then VoxelData 0 means Grass.
        // If the file was saved with Air at 0, then VoxelData 0 means Air.
        
        // VoxelData
        var voxelLen = reader.ReadInt32();
        // Read into existing buffer if possible to avoid alloc, but here we just read new
        // Note: ChunkData constructor allocates VoxelData, so we are discarding it here.
        // Optimization: Read directly into data.VoxelData
        if (voxelLen == data.VoxelData.Length)
        {
            reader.Read(data.VoxelData, 0, voxelLen);
        }
        else
        {
            data.VoxelData = reader.ReadBytes(voxelLen);
        }

        // Sanity Check: Ensure Palette[0] is Air
        // If Palette[0] is NOT Air, it means the file was saved with a non-standard palette layout.
        // This causes empty space (VoxelData=0) to be interpreted as a block (e.g. Grass),
        // leading to "invisible pickable blocks" or solid chunks.
        if (data.paletteCount > 0 && data.Palette[0] != BlockId.Air)
        {
            // Find where Air is
            int airIndex = -1;
            for (int i = 0; i < data.paletteCount; i++)
            {
                if (data.Palette[i] == BlockId.Air)
                {
                    airIndex = i;
                    break;
                }
            }

            if (airIndex != -1)
            {
                // Swap Palette[0] and Palette[airIndex]
                var temp = data.Palette[0];
                data.Palette[0] = data.Palette[airIndex];
                data.Palette[airIndex] = temp;

                // Update reversePalette
                data.reversePalette[data.Palette[0].GetId()] = 0;
                data.reversePalette[data.Palette[airIndex].GetId()] = (byte)airIndex;

                // Update VoxelData: Swap 0 and airIndex
                // This is expensive but necessary to fix the corrupted chunk
                for (int i = 0; i < data.VoxelData.Length; i++)
                {
                    if (data.VoxelData[i] == 0) data.VoxelData[i] = (byte)airIndex;
                    else if (data.VoxelData[i] == (byte)airIndex) data.VoxelData[i] = 0;
                }
            }
            else
            {
                // Air is missing from palette! This is very bad.
                // We must insert Air at 0.
                // If palette is full, we might lose a block type.
                // For now, just force 0 to Air and hope for the best (better than solid world).
                data.Palette[0] = BlockId.Air;
                data.reversePalette[BlockId.Air.GetId()] = 0;
                // Note: This effectively turns whatever was at 0 into Air.
                // If 0 was Grass, all Grass becomes Air.
            }
        }

        // LightData
        var lightLen = reader.ReadInt32();
        if (lightLen == data.LightData.Length)
        {
            reader.Read(data.LightData, 0, lightLen);
        }
        else
        {
            data.LightData = reader.ReadBytes(lightLen);
        }

        // Biomes
        var biomesLen = reader.ReadInt32();
        for (int i = 0; i < biomesLen; i++)
        {
            data.Biomes[i] = (BiomeId)reader.ReadByte();
        }

        // SurfaceHeights
        var heightsLen = reader.ReadInt32();
        for (int i = 0; i < heightsLen; i++)
        {
            data.SurfaceHeights[i] = reader.ReadInt32();
        }

        // Recalculate SurfaceHeights to be safe
        // If the file was saved with incorrect heights (e.g. due to block removal not updating them),
        // or if the palette mapping changed, we should ensure they are correct.
        // This is fast enough to do on load and prevents invisible blocks.
        data.RecalculateSurfaceHeights();

        return data;
    }

    public void RecalculateSurfaceHeights()
    {
        // Initialize to -1 (empty column) instead of 0
        // This ensures that if the column is truly empty, we don't assume a block at 0.
        Array.Fill(SurfaceHeights, -1);
        var size = VoxelHelper.ChunkSideSize;
        
        for (var z = 0; z < size; z++)
        {
            for (var x = 0; x < size; x++)
            {
                var colIdx = z * size + x;
                // Scan down from top to find highest non-air block
                for (var y = VoxelHelper.ChunkYSize - 1; y >= 0; y--)
                {
                    if (!GetBlock(x, y, z).IsAir())
                    {
                        SurfaceHeights[colIdx] = y;
                        break;
                    }
                }
            }
        }
    }
}

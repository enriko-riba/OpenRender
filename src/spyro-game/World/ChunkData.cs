using SpyroGame.World.Registry;
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

    // Chunk voxel/light buffers are frequently allocated via ArrayPool in ChunkVoxelDataCache.
    // When chunks are deserialized from disk/network, the buffers are plain arrays.
    // Track ownership so we can safely return only pooled arrays.
    internal bool VoxelDataIsPooled;
    internal bool LightDataIsPooled;

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

        VoxelDataIsPooled = false;
        LightDataIsPooled = false;

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
        var idx = y * VoxelHelper.ChunkSideSizeSquare + z * VoxelHelper.ChunkSideSize + x;
        var paletteIndex = VoxelData[idx];
        return Palette[paletteIndex];
    }

    /// <summary>
    /// Checks if the local coordinates are within the chunk bounds.
    /// </summary>
    public static bool IsWithinBounds(int x, int y, int z) => x >= 0 && x < VoxelHelper.ChunkSideSize &&
               y >= 0 && y < VoxelHelper.ChunkYSize &&
               z >= 0 && z < VoxelHelper.ChunkSideSize;

    /// <summary>
    /// Set block at position, updating palette if necessary.
    /// </summary>
    public void SetBlock(int x, int y, int z, BlockId block)
    {
        var paletteIndex = GetOrAddPaletteEntry(block);
        var idx = y * VoxelHelper.ChunkSideSizeSquare + z * VoxelHelper.ChunkSideSize + x;
        VoxelData[idx] = paletteIndex;

        // Update surface height if we placed a block above the current surface
        if (!block.IsAir())
        {
            var colIdx = z * VoxelHelper.ChunkSideSize + x;
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
        for (var i = 0; i < paletteCount; i++)
        {
            writer.Write((ushort)Palette[i]);
        }

        // VoxelData
        var expectedVoxelLen = VoxelHelper.ChunkVoxelCount;
        if (VoxelData is null || VoxelData.Length < expectedVoxelLen)
        {
            throw new InvalidOperationException($"ChunkData.VoxelData must be at least {expectedVoxelLen} bytes (actual={(VoxelData is null ? "<null>" : VoxelData.Length)})");
        }
        writer.Write(expectedVoxelLen);
        writer.Write(VoxelData, 0, expectedVoxelLen);

        // LightData
        var expectedLightLen = VoxelHelper.ChunkVoxelCount;
        if (LightData is null || LightData.Length < expectedLightLen)
        {
            throw new InvalidOperationException($"ChunkData.LightData must be at least {expectedLightLen} bytes (actual={(LightData is null ? "<null>" : LightData.Length)})");
        }
        writer.Write(expectedLightLen);
        writer.Write(LightData, 0, expectedLightLen);

        // Biomes (ChunkData.Biomes)
        writer.Write(Biomes.Length);
        for (var i = 0; i < Biomes.Length; i++)
        {
            writer.Write((byte)Biomes[i]);
        }

        // SurfaceHeights
        writer.Write(SurfaceHeights.Length);
        for (var i = 0; i < SurfaceHeights.Length; i++)
        {
            writer.Write(SurfaceHeights[i]);
        }
    }

    public static ChunkData Deserialize(System.IO.BinaryReader reader)
    {
        var data = new ChunkData
        {
            Version = reader.ReadInt64(),
            ChunkIndex = reader.ReadInt32(),

            // Palette
            paletteCount = reader.ReadInt32()
        };

        if (data.Palette.Length < data.paletteCount)
        {
            data.Palette = new BlockId[data.paletteCount];
        }
        
        // Clear default palette (Air at 0) before reading
        data.reversePalette.Clear();
        
        for (var i = 0; i < data.paletteCount; i++)
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
        // The on-disk format writes a length prefix. Older/corrupt files may have a different length;
        // keep runtime buffers at the expected size to avoid downstream out-of-range indexing.
        var expectedVoxelLen = VoxelHelper.ChunkVoxelCount;
        var voxelLen = reader.ReadInt32();

        if (voxelLen == expectedVoxelLen)
        {
            reader.Read(data.VoxelData, 0, expectedVoxelLen);
        }
        else
        {
            // Read what we have, pad/trim to expected length.
            var bytesToCopy = Math.Min(voxelLen, expectedVoxelLen);
            if (bytesToCopy > 0)
            {
                reader.Read(data.VoxelData, 0, bytesToCopy);
            }

            // If file has more data than expected, consume the remainder.
            var remaining = voxelLen - bytesToCopy;
            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, 8192);
                _ = reader.ReadBytes(chunk);
                remaining -= chunk;
            }

            // If file has less data than expected, leave the rest as zero (palette index 0).
        }

        // Sanity Check: Ensure Palette[0] is Air
        // If Palette[0] is NOT Air, it means the file was saved with a non-standard palette layout.
        // This causes empty space (VoxelData=0) to be interpreted as a block (e.g. Grass),
        // leading to "invisible pickable blocks" or solid chunks.
        if (data.paletteCount > 0 && data.Palette[0] != BlockId.Air)
        {
            // Find where Air is
            int airIndex = -1;
            for (var i = 0; i < data.paletteCount; i++)
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
                (data.Palette[airIndex], data.Palette[0]) = (data.Palette[0], data.Palette[airIndex]);

                // Update reversePalette
                data.reversePalette[data.Palette[0].GetId()] = 0;
                data.reversePalette[data.Palette[airIndex].GetId()] = (byte)airIndex;

                // Update VoxelData: Swap 0 and airIndex
                // This is expensive but necessary to fix the corrupted chunk
                for (var i = 0; i < data.VoxelData.Length; i++)
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
        // Same story as VoxelData: keep the runtime buffer at expected size.
        var expectedLightLen = VoxelHelper.ChunkVoxelCount;
        var lightLen = reader.ReadInt32();
        if (lightLen == expectedLightLen)
        {
            reader.Read(data.LightData, 0, expectedLightLen);
        }
        else
        {
            var bytesToCopy = Math.Min(lightLen, expectedLightLen);
            if (bytesToCopy > 0)
            {
                reader.Read(data.LightData, 0, bytesToCopy);
            }

            var remaining = lightLen - bytesToCopy;
            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, 8192);
                _ = reader.ReadBytes(chunk);
                remaining -= chunk;
            }
            // Pad remainder with 0 (no light).
        }

        // Biomes
        var biomesLen = reader.ReadInt32();
        for (var i = 0; i < biomesLen; i++)
        {
            data.Biomes[i] = (BiomeId)reader.ReadByte();
        }

        // SurfaceHeights
        var heightsLen = reader.ReadInt32();
        for (var i = 0; i < heightsLen; i++)
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

    /// <summary>
    /// Copies the content of this chunk data to another instance.
    /// Used when we need to modify a chunk that is already in the cache (e.g. decoration pass).
    /// </summary>
    public void CloneTo(ChunkData target)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));

        // Copy Palette
        if (target.Palette.Length < this.Palette.Length)
            target.Palette = new BlockId[this.Palette.Length];
        Array.Copy(this.Palette, target.Palette, this.Palette.Length);
        target.paletteCount = this.paletteCount;
        
        // Copy Reverse Palette
        target.reversePalette.Clear();
        foreach(var kvp in this.reversePalette)
        {
            target.reversePalette[kvp.Key] = kvp.Value;
        }
        
        // Copy VoxelData
        if (target.VoxelData.Length < this.VoxelData.Length)
        {
            target.VoxelData = new byte[this.VoxelData.Length];
            target.VoxelDataIsPooled = false;
        }
        Array.Copy(this.VoxelData, target.VoxelData, this.VoxelData.Length);
        
        // Copy SurfaceHeights
        if (target.SurfaceHeights.Length < this.SurfaceHeights.Length)
        {
            target.SurfaceHeights = new int[this.SurfaceHeights.Length];
        }
        Array.Copy(this.SurfaceHeights, target.SurfaceHeights, this.SurfaceHeights.Length);
        
        // Copy LightData
        if (target.LightData.Length < this.LightData.Length)
        {
            target.LightData = new byte[this.LightData.Length];
            target.LightDataIsPooled = false;
        }
        Array.Copy(this.LightData, target.LightData, this.LightData.Length);

        // Copy Biomes
        if (this.Biomes != null)
        {
            if (target.Biomes == null || target.Biomes.Length < this.Biomes.Length)
                target.Biomes = new BiomeId[this.Biomes.Length];
            Array.Copy(this.Biomes, target.Biomes, this.Biomes.Length);
        }
    }
}

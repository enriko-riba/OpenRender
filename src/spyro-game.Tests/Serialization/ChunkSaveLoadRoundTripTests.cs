using SpyroGame.World;
using System.Buffers;
using System.IO.Compression;
using Xunit;
using static SpyroGame.Tests.Common.LightingTestHelpers;

namespace SpyroGame.Tests.Serialization;

/// <summary>
/// Integration tests for the full save/load round-trip workflow, including:
/// - v1 (uncompressed) to v2 (compressed) migration
/// - Light data preservation across save/load
/// - Block edit persistence
/// - Biome data persistence
/// </summary>
public class ChunkSaveLoadRoundTripTests
{
    private const int SAVE_FILE_VERSION_V1 = 1;
    private const int SAVE_FILE_VERSION_V2 = 2;

    /// <summary>
    /// Simulates saving a chunk in v2 (compressed) format.
    /// </summary>
    private static byte[] SaveChunkV2(ChunkData data, ChunkBiomeData? biomeData)
    {
        using var memStream = new MemoryStream();
        
        // Write uncompressed header
        using (var headerWriter = new BinaryWriter(memStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            headerWriter.Write("CHNK");
            headerWriter.Write(SAVE_FILE_VERSION_V2);
        }
        
        // Write compressed body
        using (var gzipStream = new GZipStream(memStream, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new BinaryWriter(gzipStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            data.Serialize(writer);
            writer.Write(biomeData != null);
            biomeData?.Serialize(writer);
        }
        
        return memStream.ToArray();
    }

    /// <summary>
    /// Simulates saving a chunk in v1 (uncompressed) format for backward compatibility testing.
    /// </summary>
    private static byte[] SaveChunkV1(ChunkData data, ChunkBiomeData? biomeData)
    {
        using var memStream = new MemoryStream();
        using var writer = new BinaryWriter(memStream);
        
        writer.Write("CHNK");
        writer.Write(SAVE_FILE_VERSION_V1);
        data.Serialize(writer);
        writer.Write(biomeData != null);
        biomeData?.Serialize(writer);
        
        return memStream.ToArray();
    }

    /// <summary>
    /// Simulates loading a chunk from bytes (auto-detects v1 vs v2 format).
    /// </summary>
    private static (ChunkData data, ChunkBiomeData? biomeData) LoadChunk(byte[] bytes)
    {
        using var memStream = new MemoryStream(bytes);
        using var headerReader = new BinaryReader(memStream, System.Text.Encoding.UTF8, leaveOpen: true);
        
        var magic = headerReader.ReadString();
        if (magic != "CHNK")
            throw new InvalidDataException("Invalid chunk file magic");
        
        var version = headerReader.ReadInt32();
        
        ChunkData data;
        ChunkBiomeData? biomeData = null;
        
        if (version >= 2)
        {
            // v2+: GZip compressed
            using var gzipStream = new GZipStream(memStream, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzipStream);
            
            data = ChunkData.Deserialize(reader);
            if (reader.ReadBoolean())
            {
                biomeData = ChunkBiomeData.Deserialize(reader);
            }
        }
        else
        {
            // v1: Uncompressed
            data = ChunkData.Deserialize(headerReader);
            if (headerReader.ReadBoolean())
            {
                biomeData = ChunkBiomeData.Deserialize(headerReader);
            }
        }
        
        return (data, biomeData);
    }

    [Fact]
    public void V2Compressed_RoundTrip_WorksWithArrayPoolOversizedVoxelBuffer()
    {
        // ArrayPool may return a larger buffer than requested (bucket sizing).
        // We must not serialize the full backing length or the server will quarantine its own saves.
        var pooled = ArrayPool<byte>.Shared.Rent(VoxelHelper.ChunkVoxelCount);
        try
        {
            Assert.True(pooled.Length >= VoxelHelper.ChunkVoxelCount);
            // This test is only meaningful if we got an oversized buffer.
            Assert.True(pooled.Length > VoxelHelper.ChunkVoxelCount);

            var originalData = new ChunkData { ChunkIndex = 42, Version = 12345 };
            originalData.VoxelData = pooled;
            // Clear the used range to avoid stale palette indices from the pool.
            Array.Clear(originalData.VoxelData, 0, VoxelHelper.ChunkVoxelCount);

            // Keep LightData at the expected size.
            originalData.LightData = new byte[VoxelHelper.ChunkVoxelCount];

            originalData.SetBlock(1, 10, 1, BlockId.Stone);

            var bytes = SaveChunkV2(originalData, biomeData: null);
            var (loadedData, loadedBiome) = LoadChunk(bytes);

            Assert.Null(loadedBiome);
            Assert.Equal(42, loadedData.ChunkIndex);
            Assert.Equal(12345, loadedData.Version);
            Assert.Equal(VoxelHelper.ChunkVoxelCount, loadedData.VoxelData.Length);
            Assert.Equal(BlockId.Stone, loadedData.GetBlock(1, 10, 1));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pooled);
        }
    }

    [Fact]
    public void V2Compressed_RoundTrip_PreservesAllData()
    {
        // Arrange - create chunk with blocks, light, biomes
        var originalData = CreateChunkWithFloor(64, chunkIndex: 42);
        originalData.Version = 12345;
        originalData.SetBlock(5, 65, 5, BlockId.Torch);
        SetSkyLight(originalData, 8, 100, 8, 15);
        SetBlockLight(originalData, 5, 65, 5, 14);  // Torch light
        
        var originalBiome = new ChunkBiomeData();
        originalBiome.SetBiomeAt(8, 8, BiomeId.Savanna);
        originalBiome.Temperature[0] = 0.7f;
        
        // Act - save and load
        var bytes = SaveChunkV2(originalData, originalBiome);
        var (loadedData, loadedBiome) = LoadChunk(bytes);
        
        // Assert - chunk data
        Assert.Equal(42, loadedData.ChunkIndex);
        Assert.Equal(12345, loadedData.Version);
        Assert.Equal(BlockId.Stone, loadedData.GetBlock(8, 64, 8));
        Assert.Equal(BlockId.Torch, loadedData.GetBlock(5, 65, 5));
        Assert.Equal(15, GetSkyLight(loadedData, 8, 100, 8));
        Assert.Equal(14, GetBlockLight(loadedData, 5, 65, 5));
        
        // Assert - biome data
        Assert.NotNull(loadedBiome);
        Assert.Equal(BiomeId.Savanna, loadedBiome!.GetBiomeAt(8, 8));
        Assert.Equal(0.7f, loadedBiome.Temperature[0], precision: 5);
    }

    [Fact]
    public void V1Uncompressed_LoadsCorrectly()
    {
        // Arrange - save in old v1 format
        var originalData = CreateChunkWithFloor(50, chunkIndex: 10);
        originalData.SetBlock(3, 51, 3, BlockId.Glass);
        SetSkyLight(originalData, 3, 51, 3, 10);
        
        var originalBiome = new ChunkBiomeData();
        originalBiome.SetBiomeAt(0, 0, BiomeId.Desert);
        
        // Act - save as v1, load with auto-detection
        var bytes = SaveChunkV1(originalData, originalBiome);
        var (loadedData, loadedBiome) = LoadChunk(bytes);
        
        // Assert
        Assert.Equal(10, loadedData.ChunkIndex);
        Assert.Equal(BlockId.Stone, loadedData.GetBlock(8, 50, 8));
        Assert.Equal(BlockId.Glass, loadedData.GetBlock(3, 51, 3));
        Assert.Equal(10, GetSkyLight(loadedData, 3, 51, 3));
        Assert.NotNull(loadedBiome);
        Assert.Equal(BiomeId.Desert, loadedBiome!.GetBiomeAt(0, 0));
    }

    [Fact]
    public void V2Compressed_SignificantlySmallerThanV1()
    {
        // Arrange - typical terrain chunk
        var data = CreateChunkWithFloor(64);
        // Add some variety
        for (var x = 0; x < 16; x++)
        for (var z = 0; z < 16; z++)
        {
            if ((x + z) % 3 == 0)
                data.SetBlock(x, 65, z, BlockId.Grass);
        }
        
        var biomeData = new ChunkBiomeData();
        
        // Act
        var v1Bytes = SaveChunkV1(data, biomeData);
        var v2Bytes = SaveChunkV2(data, biomeData);
        
        // Assert - v2 should be significantly smaller
        var compressionRatio = (double)v2Bytes.Length / v1Bytes.Length;
        Assert.True(compressionRatio < 0.5, 
            $"V2 ({v2Bytes.Length} bytes) should be <50% of V1 ({v1Bytes.Length} bytes), ratio={compressionRatio:P1}");
    }

    [Fact]
    public void LightData_PreservedAfterSaveLoad_FromDisk()
    {
        // This tests the specific scenario: player builds room, exits, room should stay dark on reload
        
        // Arrange - create a sealed room (dark inside)
        var originalData = CreateSealedRoom(2, 60, 2, 13, 70, 13);
        
        // Calculate lighting
        LightingCalculator.CalculateLighting(originalData);
        
        // Check that inside the room is dark
        var insideSkyLight = GetSkyLight(originalData, 8, 65, 8);
        var outsideSkyLight = GetSkyLight(originalData, 8, 75, 8);
        
        Assert.True(insideSkyLight < 5, $"Inside room should be dark (skylight={insideSkyLight})");
        Assert.Equal(15, outsideSkyLight); // Above room should be bright
        
        // Act - save and reload
        var bytes = SaveChunkV2(originalData, null);
        var (loadedData, _) = LoadChunk(bytes);
        
        // Assert - light values preserved exactly
        Assert.Equal(insideSkyLight, GetSkyLight(loadedData, 8, 65, 8));
        Assert.Equal(outsideSkyLight, GetSkyLight(loadedData, 8, 75, 8));
    }

    [Fact]
    public void TorchLight_PreservedAfterSaveLoad()
    {
        // Arrange - room with torch
        var originalData = CreateSealedRoom(2, 60, 2, 13, 70, 13);
        
        // Place torch in center of room
        originalData.SetBlock(8, 65, 8, BlockId.Torch);
        
        // Calculate lighting
        LightingCalculator.CalculateLighting(originalData);
        
        // Check torch light is present
        var torchLight = GetBlockLight(originalData, 8, 65, 8);
        var nearbyLight = GetBlockLight(originalData, 7, 65, 8);  // Adjacent to torch
        
        Assert.Equal(14, torchLight);  // Torch emits level 14
        Assert.True(nearbyLight > 0, "Light should propagate to adjacent blocks");
        
        // Act - save and reload
        var bytes = SaveChunkV2(originalData, null);
        var (loadedData, _) = LoadChunk(bytes);
        
        // Assert - torch light preserved
        Assert.Equal(14, GetBlockLight(loadedData, 8, 65, 8));
        Assert.Equal(nearbyLight, GetBlockLight(loadedData, 7, 65, 8));
    }

    [Fact]
    public void EmptyChunk_SaveLoadWorks()
    {
        // Arrange - completely empty chunk
        var originalData = CreateAirChunk(chunkIndex: 999);
        
        // Act
        var bytes = SaveChunkV2(originalData, null);
        var (loadedData, loadedBiome) = LoadChunk(bytes);
        
        // Assert
        Assert.Equal(999, loadedData.ChunkIndex);
        Assert.Null(loadedBiome);
        Assert.Equal(BlockId.Air, loadedData.GetBlock(8, 50, 8));
    }

    [Fact]
    public void ChunkWithNoBiomeData_LoadsWithoutBiome()
    {
        // Arrange - chunk without biome data
        var originalData = CreateChunkWithFloor(50);
        
        // Act - save without biome data
        var bytes = SaveChunkV2(originalData, null);
        var (loadedData, loadedBiome) = LoadChunk(bytes);
        
        // Assert
        Assert.NotNull(loadedData);
        Assert.Null(loadedBiome);
    }

    [Fact]
    public void MultipleBlockTypes_PalettePreservedAcrossSaveLoad()
    {
        // Arrange - chunk with many block types
        var originalData = CreateAirChunk();
        
        var blockTypes = new BlockId[]
        {
            BlockId.Stone, BlockId.Dirt, BlockId.Grass, BlockId.Sand,
            BlockId.Gravel, BlockId.Cobblestone, BlockId.OakLog, BlockId.OakLeaves,
            BlockId.Glass, BlockId.Glowstone, BlockId.Torch, BlockId.Water,
            BlockId.Ice, BlockId.Snow, BlockId.Clay
        };
        
        for (var i = 0; i < blockTypes.Length; i++)
        {
            originalData.SetBlock(i % 16, 50 + i / 16, i / 16, blockTypes[i]);
        }
        
        // Act
        var bytes = SaveChunkV2(originalData, null);
        var (loadedData, _) = LoadChunk(bytes);
        
        // Assert - all block types preserved
        for (var i = 0; i < blockTypes.Length; i++)
        {
            var loaded = loadedData.GetBlock(i % 16, 50 + i / 16, i / 16);
            Assert.Equal(blockTypes[i], loaded);
        }
    }

    [Fact]
    public void CaveBiomes_PreservedAcrossSaveLoad()
    {
        // Arrange
        var originalData = CreateAirChunk();
        var originalBiome = new ChunkBiomeData();
        
        // Set various cave biomes
        originalBiome.SetCaveBiomeAt(0, 0, 0, CaveBiomeId.LushCave);
        originalBiome.SetCaveBiomeAt(2, 5, 2, CaveBiomeId.DripstoneCave);
        originalBiome.SetCaveBiomeAt(3, 10, 3, CaveBiomeId.DeepDark);
        
        // Act
        var bytes = SaveChunkV2(originalData, originalBiome);
        var (_, loadedBiome) = LoadChunk(bytes);
        
        // Assert
        Assert.NotNull(loadedBiome);
        Assert.Equal(CaveBiomeId.LushCave, loadedBiome!.GetCaveBiomeAt(0, 0, 0));
        Assert.Equal(CaveBiomeId.DripstoneCave, loadedBiome.GetCaveBiomeAt(8, 80, 8)); // Maps to cell (2,5,2)
        Assert.Equal(CaveBiomeId.DeepDark, loadedBiome.GetCaveBiomeAt(12, 160, 12)); // Maps to cell (3,10,3)
    }

    [Fact]
    public void SurfaceHeights_RecalculatedOnLoad()
    {
        // Arrange - set up specific surface heights
        var originalData = CreateChunkWithFloor(50);
        originalData.SetBlock(5, 100, 5, BlockId.Stone);  // Tall column
        originalData.RecalculateSurfaceHeights();
        
        var originalHeight = originalData.SurfaceHeights[5 * 16 + 5];
        Assert.Equal(100, originalHeight);
        
        // Act
        var bytes = SaveChunkV2(originalData, null);
        var (loadedData, _) = LoadChunk(bytes);
        
        // Assert - surface heights should be recalculated correctly
        Assert.Equal(100, loadedData.SurfaceHeights[5 * 16 + 5]);
        Assert.Equal(50, loadedData.SurfaceHeights[8 * 16 + 8]);
    }
}

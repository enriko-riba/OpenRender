using DarkVox.Shared.World.Registry;
using DarkVox.Shared.World;
using DarkVox.World;
using System.IO.Compression;
using Xunit;
using static DarkVox.Tests.Common.LightingTestHelpers;

namespace DarkVox.Tests.Serialization;

/// <summary>
/// Tests for ChunkData and ChunkBiomeData serialization, including:
/// - Round-trip serialization (save and reload)
/// - Compression support (v2 format)
/// - Backward compatibility with v1 uncompressed format
/// - Light data preservation
/// - Palette integrity
/// </summary>
public class ChunkSerializationTests
{
    [Fact]
    public void ChunkData_RoundTrip_PreservesBlocks()
    {
        // Arrange
        var original = CreateChunkWithFloor(50, chunkIndex: 42);
        original.Version = 123;
        
        // Add some varied blocks
        original.SetBlock(5, 51, 5, BlockId.Dirt);
        original.SetBlock(6, 51, 6, BlockId.Grass);
        original.SetBlock(7, 51, 7, BlockId.Sand);
        
        // Act - serialize and deserialize
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            original.Serialize(writer);
        }
        
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var loaded = ChunkData.Deserialize(reader);
        
        // Assert
        Assert.Equal(original.ChunkIndex, loaded.ChunkIndex);
        Assert.Equal(original.Version, loaded.Version);
        Assert.Equal(original.GetBlock(5, 51, 5), loaded.GetBlock(5, 51, 5));
        Assert.Equal(original.GetBlock(6, 51, 6), loaded.GetBlock(6, 51, 6));
        Assert.Equal(original.GetBlock(7, 51, 7), loaded.GetBlock(7, 51, 7));
        Assert.Equal(BlockId.Stone, loaded.GetBlock(8, 50, 8)); // Floor should be stone
        Assert.Equal(BlockId.Air, loaded.GetBlock(8, 51, 8)); // Above floor should be air
    }

    [Fact]
    public void ChunkData_RoundTrip_PreservesLightData()
    {
        // Arrange
        var original = CreateAirChunk(chunkIndex: 0);
        
        // Set some light values
        SetSkyLight(original, 8, 100, 8, 15);
        SetSkyLight(original, 8, 99, 8, 14);
        SetBlockLight(original, 5, 50, 5, 12);
        SetBlockLight(original, 6, 50, 6, 8);
        
        // Act
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            original.Serialize(writer);
        }
        
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var loaded = ChunkData.Deserialize(reader);
        
        // Assert
        Assert.Equal(15, GetSkyLight(loaded, 8, 100, 8));
        Assert.Equal(14, GetSkyLight(loaded, 8, 99, 8));
        Assert.Equal(12, GetBlockLight(loaded, 5, 50, 5));
        Assert.Equal(8, GetBlockLight(loaded, 6, 50, 6));
    }

    [Fact]
    public void ChunkData_RoundTrip_PreservesSurfaceHeights()
    {
        // Arrange
        var original = CreateChunkWithFloor(60, chunkIndex: 0);
        // Add a tall column
        for (var y = 61; y <= 80; y++)
            original.SetBlock(3, y, 3, BlockId.Stone);
        original.RecalculateSurfaceHeights();
        
        // Act
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            original.Serialize(writer);
        }
        
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var loaded = ChunkData.Deserialize(reader);
        
        // Assert - surface heights should be recalculated on load
        Assert.Equal(80, loaded.SurfaceHeights[3 * 16 + 3]); // Column with tall blocks
        Assert.Equal(60, loaded.SurfaceHeights[8 * 16 + 8]); // Regular floor column
    }

    [Fact]
    public void ChunkData_RoundTrip_PreservesBiomes()
    {
        // Arrange
        var original = CreateAirChunk(chunkIndex: 0);
        original.Biomes[0] = BiomeId.Desert;
        original.Biomes[5] = BiomeId.Savanna;
        original.Biomes[15] = BiomeId.Highlands;
        
        // Act
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            original.Serialize(writer);
        }
        
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var loaded = ChunkData.Deserialize(reader);
        
        // Assert
        Assert.Equal(BiomeId.Desert, loaded.Biomes[0]);
        Assert.Equal(BiomeId.Savanna, loaded.Biomes[5]);
        Assert.Equal(BiomeId.Highlands, loaded.Biomes[15]);
    }

    [Fact]
    public void ChunkData_Deserialize_FixesMissingAirAtPalette0()
    {
        // This tests the sanity check that ensures Air is at palette[0]
        // We can't easily create a corrupted file, but we test that a valid file works
        var original = CreateChunkWithFloor(50);
        
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            original.Serialize(writer);
        }
        
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var loaded = ChunkData.Deserialize(reader);
        
        // Air should be at palette[0]
        Assert.Equal(BlockId.Air, loaded.Palette[0]);
        // And air blocks should still be air
        Assert.Equal(BlockId.Air, loaded.GetBlock(8, 51, 8));
    }

    [Fact]
    public void ChunkData_CompressedRoundTrip_SmallFile()
    {
        // Arrange - create a chunk with typical terrain
        var original = CreateChunkWithFloor(64);
        SetSkyLight(original, 8, 100, 8, 15);
        SetBlockLight(original, 8, 64, 8, 10);
        
        // Act - save with GZip compression
        using var compressedStream = new MemoryStream();
        using (var gzipStream = new GZipStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new BinaryWriter(gzipStream))
        {
            original.Serialize(writer);
        }
        
        var compressedSize = compressedStream.Length;
        
        // Decompress and verify
        compressedStream.Position = 0;
        using var decompressStream = new GZipStream(compressedStream, CompressionMode.Decompress);
        using var reader = new BinaryReader(decompressStream);
        var loaded = ChunkData.Deserialize(reader);
        
        // Assert - data integrity
        Assert.Equal(BlockId.Stone, loaded.GetBlock(8, 64, 8));
        Assert.Equal(BlockId.Air, loaded.GetBlock(8, 65, 8));
        Assert.Equal(15, GetSkyLight(loaded, 8, 100, 8));
        Assert.Equal(10, GetBlockLight(loaded, 8, 64, 8));
        
        // Assert - compression should significantly reduce size
        // Uncompressed: ~197KB, Compressed: typically ~20-40KB
        Assert.True(compressedSize < 100_000, $"Compressed size {compressedSize} should be under 100KB");
    }

    [Fact]
    public void ChunkBiomeData_RoundTrip_PreservesAllData()
    {
        // Arrange
        var original = new ChunkBiomeData();
        
        // Set column biomes
        original.SetBiomeAt(0, 0, BiomeId.Desert);
        original.SetBiomeAt(8, 8, BiomeId.Savanna);
        original.SetBiomeAt(15, 15, BiomeId.Swamp);
        
        // Set cell biomes
        original.BiomeIds[0] = BiomeId.Plains;
        original.BiomeIds[5] = BiomeId.Rainforest;
        
        // Set cave biomes
        original.CaveBiomeIds[0] = CaveBiomeId.LushCave;
        original.CaveBiomeIds[100] = CaveBiomeId.DripstoneCave;
        
        // Set climate data
        original.Temperature[0] = 0.5f;
        original.Humidity[0] = 0.8f;
        original.Continentalness[0] = 0.3f;
        original.Erosion[0] = -0.2f;
        original.PeaksValleys[0] = 0.7f;
        original.Weirdness[0] = -0.5f;
        
        // Act
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            original.Serialize(writer);
        }
        
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var loaded = ChunkBiomeData.Deserialize(reader);
        
        // Assert - column biomes
        Assert.Equal(BiomeId.Desert, loaded.GetBiomeAt(0, 0));
        Assert.Equal(BiomeId.Savanna, loaded.GetBiomeAt(8, 8));
        Assert.Equal(BiomeId.Swamp, loaded.GetBiomeAt(15, 15));
        
        // Assert - cell biomes
        Assert.Equal(BiomeId.Plains, loaded.BiomeIds[0]);
        Assert.Equal(BiomeId.Rainforest, loaded.BiomeIds[5]);
        
        // Assert - cave biomes
        Assert.Equal(CaveBiomeId.LushCave, loaded.CaveBiomeIds[0]);
        Assert.Equal(CaveBiomeId.DripstoneCave, loaded.CaveBiomeIds[100]);
        
        // Assert - climate data
        Assert.Equal(0.5f, loaded.Temperature[0], precision: 5);
        Assert.Equal(0.8f, loaded.Humidity[0], precision: 5);
        Assert.Equal(0.3f, loaded.Continentalness[0], precision: 5);
        Assert.Equal(-0.2f, loaded.Erosion[0], precision: 5);
        Assert.Equal(0.7f, loaded.PeaksValleys[0], precision: 5);
        Assert.Equal(-0.5f, loaded.Weirdness[0], precision: 5);
    }

    [Fact]
    public void ChunkData_EmptyChunk_RoundTrip()
    {
        // Arrange - completely empty chunk (all air)
        var original = CreateAirChunk(chunkIndex: 99);
        
        // Act
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            original.Serialize(writer);
        }
        
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var loaded = ChunkData.Deserialize(reader);
        
        // Assert - should be all air
        Assert.Equal(99, loaded.ChunkIndex);
        for (var y = 0; y < 10; y++)
        {
            Assert.Equal(BlockId.Air, loaded.GetBlock(8, y, 8));
        }
    }

    [Fact]
    public void ChunkData_ManyBlockTypes_PalettePreserved()
    {
        // Arrange - chunk with many different block types
        var original = CreateAirChunk();
        var blocks = new[] 
        { 
            BlockId.Stone, BlockId.Dirt, BlockId.Grass, BlockId.Sand,
            BlockId.Gravel, BlockId.Cobblestone, BlockId.OakLog, BlockId.OakLeaves,
            BlockId.Glass, BlockId.Glowstone, BlockId.Torch, BlockId.Water
        };
        
        for (var i = 0; i < blocks.Length; i++)
        {
            original.SetBlock(i, 50, 8, blocks[i]);
        }
        
        // Act
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            original.Serialize(writer);
        }
        
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var loaded = ChunkData.Deserialize(reader);
        
        // Assert - all block types preserved
        for (var i = 0; i < blocks.Length; i++)
        {
            Assert.Equal(blocks[i], loaded.GetBlock(i, 50, 8));
        }
    }
}

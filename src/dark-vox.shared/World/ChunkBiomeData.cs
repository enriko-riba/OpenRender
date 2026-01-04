using System;
using System.IO;
using DarkVox.Shared.World;
using DarkVox.Shared.World.Registry;

namespace DarkVox.World;

/// <summary>
/// Stores biome and climate data for a chunk using both:
/// - Per-column biome IDs (16x16) for accurate voxel-resolution borders
/// - Coarse 4x4 grid (16 cells) for climate interpolation
/// Also stores 3D cave biome data using a 4×4×24 grid for underground biome variation.
/// </summary>
public sealed class ChunkBiomeData
{
    public const int ColumnCount = VoxelHelper.ChunkSideSizeSquare;

    public const int GridSize = 4;
    public const int CellCount = GridSize * GridSize;
    public const int BlocksPerCell = VoxelHelper.ChunkSideSize / GridSize;

    public const int BlocksPerCellY = 16;
    public const int GridSizeY = VoxelHelper.ChunkYSize / BlocksPerCellY;
    public const int CaveBiomeCellCount = GridSize * GridSize * GridSizeY;

    public readonly BiomeId[] ColumnBiomes;

    public readonly BiomeId[] BiomeIds = new BiomeId[CellCount];
    public readonly CaveBiomeId[] CaveBiomeIds = new CaveBiomeId[CaveBiomeCellCount];

    public readonly float[] Temperature = new float[CellCount];
    public readonly float[] Humidity = new float[CellCount];
    public readonly float[] Continentalness = new float[CellCount];
    public readonly float[] Erosion = new float[CellCount];
    public readonly float[] PeaksValleys = new float[CellCount];
    public readonly float[] Weirdness = new float[CellCount];

    public ChunkBiomeData()
    {
        ColumnBiomes = new BiomeId[ColumnCount];
        Array.Fill(ColumnBiomes, BiomeId.Plains);
    }

    public BiomeId GetBiomeAt(int localX, int localZ)
    {
        localX = Math.Clamp(localX, 0, VoxelHelper.ChunkSideSize - 1);
        localZ = Math.Clamp(localZ, 0, VoxelHelper.ChunkSideSize - 1);
        return ColumnBiomes[localZ * VoxelHelper.ChunkSideSize + localX];
    }

    public void SetBiomeAt(int localX, int localZ, BiomeId biome)
    {
        localX = Math.Clamp(localX, 0, VoxelHelper.ChunkSideSize - 1);
        localZ = Math.Clamp(localZ, 0, VoxelHelper.ChunkSideSize - 1);
        ColumnBiomes[localZ * VoxelHelper.ChunkSideSize + localX] = biome;
    }

    public BiomeId GetCellBiome(int cellX, int cellZ)
    {
        cellX = Math.Clamp(cellX, 0, GridSize - 1);
        cellZ = Math.Clamp(cellZ, 0, GridSize - 1);
        return BiomeIds[cellZ * GridSize + cellX];
    }

    public (float C, float T, float H, float E, float PV, float W) GetCellClimate(int cellX, int cellZ)
    {
        var idx = cellZ * GridSize + cellX;
        return (Continentalness[idx], Temperature[idx], Humidity[idx], Erosion[idx], PeaksValleys[idx], Weirdness[idx]);
    }

    public static int GetCaveBiomeCellIndex(int cellX, int cellY, int cellZ)
        => cellY * (GridSize * GridSize) + cellZ * GridSize + cellX;

    public CaveBiomeId GetCaveBiomeAt(int localX, int localY, int localZ)
    {
        var cellX = Math.Clamp(localX / BlocksPerCell, 0, GridSize - 1);
        var cellY = Math.Clamp(localY / BlocksPerCellY, 0, GridSizeY - 1);
        var cellZ = Math.Clamp(localZ / BlocksPerCell, 0, GridSize - 1);
        return CaveBiomeIds[GetCaveBiomeCellIndex(cellX, cellY, cellZ)];
    }

    public void SetCaveBiomeAt(int cellX, int cellY, int cellZ, CaveBiomeId biome)
    {
        cellX = Math.Clamp(cellX, 0, GridSize - 1);
        cellY = Math.Clamp(cellY, 0, GridSizeY - 1);
        cellZ = Math.Clamp(cellZ, 0, GridSize - 1);
        CaveBiomeIds[GetCaveBiomeCellIndex(cellX, cellY, cellZ)] = biome;
    }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(ColumnBiomes.Length);
        for (var i = 0; i < ColumnBiomes.Length; i++) writer.Write((byte)ColumnBiomes[i]);

        writer.Write(BiomeIds.Length);
        for (var i = 0; i < BiomeIds.Length; i++) writer.Write((byte)BiomeIds[i]);

        writer.Write(CaveBiomeIds.Length);
        for (var i = 0; i < CaveBiomeIds.Length; i++) writer.Write((byte)CaveBiomeIds[i]);

        writer.Write(Temperature.Length);
        foreach (var v in Temperature) writer.Write(v);

        writer.Write(Humidity.Length);
        foreach (var v in Humidity) writer.Write(v);

        writer.Write(Continentalness.Length);
        foreach (var v in Continentalness) writer.Write(v);

        writer.Write(Erosion.Length);
        foreach (var v in Erosion) writer.Write(v);

        writer.Write(PeaksValleys.Length);
        foreach (var v in PeaksValleys) writer.Write(v);

        writer.Write(Weirdness.Length);
        foreach (var v in Weirdness) writer.Write(v);
    }

    public static ChunkBiomeData Deserialize(BinaryReader reader)
    {
        var data = new ChunkBiomeData();

        var len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.ColumnBiomes[i] = (BiomeId)reader.ReadByte();

        len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.BiomeIds[i] = (BiomeId)reader.ReadByte();

        len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.CaveBiomeIds[i] = (CaveBiomeId)reader.ReadByte();

        len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.Temperature[i] = reader.ReadSingle();

        len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.Humidity[i] = reader.ReadSingle();

        len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.Continentalness[i] = reader.ReadSingle();

        len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.Erosion[i] = reader.ReadSingle();

        len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.PeaksValleys[i] = reader.ReadSingle();

        len = reader.ReadInt32();
        for (var i = 0; i < len; i++) data.Weirdness[i] = reader.ReadSingle();

        return data;
    }
}

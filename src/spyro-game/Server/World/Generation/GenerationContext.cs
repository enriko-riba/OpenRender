using System.Buffers;
using SpyroGame.World;
using SpyroGame.World.Registry;

namespace SpyroGame.Server.World.Generation;

/// <summary>
/// Holds pooled arrays for terrain generation to avoid allocations and ensure thread safety.
/// </summary>
internal sealed class GenerationContext : IDisposable
{
    public const int ColumnCount = VoxelHelper.ChunkSideSizeSquare;
    public const int BufferSize = ColumnCount + 32; // + Padding for SIMD
    public const int ColumnHeightWords = ColumnCount * VoxelHelper.ChunkYSize;
    
    // Sparse sampling constants
    public const int SparseStep = 4;
    public const int SparseSamplesXZ = VoxelHelper.ChunkSideSize / SparseStep + 1;
    public const int SparseSamplesY = VoxelHelper.ChunkYSize / SparseStep + 1;
    public const int SparseSampleCount = SparseSamplesXZ * SparseSamplesXZ;
    public const int SparseVolumeSize = SparseSampleCount * SparseSamplesY;

    // Climate Arrays
    public readonly float[] Continentalness = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] Erosion = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] PeaksValleys = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] Temperature = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] Humidity = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] Weirdness = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] AquiferNoise = ArrayPool<float>.Shared.Rent(BufferSize);

    public readonly float[] Continentalness01 = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] Erosion01 = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] PeaksValleys01 = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] Temperature01 = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] Humidity01 = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] Weirdness01 = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] AquiferNoise01 = ArrayPool<float>.Shared.Rent(BufferSize);

    public readonly float[] WarpX = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] WarpZ = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] WorldX = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] WorldZ = ArrayPool<float>.Shared.Rent(BufferSize);

    // Scratch buffers for ClimateSampler
    public readonly float[] Scratch1 = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] Scratch2 = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] Scratch3 = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] NoiseScratchOctave = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] NoiseScratchX = ArrayPool<float>.Shared.Rent(BufferSize);
    public readonly float[] NoiseScratchZ = ArrayPool<float>.Shared.Rent(BufferSize);

    // Terrain Generator Arrays - MUST be cleared since ArrayPool returns dirty buffers
    public readonly float[] ColumnHeights;
    public readonly int[] ColumnHeightInts;
    public readonly float[] ColumnCliff;
    public readonly float[] Column3DFactor;
    
    public readonly WaterBodyInfo[] ColumnWaterBody = ArrayPool<WaterBodyInfo>.Shared.Rent(ColumnCount);
    public readonly float[] ColumnOceanDistance = ArrayPool<float>.Shared.Rent(ColumnCount);
    public readonly float[] ColumnBeachThreshold = ArrayPool<float>.Shared.Rent(ColumnCount);
    
    public readonly BiomeDefinition?[] ColumnBiomes = ArrayPool<BiomeDefinition?>.Shared.Rent(ColumnCount);

    // 3D Noise Volumes - MUST be cleared since ArrayPool returns dirty buffers
    public readonly float[] CheeseVolume;
    public readonly float[] SpaghettiVolume;
    public readonly float[] OverhangVolume;
    public readonly byte[] CaveMaskVolume;
    
    // Sparse Sampling Buffers
    public readonly float[] SparseSampleX = ArrayPool<float>.Shared.Rent(SparseSampleCount);
    public readonly float[] SparseSampleZ = ArrayPool<float>.Shared.Rent(SparseSampleCount);
    public readonly float[] SparseCheeseGrid;
    public readonly float[] SparseSpaghettiA;
    public readonly float[] SparseSpaghettiB;
    public readonly float[] SparseOverhangGrid;
    public readonly float[] SparseSliceScratch = ArrayPool<float>.Shared.Rent(SparseSampleCount);
    
    // Scratch buffers for 2D/3D noise in Generator
    public readonly float[] SampleScratch2D = ArrayPool<float>.Shared.Rent(ColumnCount);
    public readonly float[] Scratch3DOutput = ArrayPool<float>.Shared.Rent(VoxelHelper.ChunkYSize);

    public GenerationContext()
    {
        // CRITICAL: Clear column height arrays - stale values cause phantom terrain pillars
        ColumnHeights = ArrayPool<float>.Shared.Rent(ColumnCount);
        Array.Clear(ColumnHeights);
        
        ColumnHeightInts = ArrayPool<int>.Shared.Rent(ColumnCount);
        Array.Clear(ColumnHeightInts);
        
        ColumnCliff = ArrayPool<float>.Shared.Rent(ColumnCount);
        Array.Clear(ColumnCliff);
        
        Column3DFactor = ArrayPool<float>.Shared.Rent(ColumnCount);
        Array.Clear(Column3DFactor);
        
        // Clear 3D volumes since ArrayPool returns dirty buffers and stale data
        // causes phantom solid blocks or incorrect terrain
        CheeseVolume = ArrayPool<float>.Shared.Rent(ColumnHeightWords);
        Array.Clear(CheeseVolume);
        
        SpaghettiVolume = ArrayPool<float>.Shared.Rent(ColumnHeightWords);
        Array.Clear(SpaghettiVolume);
        
        OverhangVolume = ArrayPool<float>.Shared.Rent(ColumnHeightWords);
        Array.Clear(OverhangVolume);
        
        CaveMaskVolume = ArrayPool<byte>.Shared.Rent(ColumnHeightWords);
        Array.Clear(CaveMaskVolume);
        
        // Sparse grids also need clearing
        SparseCheeseGrid = ArrayPool<float>.Shared.Rent(SparseVolumeSize);
        Array.Clear(SparseCheeseGrid);
        
        SparseSpaghettiA = ArrayPool<float>.Shared.Rent(SparseVolumeSize);
        Array.Clear(SparseSpaghettiA);
        
        SparseSpaghettiB = ArrayPool<float>.Shared.Rent(SparseVolumeSize);
        Array.Clear(SparseSpaghettiB);
        
        SparseOverhangGrid = ArrayPool<float>.Shared.Rent(SparseVolumeSize);
        Array.Clear(SparseOverhangGrid);
    }

    public void Dispose()
    {
        ArrayPool<float>.Shared.Return(Continentalness);
        ArrayPool<float>.Shared.Return(Erosion);
        ArrayPool<float>.Shared.Return(PeaksValleys);
        ArrayPool<float>.Shared.Return(Temperature);
        ArrayPool<float>.Shared.Return(Humidity);
        ArrayPool<float>.Shared.Return(Weirdness);
        ArrayPool<float>.Shared.Return(AquiferNoise);

        ArrayPool<float>.Shared.Return(Continentalness01);
        ArrayPool<float>.Shared.Return(Erosion01);
        ArrayPool<float>.Shared.Return(PeaksValleys01);
        ArrayPool<float>.Shared.Return(Temperature01);
        ArrayPool<float>.Shared.Return(Humidity01);
        ArrayPool<float>.Shared.Return(Weirdness01);
        ArrayPool<float>.Shared.Return(AquiferNoise01);

        ArrayPool<float>.Shared.Return(WarpX);
        ArrayPool<float>.Shared.Return(WarpZ);
        ArrayPool<float>.Shared.Return(WorldX);
        ArrayPool<float>.Shared.Return(WorldZ);

        ArrayPool<float>.Shared.Return(Scratch1);
        ArrayPool<float>.Shared.Return(Scratch2);
        ArrayPool<float>.Shared.Return(Scratch3);
        ArrayPool<float>.Shared.Return(NoiseScratchOctave);
        ArrayPool<float>.Shared.Return(NoiseScratchX);
        ArrayPool<float>.Shared.Return(NoiseScratchZ);

        ArrayPool<float>.Shared.Return(ColumnHeights);
        ArrayPool<int>.Shared.Return(ColumnHeightInts);
        ArrayPool<float>.Shared.Return(ColumnCliff);
        ArrayPool<float>.Shared.Return(Column3DFactor);
        
        ArrayPool<WaterBodyInfo>.Shared.Return(ColumnWaterBody);
        ArrayPool<float>.Shared.Return(ColumnOceanDistance);
        ArrayPool<float>.Shared.Return(ColumnBeachThreshold);
        
        ArrayPool<BiomeDefinition?>.Shared.Return(ColumnBiomes);

        ArrayPool<float>.Shared.Return(CheeseVolume);
        ArrayPool<float>.Shared.Return(SpaghettiVolume);
        ArrayPool<float>.Shared.Return(OverhangVolume);
        ArrayPool<byte>.Shared.Return(CaveMaskVolume);
        
        ArrayPool<float>.Shared.Return(SparseSampleX);
        ArrayPool<float>.Shared.Return(SparseSampleZ);
        ArrayPool<float>.Shared.Return(SparseCheeseGrid);
        ArrayPool<float>.Shared.Return(SparseSpaghettiA);
        ArrayPool<float>.Shared.Return(SparseSpaghettiB);
        ArrayPool<float>.Shared.Return(SparseOverhangGrid);
        ArrayPool<float>.Shared.Return(SparseSliceScratch);
        
        ArrayPool<float>.Shared.Return(SampleScratch2D);
        ArrayPool<float>.Shared.Return(Scratch3DOutput);
    }
}

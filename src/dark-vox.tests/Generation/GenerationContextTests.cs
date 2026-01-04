using DarkVox.Shared.World;
using DarkVox.Server.World.Generation;
using Xunit;

namespace DarkVox.Tests.Generation;

/// <summary>
/// Tests for GenerationContext array initialization.
/// These tests verify that pooled arrays are properly cleared to prevent
/// stale data from causing terrain generation bugs (phantom columns, etc.).
/// </summary>
public class GenerationContextTests
{
    [Fact]
    public void GenerationContext_ColumnHeights_InitializedToZero()
    {
        // Arrange & Act
        using var ctx = new GenerationContext();
        
        // Assert - all column heights should be zero (not stale values)
        for (var i = 0; i < GenerationContext.ColumnCount; i++)
        {
            Assert.Equal(0f, ctx.ColumnHeights[i]);
        }
    }

    [Fact]
    public void GenerationContext_ColumnHeightInts_InitializedToZero()
    {
        // Arrange & Act
        using var ctx = new GenerationContext();
        
        // Assert - all column height ints should be zero
        for (var i = 0; i < GenerationContext.ColumnCount; i++)
        {
            Assert.Equal(0, ctx.ColumnHeightInts[i]);
        }
    }

    [Fact]
    public void GenerationContext_Column3DFactor_InitializedToZero()
    {
        // Arrange & Act
        using var ctx = new GenerationContext();
        
        // Assert
        for (var i = 0; i < GenerationContext.ColumnCount; i++)
        {
            Assert.Equal(0f, ctx.Column3DFactor[i]);
        }
    }

    [Fact]
    public void GenerationContext_OverhangVolume_InitializedToZero()
    {
        // Arrange & Act
        using var ctx = new GenerationContext();
        
        // Assert - spot check some values in the volume
        var volumeSize = GenerationContext.ColumnCount * VoxelHelper.ChunkYSize;
        Assert.Equal(0f, ctx.OverhangVolume[0]);
        Assert.Equal(0f, ctx.OverhangVolume[volumeSize / 2]);
        Assert.Equal(0f, ctx.OverhangVolume[volumeSize - 1]);
    }

    [Fact]
    public void GenerationContext_CheeseVolume_InitializedToZero()
    {
        // Arrange & Act
        using var ctx = new GenerationContext();
        
        // Assert
        var volumeSize = GenerationContext.ColumnCount * VoxelHelper.ChunkYSize;
        Assert.Equal(0f, ctx.CheeseVolume[0]);
        Assert.Equal(0f, ctx.CheeseVolume[volumeSize / 2]);
        Assert.Equal(0f, ctx.CheeseVolume[volumeSize - 1]);
    }

    [Fact]
    public void GenerationContext_CaveMaskVolume_InitializedToZero()
    {
        // Arrange & Act
        using var ctx = new GenerationContext();
        
        // Assert
        var volumeSize = GenerationContext.ColumnCount * VoxelHelper.ChunkYSize;
        Assert.Equal(0, ctx.CaveMaskVolume[0]);
        Assert.Equal(0, ctx.CaveMaskVolume[volumeSize / 2]);
        Assert.Equal(0, ctx.CaveMaskVolume[volumeSize - 1]);
    }

    [Fact]
    public void GenerationContext_SparseGrids_InitializedToZero()
    {
        // Arrange & Act
        using var ctx = new GenerationContext();
        
        // Assert
        Assert.Equal(0f, ctx.SparseCheeseGrid[0]);
        Assert.Equal(0f, ctx.SparseSpaghettiA[0]);
        Assert.Equal(0f, ctx.SparseSpaghettiB[0]);
        Assert.Equal(0f, ctx.SparseOverhangGrid[0]);
    }

    [Fact]
    public void GenerationContext_MultipleInstances_IndependentArrays()
    {
        // This test verifies that creating multiple contexts doesn't share state
        // Arrange & Act
        using var ctx1 = new GenerationContext();
        ctx1.ColumnHeights[0] = 100f;
        ctx1.ColumnHeightInts[0] = 100;
        
        using var ctx2 = new GenerationContext();
        
        // Assert - ctx2 should have fresh zeroed arrays, not ctx1's values
        Assert.Equal(0f, ctx2.ColumnHeights[0]);
        Assert.Equal(0, ctx2.ColumnHeightInts[0]);
    }

    [Fact]
    public void GenerationContext_AfterDispose_PoolReturnsCleanOnNextRent()
    {
        // This test attempts to catch the dirty buffer scenario
        // by simulating what happens when buffers are reused
        
        // First context - simulate a "tall mountain" chunk
        using (var ctx1 = new GenerationContext())
        {
            // Dirty the arrays with extreme values
            for (var i = 0; i < GenerationContext.ColumnCount; i++)
            {
                ctx1.ColumnHeights[i] = 384f;
                ctx1.ColumnHeightInts[i] = 384;
            }
            
            for (var i = 0; i < ctx1.OverhangVolume.Length; i++)
            {
                ctx1.OverhangVolume[i] = 1.0f;
            }
        } // Dispose returns arrays to pool
        
        // Second context - should get clean arrays regardless of pool state
        using var ctx2 = new GenerationContext();
        
        // Assert - new context should be clean
        // Note: This test may not always catch the bug since ArrayPool
        // doesn't guarantee the same buffer will be returned.
        // But if it IS the same buffer, our clearing should handle it.
        for (var i = 0; i < GenerationContext.ColumnCount; i++)
        {
            Assert.Equal(0f, ctx2.ColumnHeights[i]);
            Assert.Equal(0, ctx2.ColumnHeightInts[i]);
        }
    }
}

using System.Diagnostics;
using OpenRender;

namespace SpyroGame.World;

/// <summary>
/// Performance profiler for terrain generation steps.
/// Tracks timing for each major phase of chunk generation to validate performance budgets.
/// 
/// Target budgets (per chunk):
/// - Climate sampling: &lt;2ms
/// - Height calculation: &lt;2ms  
/// - 3D noise sampling: &lt;4ms
/// - Biome selection: &lt;1ms
/// - Block generation: &lt;3ms
/// - Total: &lt;10ms
/// </summary>
internal sealed class TerrainGenerationProfiler
{
    /// <summary>Generation step identifiers for timing.</summary>
    public enum Step
    {
        /// <summary>Climate parameter sampling (2D noise).</summary>
        ClimateSampling,
        
        /// <summary>Height calculation from climate values.</summary>
        HeightCalculation,
        
        /// <summary>3D noise sampling for caves/overhangs.</summary>
        Noise3DSampling,
        
        /// <summary>Biome selection from climate + height.</summary>
        BiomeSelection,
        
        /// <summary>Block generation and collision spans.</summary>
        BlockGeneration,
        
        /// <summary>Aquifer water level determination (Stage 3).</summary>
        AquiferLookup,
        
        /// <summary>Vegetation placement.</summary>
        Vegetation,

        /// <summary>Collision span generation.</summary>
        CollisionGeneration,

        /// <summary>Total chunk generation time.</summary>
        Total
    }

    private const int StepCount = 9;
    private const int SampleBufferSize = 64; // Rolling average over last N chunks

    private readonly Stopwatch _stepTimer = new();
    private readonly Stopwatch _totalTimer = new();
    private readonly long[] _currentStepTicks = new long[StepCount];
    private readonly double[] _averageMs = new double[StepCount];
    private readonly double[] _maxMs = new double[StepCount];
    private readonly double[] _minMs = new double[StepCount];
    
    // Rolling sample buffers for each step
    private readonly double[][] _sampleBuffers;
    private readonly int[] _sampleIndices = new int[StepCount];
    private int _totalChunks;

    private Step _currentStep = Step.Total;
    private bool _isEnabled = true;

    public TerrainGenerationProfiler()
    {
        _sampleBuffers = new double[StepCount][];
        for (var i = 0; i < StepCount; i++)
        {
            _sampleBuffers[i] = new double[SampleBufferSize];
            _minMs[i] = double.MaxValue;
        }
    }

    /// <summary>Gets or sets whether profiling is enabled.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    /// <summary>Gets the total number of chunks profiled.</summary>
    public int TotalChunks => _totalChunks;

    /// <summary>
    /// Start timing a generation step.
    /// </summary>
    public void BeginStep(Step step)
    {
        if (!_isEnabled) return;
        
        _currentStep = step;
        if (step == Step.Total)
        {
            _totalTimer.Restart();
        }
        else
        {
            _stepTimer.Restart();
        }
    }

    /// <summary>
    /// End timing for the current step.
    /// </summary>
    public void EndStep()
    {
        if (!_isEnabled) return;

        if (_currentStep == Step.Total)
        {
            _currentStepTicks[(int)Step.Total] = _totalTimer.ElapsedTicks;
        }
        else
        {
            _currentStepTicks[(int)_currentStep] = _stepTimer.ElapsedTicks;
        }
    }

    /// <summary>
    /// End timing for a specific step and record it.
    /// </summary>
    public void EndStep(Step step)
    {
        if (!_isEnabled) return;

        if (step == Step.Total)
        {
            _currentStepTicks[(int)Step.Total] = _totalTimer.ElapsedTicks;
        }
        else
        {
            _currentStepTicks[(int)step] = _stepTimer.ElapsedTicks;
        }
    }

    /// <summary>
    /// Finalize profiling for the current chunk and update statistics.
    /// Call this after all steps are complete.
    /// </summary>
    public void FinalizeChunk()
    {
        if (!_isEnabled) return;

        var ticksPerMs = Stopwatch.Frequency / 1000.0;
        _totalChunks++;

        for (var i = 0; i < StepCount; i++)
        {
            var ms = _currentStepTicks[i] / ticksPerMs;
            
            // Update rolling buffer
            var bufferIdx = _sampleIndices[i];
            _sampleBuffers[i][bufferIdx] = ms;
            _sampleIndices[i] = (bufferIdx + 1) % SampleBufferSize;

            // Update min/max
            if (ms > _maxMs[i]) _maxMs[i] = ms;
            if (ms < _minMs[i]) _minMs[i] = ms;

            // Calculate rolling average
            var sampleCount = Math.Min(_totalChunks, SampleBufferSize);
            var sum = 0.0;
            for (var j = 0; j < sampleCount; j++)
            {
                sum += _sampleBuffers[i][j];
            }
            _averageMs[i] = sum / sampleCount;
        }
    }

    /// <summary>
    /// Get the last timing for a step in milliseconds.
    /// </summary>
    public double GetLastMs(Step step)
    {
        var ticksPerMs = Stopwatch.Frequency / 1000.0;
        return _currentStepTicks[(int)step] / ticksPerMs;
    }

    /// <summary>
    /// Get the rolling average timing for a step in milliseconds.
    /// </summary>
    public double GetAverageMs(Step step) => _averageMs[(int)step];

    /// <summary>
    /// Get the maximum timing recorded for a step in milliseconds.
    /// </summary>
    public double GetMaxMs(Step step) => _maxMs[(int)step];

    /// <summary>
    /// Get the minimum timing recorded for a step in milliseconds.
    /// </summary>
    public double GetMinMs(Step step) => _minMs[(int)step] == double.MaxValue ? 0 : _minMs[(int)step];

    /// <summary>
    /// Log current performance statistics.
    /// </summary>
    public void LogStatistics()
    {
        if (_totalChunks == 0) return;

        Log.Debug($"=== Terrain Generation Performance ({_totalChunks} chunks) ===");
        Log.Debug($"Climate:  avg={GetAverageMs(Step.ClimateSampling):F2}ms, " +
                  $"max={GetMaxMs(Step.ClimateSampling):F2}ms, " +
                  $"min={GetMinMs(Step.ClimateSampling):F2}ms");
        Log.Debug($"Height:   avg={GetAverageMs(Step.HeightCalculation):F2}ms, " +
                  $"max={GetMaxMs(Step.HeightCalculation):F2}ms, " +
                  $"min={GetMinMs(Step.HeightCalculation):F2}ms");
        Log.Debug($"3D Noise: avg={GetAverageMs(Step.Noise3DSampling):F2}ms, " +
                  $"max={GetMaxMs(Step.Noise3DSampling):F2}ms, " +
                  $"min={GetMinMs(Step.Noise3DSampling):F2}ms");
        Log.Debug($"Biome:    avg={GetAverageMs(Step.BiomeSelection):F2}ms, " +
                  $"max={GetMaxMs(Step.BiomeSelection):F2}ms, " +
                  $"min={GetMinMs(Step.BiomeSelection):F2}ms");
        Log.Debug($"Blocks:   avg={GetAverageMs(Step.BlockGeneration):F2}ms, " +
                  $"max={GetMaxMs(Step.BlockGeneration):F2}ms, " +
                  $"min={GetMinMs(Step.BlockGeneration):F2}ms");
        Log.Debug($"TOTAL:    avg={GetAverageMs(Step.Total):F2}ms, " +
                  $"max={GetMaxMs(Step.Total):F2}ms, " +
                  $"min={GetMinMs(Step.Total):F2}ms");
        
        // Budget warnings
        if (GetAverageMs(Step.Total) > 10.0)
        {
            Log.Warn($"⚠ Total chunk generation exceeds 10ms budget! " +
                       $"(avg={GetAverageMs(Step.Total):F2}ms)");
        }
        if (GetAverageMs(Step.ClimateSampling) > 2.0)
        {
            Log.Warn($"⚠ Climate sampling exceeds 2ms budget! " +
                       $"(avg={GetAverageMs(Step.ClimateSampling):F2}ms)");
        }
    }

    /// <summary>
    /// Reset all statistics.
    /// </summary>
    public void Reset()
    {
        _totalChunks = 0;
        Array.Clear(_currentStepTicks);
        Array.Clear(_averageMs);
        Array.Clear(_sampleIndices);
        
        for (var i = 0; i < StepCount; i++)
        {
            Array.Clear(_sampleBuffers[i]);
            _maxMs[i] = 0;
            _minMs[i] = double.MaxValue;
        }
    }

    /// <summary>
    /// Get a formatted string with the last chunk's timing breakdown.
    /// </summary>
    public string GetLastChunkSummary()
    {
        return $"Climate={GetLastMs(Step.ClimateSampling):F2}ms, " +
               $"Height={GetLastMs(Step.HeightCalculation):F2}ms, " +
               $"3D={GetLastMs(Step.Noise3DSampling):F2}ms, " +
               $"Biome={GetLastMs(Step.BiomeSelection):F2}ms, " +
               $"Blocks={GetLastMs(Step.BlockGeneration):F2}ms, " +
               $"Total={GetLastMs(Step.Total):F2}ms";
    }
}

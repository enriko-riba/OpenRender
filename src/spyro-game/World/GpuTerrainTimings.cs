using System.Diagnostics;

namespace SpyroGame.World;

/// <summary>
/// Helper class for tracking performance timing of GPU terrain pipeline stages.
/// Provides detailed breakdown of generation, visibility, and compaction times.
/// </summary>
public class GpuTerrainTimings
{
    private readonly Stopwatch sw = new();
    
    // Phase timings
    public double Phase2GenerationMs { get; private set; }
    public double Phase3VisibilityMs { get; private set; }
    public double Phase3CountMs { get; private set; }
    public double Phase3PrefixSumMs { get; private set; }
    public double Phase3CompactionMs { get; private set; }
    public double Phase4SetupMs { get; private set; }
    
    // Aggregate timings
    public double TotalPhase3Ms => Phase3VisibilityMs + Phase3CountMs + Phase3PrefixSumMs + Phase3CompactionMs;
    public double TotalPipelineMs => Phase2GenerationMs + TotalPhase3Ms + Phase4SetupMs;
    
    // Results
    public int ChunkCount { get; private set; }
    public uint VertexCount { get; private set; }
    public uint IndexCount { get; private set; }
    
    public void StartTiming() => sw.Restart();
    
    public void RecordPhase2Generation(int chunkCount)
    {
        Phase2GenerationMs = sw.Elapsed.TotalMilliseconds;
        ChunkCount = chunkCount;
        sw.Restart();
    }
    
    public void RecordPhase3Visibility()
    {
        Phase3VisibilityMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
    }
    
    public void RecordPhase3Count()
    {
        Phase3CountMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
    }
    
    public void RecordPhase3PrefixSum()
    {
        Phase3PrefixSumMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
    }
    
    public void RecordPhase3Compaction(uint vertexCount, uint indexCount)
    {
        Phase3CompactionMs = sw.Elapsed.TotalMilliseconds;
        VertexCount = vertexCount;
        IndexCount = indexCount;
        sw.Restart();
    }
    
    public void RecordPhase4Setup()
    {
        Phase4SetupMs = sw.Elapsed.TotalMilliseconds;
        sw.Stop();
    }
    
    public void Reset()
    {
        Phase2GenerationMs = 0;
        Phase3VisibilityMs = 0;
        Phase3CountMs = 0;
        Phase3PrefixSumMs = 0;
        Phase3CompactionMs = 0;
        Phase4SetupMs = 0;
        ChunkCount = 0;
        VertexCount = 0;
        IndexCount = 0;
    }
}

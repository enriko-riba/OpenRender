namespace SpyroGame.World;

/// <summary>
/// Represents the CPU-built mesh data for a single chunk prior to GPU upload.
/// Stores packed vertex/index arrays that match the compacted format.
/// </summary>
public sealed record ChunkMesh(
    int ChunkIndex,
    byte PlaceholderMask,
    uint[] VertexData,
    uint[] IndexData,
    int VisibleFaceCount,
    int TranslucentFaceCount,
    int WaterFaceCount,
    int AlphaTestFaceCount,
    int MaxSurfaceHeight,
    long CacheVersion,
    long EnqueueId,
    long BuildId)
{
    public int VertexCount => VertexData.Length / 2;
    public int IndexCount => IndexData.Length;
    public int OpaqueFaceCount => Math.Max(0, VisibleFaceCount - TranslucentFaceCount - WaterFaceCount - AlphaTestFaceCount);

    public void Deconstruct(out int chunkIndex, out byte placeholderMask, out uint[] vertices, out uint[] indices, out int visibleFaces)
    {
        chunkIndex = ChunkIndex;
        placeholderMask = PlaceholderMask;
        vertices = VertexData;
        indices = IndexData;
        visibleFaces = VisibleFaceCount;
    }

    public ReadOnlySpan<uint> VertexSpan => VertexData;
    public ReadOnlySpan<uint> IndexSpan => IndexData;
}

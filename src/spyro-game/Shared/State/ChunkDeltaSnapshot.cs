namespace SpyroGame.Shared.State;

public readonly record struct ChunkDeltaSnapshot(int[] LoadedChunkIndices, int[] UnloadedChunkIndices)
{
    public bool HasChanges => LoadedChunkIndices.Length > 0 || UnloadedChunkIndices.Length > 0;
}

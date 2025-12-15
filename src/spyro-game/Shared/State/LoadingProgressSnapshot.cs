namespace SpyroGame.Shared.State;

public readonly record struct LoadingProgressSnapshot(
    int DesiredChunkCount,
    int ReadyChunkCount,
    int GeneratingChunkCount);

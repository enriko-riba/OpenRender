namespace SpyroGame.Shared.State;

public readonly record struct GameStateSnapshot(
    ulong TickId,
    double ServerTimeSeconds,
    PlayerSnapshot Player,
    ChunkDeltaSnapshot? ChunkDelta);

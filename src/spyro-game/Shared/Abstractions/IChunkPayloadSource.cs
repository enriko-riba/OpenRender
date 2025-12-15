using SpyroGame.Shared.State;

namespace SpyroGame.Shared.Abstractions;

/// <summary>
/// Optional server capability: provides outgoing chunk voxel payloads to send to clients.
/// This is intentionally separate from <see cref="IGameServer"/> so non-streaming servers don't need to implement it.
/// </summary>
public interface IChunkPayloadSource
{
    bool TryDequeueOutgoingChunkPayload(PlayerId playerId, out int chunkIndex, out byte[] payload);
}

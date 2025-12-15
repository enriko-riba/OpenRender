using SpyroGame.Shared.Commands;
using SpyroGame.Shared.Input;
using SpyroGame.Shared.State;

namespace SpyroGame.Shared.Net;

public interface IClientToServerMessage;
public interface IServerToClientMessage;

public readonly record struct ClientInputMessage(PlayerId PlayerId, PlayerInputCommand Input) : IClientToServerMessage;
public readonly record struct ClientBreakBlockMessage(PlayerId PlayerId, BreakBlockCommand Command) : IClientToServerMessage;
public readonly record struct ClientPlaceBlockMessage(PlayerId PlayerId, PlaceBlockCommand Command) : IClientToServerMessage;

public readonly record struct ClientHelloMessage(PlayerId PlayerId) : IClientToServerMessage;

public readonly record struct ServerStateMessage(PlayerId PlayerId, GameStateSnapshot Snapshot) : IServerToClientMessage;

public readonly record struct ServerLoadingProgressMessage(PlayerId PlayerId, LoadingProgressSnapshot Progress) : IServerToClientMessage;

public readonly record struct ServerGameStartMessage(PlayerId PlayerId) : IServerToClientMessage;

/// <summary>
/// Server-to-client payload for voxel data of a single chunk.
/// This is intentionally opaque bytes so the transport remains decoupled from world/GL concerns.
/// </summary>
public readonly record struct ServerChunkPayloadMessage(PlayerId PlayerId, int ChunkIndex, byte[] Payload) : IServerToClientMessage;

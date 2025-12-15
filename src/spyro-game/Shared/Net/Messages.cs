using SpyroGame.Shared.Commands;
using SpyroGame.Shared.Input;
using SpyroGame.Shared.State;

namespace SpyroGame.Shared.Net;

public interface IClientToServerMessage;
public interface IServerToClientMessage;

public readonly record struct ClientInputMessage(PlayerId PlayerId, PlayerInputCommand Input) : IClientToServerMessage;
public readonly record struct ClientBreakBlockMessage(PlayerId PlayerId, BreakBlockCommand Command) : IClientToServerMessage;
public readonly record struct ClientPlaceBlockMessage(PlayerId PlayerId, PlaceBlockCommand Command) : IClientToServerMessage;

public readonly record struct ServerStateMessage(PlayerId PlayerId, GameStateSnapshot Snapshot) : IServerToClientMessage;

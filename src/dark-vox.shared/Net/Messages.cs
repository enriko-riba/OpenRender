using DarkVox.Shared.Commands;
using DarkVox.Shared.Input;
using DarkVox.Shared.State;

namespace DarkVox.Shared.Net;

public interface IClientToServerMessage;
public interface IServerToClientMessage;

public readonly record struct ClientInputMessage(PlayerId PlayerId, PlayerInputCommand Input) : IClientToServerMessage;
public readonly record struct ClientBreakBlockMessage(PlayerId PlayerId, BreakBlockCommand Command) : IClientToServerMessage;
public readonly record struct ClientPlaceBlockMessage(PlayerId PlayerId, PlaceBlockCommand Command) : IClientToServerMessage;
public readonly record struct ClientEatFoodMessage(PlayerId PlayerId, EatFoodCommand Command) : IClientToServerMessage;

/// <summary>
/// Client requests an attack on a mob.
/// Server validates reach, cooldown, line-of-sight, and applies damage.
/// </summary>
public readonly record struct ClientAttackMobMessage(PlayerId PlayerId, MobId TargetMob) : IClientToServerMessage;

public readonly record struct ClientHelloMessage(PlayerId PlayerId) : IClientToServerMessage;

/// <summary>
/// Client requests moving an item between inventory slots.
/// Server validates and executes the move, then sends updated state.
/// </summary>
public readonly record struct ClientInventoryMoveMessage(PlayerId PlayerId, InventoryMoveCommand Command) : IClientToServerMessage;

/// <summary>
/// Client requests returning an item from a hotbar slot to inventory storage.
/// Server clears the hotbar slot and redistributes the item via ReturnItemToStorage.
/// </summary>
public readonly record struct ClientReturnToStorageMessage(PlayerId PlayerId, ReturnToStorageCommand Command) : IClientToServerMessage;

/// <summary>
/// Client requests moving an item between inventory and crafting grid.
/// </summary>
public readonly record struct ClientContainerMoveMessage(PlayerId PlayerId, ContainerMoveCommand Command) : IClientToServerMessage;

/// <summary>
/// Client requests crafting the current result from the 2x2 grid.
/// </summary>
public readonly record struct ClientCraftFromGridMessage(PlayerId PlayerId, CraftFromGridCommand Command) : IClientToServerMessage;

public readonly record struct ServerStateMessage(PlayerId PlayerId, GameStateSnapshot Snapshot) : IServerToClientMessage;

public readonly record struct ServerMobStateMessage(PlayerId PlayerId, MobStateSnapshot Snapshot) : IServerToClientMessage;

public readonly record struct ServerWorldTimeMessage(PlayerId PlayerId, WorldTimeSnapshot Snapshot) : IServerToClientMessage;

public readonly record struct ServerLoadingProgressMessage(PlayerId PlayerId, LoadingProgressSnapshot Progress) : IServerToClientMessage;

public readonly record struct ServerGameStartMessage(PlayerId PlayerId) : IServerToClientMessage;

/// <summary>
/// Server-to-client payload for voxel data of a single chunk.
/// This is intentionally opaque bytes so the transport remains decoupled from world/GL concerns.
/// </summary>
public readonly record struct ServerChunkPayloadMessage(PlayerId PlayerId, int ChunkIndex, byte[] Payload) : IServerToClientMessage;


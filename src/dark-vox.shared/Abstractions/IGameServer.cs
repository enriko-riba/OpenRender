using DarkVox.Shared.Commands;
using DarkVox.Shared.Input;
using DarkVox.Shared.State;

namespace DarkVox.Shared.Abstractions;

public interface IGameServer
{
    void Submit(PlayerId playerId, PlayerInputCommand input);
    void Submit(PlayerId playerId, BreakBlockCommand command);
    void Submit(PlayerId playerId, PlaceBlockCommand command);
    void Submit(PlayerId playerId, EatFoodCommand command);
    void Submit(PlayerId playerId, InventoryMoveCommand command);
    void Submit(PlayerId playerId, ReturnToStorageCommand command);

    void Tick(double elapsedSeconds);

    bool TryGetSnapshot(PlayerId playerId, out GameStateSnapshot snapshot);
}

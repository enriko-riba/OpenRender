using SpyroGame.Shared.Commands;
using SpyroGame.Shared.Input;
using SpyroGame.Shared.State;

namespace SpyroGame.Shared.Abstractions;

public interface IGameServer
{
    void Submit(PlayerId playerId, PlayerInputCommand input);
    void Submit(PlayerId playerId, BreakBlockCommand command);
    void Submit(PlayerId playerId, PlaceBlockCommand command);
    void Submit(PlayerId playerId, EatFoodCommand command);
    void Submit(PlayerId playerId, InventoryMoveCommand command);

    void Tick(double elapsedSeconds);

    bool TryGetSnapshot(PlayerId playerId, out GameStateSnapshot snapshot);
}

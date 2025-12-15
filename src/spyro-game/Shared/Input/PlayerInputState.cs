using OpenTK.Mathematics;

namespace SpyroGame.Shared.Input;

public readonly record struct PlayerInputState(
    Vector2 MoveAxes,
    float VerticalAxis,
    bool SprintHeld,
    bool CrouchHeld);

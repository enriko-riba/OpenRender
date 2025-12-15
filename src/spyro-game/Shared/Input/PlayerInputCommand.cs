using OpenTK.Mathematics;

namespace SpyroGame.Shared.Input;

/// <summary>
/// Client-produced, server-consumed player intent for a single simulation tick.
/// This is the first step toward a multiplayer client/server split.
/// </summary>
public readonly record struct PlayerInputCommand(
    Vector2 MoveAxes,
    float VerticalAxis,
    Vector2 LookDelta,
    bool JumpPressed,
    bool SprintHeld,
    bool CrouchHeld,
    bool? SetGhostMode,
    int? SelectHotbarSlot,
    int HotbarScrollDelta);

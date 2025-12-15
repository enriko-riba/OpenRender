using OpenTK.Mathematics;

namespace SpyroGame.Shared.State;

public readonly record struct PlayerSnapshot(
    Vector3 Position,
    Vector3 Direction,
    Vector3 Velocity,
    bool IsGrounded,
    bool IsGhostMode,
    int SelectedHotbarSlot,
    PlayerAttributesSnapshot Attributes);

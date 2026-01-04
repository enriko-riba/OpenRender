namespace DarkVox.Shared.State;

public readonly record struct PlayerAttributesSnapshot(
    int MaxHealth,
    int Health,
    int MaxFood,
    int Food,
    float Saturation,
    float Exhaustion);

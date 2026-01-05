namespace DarkVox.Shared.World.Registry;

public enum GameObjectTransformationKind : byte
{
    BreakDrop = 0,
    Smelting = 1,
}

public readonly record struct GameObjectTransformationKey(GameObjectId Source, GameObjectTransformationKind Kind);

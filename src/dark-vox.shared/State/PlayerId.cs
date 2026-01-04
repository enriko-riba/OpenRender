namespace DarkVox.Shared.State;

public readonly record struct PlayerId(System.Guid Value)
{
    public static PlayerId New() => new(System.Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

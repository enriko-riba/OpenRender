namespace DarkVox.Shared.State;

/// <summary>
/// Unique identifier for a server-authoritative mob entity.
/// </summary>
public readonly record struct MobId(ulong Value)
{
    public override string ToString() => Value.ToString();
}

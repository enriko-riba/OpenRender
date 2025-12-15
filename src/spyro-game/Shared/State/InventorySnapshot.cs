namespace SpyroGame.Shared.State;

public readonly struct InventorySnapshot : IEquatable<InventorySnapshot>
{
    public int Version { get; }
    public InventoryItemSnapshot[] Slots { get; }

    public InventorySnapshot(int version, InventoryItemSnapshot[] slots)
    {
        Version = version;
        Slots = slots;
    }

    public bool Equals(InventorySnapshot other)
    {
        if (Version != other.Version)
        {
            return false;
        }

        // If version matches, slots are expected to match too, but compare defensively.
        if (ReferenceEquals(Slots, other.Slots))
        {
            return true;
        }

        if (Slots == null || other.Slots == null)
        {
            return Slots == other.Slots;
        }

        if (Slots.Length != other.Slots.Length)
        {
            return false;
        }

        for (var i = 0; i < Slots.Length; i++)
        {
            if (!Slots[i].Equals(other.Slots[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj)
        => obj is InventorySnapshot other && Equals(other);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(Version);

        // Keep hashing cheap; version already captures most changes.
        // Slots hash is intentionally omitted.
        return hc.ToHashCode();
    }

    public static bool operator ==(InventorySnapshot left, InventorySnapshot right) => left.Equals(right);

    public static bool operator !=(InventorySnapshot left, InventorySnapshot right) => !left.Equals(right);
}

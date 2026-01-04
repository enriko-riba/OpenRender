using OpenTK.Mathematics;

namespace DarkVox.Shared.State;

/// <summary>
/// Serializable player save data for persistence.
/// Follows Minecraft-style .dat file format conceptually.
/// </summary>
public sealed class PlayerSaveData
{
    /// <summary>
    /// Current file format version for migration support.
    /// </summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    // Position and orientation
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float PositionZ { get; set; }
    public float DirectionX { get; set; }
    public float DirectionY { get; set; }
    public float DirectionZ { get; set; }

    // Game mode
    public bool IsGhostMode { get; set; }

    // Attributes
    public int Health { get; set; } = 20;
    public int MaxHealth { get; set; } = 20;
    public int Food { get; set; } = 20;
    public int MaxFood { get; set; } = 20;
    public float Saturation { get; set; } = 5.0f;
    public float Exhaustion { get; set; }

    // Inventory
    public int SelectedHotbarSlot { get; set; }
    public PlayerSaveInventorySlot[] InventorySlots { get; set; } = [];

    /// <summary>
    /// Creates save data from a player snapshot.
    /// </summary>
    public static PlayerSaveData FromSnapshot(
        Vector3 position,
        Vector3 direction,
        bool isGhostMode,
        PlayerAttributesSnapshot attributes,
        InventorySnapshot inventory,
        int selectedHotbarSlot)
    {
        var slots = new PlayerSaveInventorySlot[inventory.Slots?.Length ?? 0];
        for (int i = 0; i < slots.Length; i++)
        {
            var slot = inventory.Slots![i];
            slots[i] = new PlayerSaveInventorySlot
            {
                SlotIndex = i,
                ItemId = slot.Item.ToString(),
                Count = slot.Count
            };
        }

        return new PlayerSaveData
        {
            Version = CurrentVersion,
            PositionX = position.X,
            PositionY = position.Y,
            PositionZ = position.Z,
            DirectionX = direction.X,
            DirectionY = direction.Y,
            DirectionZ = direction.Z,
            IsGhostMode = isGhostMode,
            Health = attributes.Health,
            MaxHealth = attributes.MaxHealth,
            Food = attributes.Food,
            MaxFood = attributes.MaxFood,
            Saturation = attributes.Saturation,
            Exhaustion = attributes.Exhaustion,
            SelectedHotbarSlot = selectedHotbarSlot,
            InventorySlots = slots
        };
    }

    public Vector3 GetPosition() => new(PositionX, PositionY, PositionZ);
    public Vector3 GetDirection() => new(DirectionX, DirectionY, DirectionZ);
}

/// <summary>
/// Serializable inventory slot for save data.
/// </summary>
public sealed class PlayerSaveInventorySlot
{
    public int SlotIndex { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>
/// Debug/development constants for player identification.
/// In production, this would be replaced with proper authentication.
/// </summary>
public static class DebugPlayerIdentity
{
    /// <summary>
    /// Hardcoded debug UUID for single-player development.
    /// This simulates what would normally come from an authentication system.
    /// </summary>
    public static readonly Guid DebugPlayerUuid = new("12345678-1234-1234-1234-123456789abc");

    /// <summary>
    /// Creates a PlayerId from the debug UUID.
    /// </summary>
    public static PlayerId GetDebugPlayerId() => new(DebugPlayerUuid);
}

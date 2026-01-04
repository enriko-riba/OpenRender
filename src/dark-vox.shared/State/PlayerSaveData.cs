using System.Text.Json;
using System.Text.Json.Serialization;
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
/// Service for saving and loading player data.
/// </summary>
public static class PlayerPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const string PlayerFolderName = "players";

    /// <summary>
    /// Gets the save directory path for player data.
    /// </summary>
    public static string GetSaveDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        var saveDir = Path.Combine(baseDir, "saves", "players");
        return saveDir;
    }

    /// <summary>
    /// Gets the save directory path for player data inside a specific world save folder.
    /// </summary>
    public static string GetSaveDirectory(string worldSaveDirectory)
    {
        if (string.IsNullOrWhiteSpace(worldSaveDirectory))
        {
            throw new ArgumentException("World save directory is required.", nameof(worldSaveDirectory));
        }

        // World-scoped player saves live directly inside the world save directory.
        // (e.g., save/<world>_<seed>/<uuid>.dat)
        return worldSaveDirectory;
    }

    /// <summary>
    /// Gets the full path for a player's save file.
    /// </summary>
    public static string GetPlayerSavePath(Guid playerUuid)
    {
        return Path.Combine(GetSaveDirectory(), $"{playerUuid}.dat");
    }

    /// <summary>
    /// Gets the full path for a player's save file inside a specific world save folder.
    /// </summary>
    public static string GetPlayerSavePath(Guid playerUuid, string worldSaveDirectory)
    {
        return Path.Combine(GetSaveDirectory(worldSaveDirectory), $"{playerUuid}.dat");
    }

    /// <summary>
    /// Saves player data to disk.
    /// </summary>
    public static void Save(Guid playerUuid, PlayerSaveData data)
    {
        var dir = GetSaveDirectory();
        Directory.CreateDirectory(dir);

        var path = GetPlayerSavePath(playerUuid);
        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Saves player data to disk inside a specific world save folder.
    /// </summary>
    public static void Save(Guid playerUuid, PlayerSaveData data, string worldSaveDirectory)
    {
        var dir = GetSaveDirectory(worldSaveDirectory);
        Directory.CreateDirectory(dir);

        var path = GetPlayerSavePath(playerUuid, worldSaveDirectory);
        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Loads player data from disk if it exists.
    /// </summary>
    public static PlayerSaveData? Load(Guid playerUuid)
    {
        var path = GetPlayerSavePath(playerUuid);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PlayerSaveData>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayerPersistence] Failed to load player data: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads player data from disk inside a specific world save folder if it exists.
    /// Falls back to the legacy global save path for backwards compatibility.
    /// </summary>
    public static PlayerSaveData? Load(Guid playerUuid, string worldSaveDirectory)
    {
        var worldRootPath = GetPlayerSavePath(playerUuid, worldSaveDirectory);
        var worldPlayersPath = Path.Combine(worldSaveDirectory, PlayerFolderName, $"{playerUuid}.dat");
        var legacyPath = GetPlayerSavePath(playerUuid);

        var path = File.Exists(worldPlayersPath)
            ? worldPlayersPath
            : (File.Exists(worldRootPath) ? worldRootPath : legacyPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PlayerSaveData>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayerPersistence] Failed to load player data: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Checks if a save file exists for the given player UUID.
    /// </summary>
    public static bool Exists(Guid playerUuid)
    {
        return File.Exists(GetPlayerSavePath(playerUuid));
    }

    /// <summary>
    /// Checks if a save file exists for the given player UUID inside a specific world save folder.
    /// </summary>
    public static bool Exists(Guid playerUuid, string worldSaveDirectory)
    {
        return File.Exists(Path.Combine(worldSaveDirectory, PlayerFolderName, $"{playerUuid}.dat"))
               || File.Exists(GetPlayerSavePath(playerUuid, worldSaveDirectory));
    }

    /// <summary>
    /// Deletes a player's save file if it exists.
    /// </summary>
    public static void Delete(Guid playerUuid)
    {
        var path = GetPlayerSavePath(playerUuid);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Deletes a player's save file inside a specific world save folder if it exists.
    /// </summary>
    public static void Delete(Guid playerUuid, string worldSaveDirectory)
    {
        var worldRootPath = GetPlayerSavePath(playerUuid, worldSaveDirectory);
        var worldPlayersPath = Path.Combine(worldSaveDirectory, PlayerFolderName, $"{playerUuid}.dat");

        if (File.Exists(worldPlayersPath)) File.Delete(worldPlayersPath);
        if (File.Exists(worldRootPath)) File.Delete(worldRootPath);
    }
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

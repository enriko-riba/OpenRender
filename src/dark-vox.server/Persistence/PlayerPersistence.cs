using System.Text.Json;
using DarkVox.Shared.Abstractions;
using DarkVox.Shared.State;

namespace DarkVox.Server.Persistence;

/// <summary>
/// Service for saving and loading player data.
/// Server-only: player persistence is authoritative on the server.
/// </summary>
public static class PlayerPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Gets the save directory path for player data inside a specific world save folder.
    /// Canonical format: save/&lt;world&gt;_&lt;seed&gt;/&lt;uuid&gt;.dat
    /// </summary>
    public static string GetSaveDirectory(string worldSaveDirectory)
    {
        if (string.IsNullOrWhiteSpace(worldSaveDirectory))
        {
            throw new ArgumentException("World save directory is required.", nameof(worldSaveDirectory));
        }

        return worldSaveDirectory;
    }

    public static string GetPlayerSavePath(Guid playerUuid, string worldSaveDirectory)
        => Path.Combine(GetSaveDirectory(worldSaveDirectory), $"{playerUuid}.dat");

    public static void Save(Guid playerUuid, PlayerSaveData data, string worldSaveDirectory)
    {
        var dir = GetSaveDirectory(worldSaveDirectory);
        Directory.CreateDirectory(dir);

        var path = GetPlayerSavePath(playerUuid, worldSaveDirectory);
        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Loads player data from the canonical world save folder path.
    /// Canonical format: save/&lt;world&gt;_&lt;seed&gt;/&lt;uuid&gt;.dat
    /// </summary>
    public static PlayerSaveData? Load(Guid playerUuid, string worldSaveDirectory, ILog? log = null)
    {
        var path = GetPlayerSavePath(playerUuid, worldSaveDirectory);
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
            (log ?? NullLog.Instance).Error($"[PlayerPersistence] Failed to load player data: {ex.Message}");
            return null;
        }
    }

    public static bool Exists(Guid playerUuid, string worldSaveDirectory)
        => File.Exists(GetPlayerSavePath(playerUuid, worldSaveDirectory));

    public static void Delete(Guid playerUuid, string worldSaveDirectory)
    {
        var path = GetPlayerSavePath(playerUuid, worldSaveDirectory);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

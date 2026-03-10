using System.Text.Json;

namespace MonocoBot.Tools;

public class SteamKeyStore
{
    private readonly string _filePath;
    private readonly Lock _lock = new();
    private Dictionary<string, RegisteredProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public SteamKeyStore()
    {
        _filePath = Path.Combine(AppContext.BaseDirectory, "steam_keys.json");
        Load();
    }

    public void Register(string steamId, string apiKey, string displayName)
    {
        lock (_lock)
        {
            _profiles[steamId] = new RegisteredProfile(apiKey, displayName);
            Save();
        }
    }

    public void Unregister(string steamId)
    {
        lock (_lock)
        {
            _profiles.Remove(steamId);
            Save();
        }
    }

    public string? GetApiKey(string steamId)
    {
        lock (_lock)
        {
            return _profiles.TryGetValue(steamId, out var profile) ? profile.ApiKey : null;
        }
    }

    public bool HasKey(string steamId)
    {
        lock (_lock)
        {
            return _profiles.ContainsKey(steamId);
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath))
            return;

        try
        {
            var json = File.ReadAllText(_filePath);
            _profiles = JsonSerializer.Deserialize<Dictionary<string, RegisteredProfile>>(json)
                        ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _profiles = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_profiles, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    public record RegisteredProfile(string ApiKey, string DisplayName);
}

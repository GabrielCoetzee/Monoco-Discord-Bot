using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MonocoBot.Configuration;

namespace MonocoBot.Tools;

public class SteamTools
{
    private readonly HttpClient _httpClient;
    private readonly BotOptions _options;
    private readonly SteamKeyStore _keyStore;

    public SteamTools(HttpClient httpClient, IOptions<BotOptions> options, SteamKeyStore keyStore)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _keyStore = keyStore;
    }

    private string? ResolveApiKey(string? steamId = null)
    {
        if (!string.IsNullOrEmpty(steamId))
        {
            var personalKey = _keyStore.GetApiKey(steamId);
            if (!string.IsNullOrEmpty(personalKey))
                return personalKey;
        }

        return string.IsNullOrEmpty(_options.SteamApiKey) ? null : _options.SteamApiKey;
    }

    [Description("Gets the list of owned games for a Steam user by their Steam 64-bit ID. " +
        "Works for public profiles, and also for private profiles if the user has registered their API key with RegisterSteamApiKey.")]
    public async Task<string> GetSteamLibrary(
        [Description("The Steam 64-bit ID (e.g., '76561198012345678')")] string steamId)
    {
        try
        {
            var apiKey = ResolveApiKey(steamId);
            if (apiKey is null)
                return "Error: No Steam API key available. Either set 'Bot:SteamApiKey' in config or ask the user to register their key with RegisterSteamApiKey.";

            var url = $"http://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/?key={apiKey}&steamid={steamId}&include_appinfo=1&format=json";
            var response = await _httpClient.GetStringAsync(url);
            var doc = JsonDocument.Parse(response);

            var resp = doc.RootElement.GetProperty("response");
            if (!resp.TryGetProperty("games", out var games))
                return "No games found. The profile may be private — ask the user to register their Steam API key with RegisterSteamApiKey.";

            var gameList = new List<(string Name, double Hours)>();
            foreach (var game in games.EnumerateArray())
            {
                var name = game.GetProperty("name").GetString() ?? "Unknown";
                var playtime = game.GetProperty("playtime_forever").GetInt32();
                gameList.Add((name, playtime / 60.0));
            }

            gameList.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            var totalGames = resp.GetProperty("game_count").GetInt32();

            var lines = gameList.Select(g => $"- {g.Name} ({g.Hours:F1} hours played)");
            return $"**Steam Library** ({totalGames} games):\n{string.Join("\n", lines)}";
        }
        catch (Exception ex)
        {
            return $"Failed to get Steam library: {ex.Message}";
        }
    }

    [Description("Resolves a Steam vanity URL name (the custom URL part) to a numeric Steam 64-bit ID. " +
        "For example, if the profile URL is steamcommunity.com/id/gaben, the vanity name is 'gaben'.")]
    public async Task<string> ResolveSteamVanityName(
        [Description("The vanity URL name (e.g., 'gaben')")] string vanityName)
    {
        try
        {
            var apiKey = ResolveApiKey();
            if (apiKey is null)
                return "Error: Steam API key is not configured.";

            var url = $"http://api.steampowered.com/ISteamUser/ResolveVanityURL/v0001/?key={apiKey}&vanityurl={vanityName}";
            var response = await _httpClient.GetStringAsync(url);
            var doc = JsonDocument.Parse(response);

            var resp = doc.RootElement.GetProperty("response");
            if (resp.GetProperty("success").GetInt32() == 1)
            {
                var steamId = resp.GetProperty("steamid").GetString();
                return $"Resolved '{vanityName}' to Steam ID: {steamId}";
            }

            return $"Could not resolve vanity name '{vanityName}'. It may not exist.";
        }
        catch (Exception ex)
        {
            return $"Failed to resolve vanity name: {ex.Message}";
        }
    }

    [Description("Gets the Steam wishlist for a user. The wishlist must be set to public.")]
    public async Task<string> GetSteamWishlist(
        [Description("The Steam 64-bit ID of the user")] string steamId)
    {
        try
        {
            var url = $"https://store.steampowered.com/wishlist/profiles/{steamId}/wishlistdata/?p=0";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0");

            var response = await _httpClient.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (json is "[]" or "" or "null")
                return "Wishlist is empty or private.";

            var doc = JsonDocument.Parse(json);
            var items = new List<string>();

            foreach (var item in doc.RootElement.EnumerateObject())
            {
                if (item.Value.TryGetProperty("name", out var name))
                    items.Add($"- {name.GetString()}");
            }

            items.Sort(StringComparer.OrdinalIgnoreCase);
            return $"**Steam Wishlist** ({items.Count} items):\n{string.Join("\n", items)}";
        }
        catch (Exception ex)
        {
            return $"Failed to get wishlist: {ex.Message}";
        }
    }

    [Description("Searches a Steam user's friend list by display name and returns matching friends with their Steam IDs. " +
        "Works for public friend lists, and also for private ones if the user has registered their API key.")]
    public async Task<string> FindFriendByName(
        [Description("The Steam 64-bit ID of the user whose friend list to search")] string steamId,
        [Description("The display name (or part of it) to search for in the friend list")] string friendName)
    {
        try
        {
            var apiKey = ResolveApiKey(steamId);

            if (apiKey is null)
                return "Error: No Steam API key available.";

            var friends = await GetFriendSteamIds(steamId, apiKey);

            if (friends.Count == 0)
                return "Friend list is empty or the profile's friend list is private.";

            var summaries = await GetPlayerSummaries(friends, apiKey);

            var matches = summaries
                .Where(s => s.Name.Contains(friendName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
                return $"No friends found matching '{friendName}'. Use GetFriendsList to see all friends.";

            var lines = matches.Select(s =>
                $"- **{s.Name}** — Steam ID: `{s.SteamId}` (profile: {s.ProfileUrl})");

            return $"Found {matches.Count} match(es) for '{friendName}':\n{string.Join("\n", lines)}";
        }
        catch (Exception ex)
        {
            return $"Failed to search friends: {ex.Message}";
        }
    }

    [Description("Lists all friends on a Steam user's friend list with their display names and Steam IDs. " +
        "Works for public friend lists, and also for private ones if the user has registered their API key.")]
    public async Task<string> GetFriendsList(
        [Description("The Steam 64-bit ID of the user whose friend list to retrieve")] string steamId)
    {
        try
        {
            var apiKey = ResolveApiKey(steamId);
            if (apiKey is null)
                return "Error: No Steam API key available.";

            var friends = await GetFriendSteamIds(steamId, apiKey);

            if (friends.Count == 0)
                return "Friend list is empty or the profile's friend list is private.";

            var summaries = await GetPlayerSummaries(friends, apiKey);
            summaries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            var lines = summaries.Select(s => $"- {s.Name}  (`{s.SteamId}`)");
            return $"**Friends List** ({summaries.Count}):\n{string.Join("\n", lines)}";
        }
        catch (Exception ex)
        {
            return $"Failed to get friends list: {ex.Message}";
        }
    }

    [Description("Registers a user's personal Steam Web API key so the bot can access their profile even if it's set to private. " +
        "The user can get a free key at https://steamcommunity.com/dev/apikey. " +
        "IMPORTANT: Always tell the user to send their API key via DM to the bot, never in a public channel.")]
    public async Task<string> RegisterSteamApiKey(
        [Description("The user's Steam vanity name or 64-bit ID")] string steamIdOrVanity,
        [Description("The user's personal Steam Web API key from steamcommunity.com/dev/apikey")] string apiKey)
    {
        try
        {
            // Resolve to a Steam 64-bit ID if a vanity name was provided
            var steamId = steamIdOrVanity;
            if (!steamIdOrVanity.All(char.IsDigit) || steamIdOrVanity.Length < 15)
            {
                var resolveUrl = $"http://api.steampowered.com/ISteamUser/ResolveVanityURL/v0001/?key={apiKey}&vanityurl={steamIdOrVanity}";
                var resolveResponse = await _httpClient.GetStringAsync(resolveUrl);
                var resolveDoc = JsonDocument.Parse(resolveResponse);
                var resp = resolveDoc.RootElement.GetProperty("response");

                if (resp.GetProperty("success").GetInt32() != 1)
                    return $"Could not resolve '{steamIdOrVanity}' to a Steam ID. Check the name and make sure the API key is valid.";

                steamId = resp.GetProperty("steamid").GetString()!;
            }

            // Verify the key works by fetching the player summary
            var summaryUrl = $"http://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/?key={apiKey}&steamids={steamId}";
            var summaryResponse = await _httpClient.GetStringAsync(summaryUrl);
            var summaryDoc = JsonDocument.Parse(summaryResponse);

            var players = summaryDoc.RootElement.GetProperty("response").GetProperty("players");
            var displayName = "Unknown";
            foreach (var player in players.EnumerateArray())
            {
                if (player.GetProperty("steamid").GetString() == steamId)
                {
                    displayName = player.GetProperty("personaname").GetString() ?? "Unknown";
                    break;
                }
            }

            _keyStore.Register(steamId, apiKey, displayName);
            return $"Successfully registered Steam API key for **{displayName}** (ID: `{steamId}`). " +
                   $"The bot can now access this profile's games, wishlist, and friend list even if it's private.";
        }
        catch (Exception ex)
        {
            return $"Failed to register Steam API key: {ex.Message}. Make sure the API key is valid.";
        }
    }

    [Description("Removes a previously registered Steam API key so the bot no longer has private access to that profile.")]
    public string UnregisterSteamApiKey(
        [Description("The Steam 64-bit ID to unregister")] string steamId)
    {
        if (_keyStore.HasKey(steamId))
        {
            _keyStore.Unregister(steamId);
            return $"Removed registered API key for Steam ID `{steamId}`.";
        }

        return $"No registered API key found for Steam ID `{steamId}`.";
    }

    [Description("Gets game and wishlist information for a private Steam profile from the locally configured steam_profiles.json file. " +
        "Use this when a Steam profile is set to private and the owner has manually provided their game data.")]
    public string GetPrivateProfileGames(
        [Description("The profile key/name as configured in steam_profiles.json")] string profileName)
    {
        try
        {
            var profilesPath = Path.Combine(AppContext.BaseDirectory, "steam_profiles.json");
            if (!File.Exists(profilesPath))
                return "No steam_profiles.json file found. Ask the server admin to create one with private profile data.";

            var json = File.ReadAllText(profilesPath);
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("profiles", out var profiles))
                return "No profiles section found in steam_profiles.json.";

            foreach (var profile in profiles.EnumerateObject())
            {
                if (!string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var displayName = profile.Value.TryGetProperty("displayName", out var dn) ? dn.GetString() : profile.Name;
                var result = $"**Profile:** {displayName}\n";

                if (profile.Value.TryGetProperty("games", out var games))
                {
                    var gameList = games.EnumerateArray().Select(g => $"- {g.GetString()}").OrderBy(g => g).ToList();
                    result += $"\n**Games ({gameList.Count}):**\n{string.Join("\n", gameList)}";
                }

                if (profile.Value.TryGetProperty("wishlist", out var wishlist))
                {
                    var wishlistItems = wishlist.EnumerateArray().Select(g => $"- {g.GetString()}").OrderBy(g => g).ToList();
                    result += $"\n\n**Wishlist ({wishlistItems.Count}):**\n{string.Join("\n", wishlistItems)}";
                }

                return result;
            }

            var available = string.Join(", ", profiles.EnumerateObject().Select(p => p.Name));
            return $"Profile '{profileName}' not found in steam_profiles.json. Available profiles: {available}";
        }
        catch (Exception ex)
        {
            return $"Failed to read private profile data: {ex.Message}";
        }
    }

    private async Task<List<string>> GetFriendSteamIds(string ownerSteamId, string apiKey)
    {
        var url = $"http://api.steampowered.com/ISteamUser/GetFriendList/v0001/?key={apiKey}&steamid={ownerSteamId}&relationship=friend";
        var response = await _httpClient.GetStringAsync(url);
        var doc = JsonDocument.Parse(response);

        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("friendslist", out var fl) &&
            fl.TryGetProperty("friends", out var friends))
        {
            foreach (var friend in friends.EnumerateArray())
                ids.Add(friend.GetProperty("steamid").GetString()!);
        }

        return ids;
    }

    private async Task<List<PlayerSummary>> GetPlayerSummaries(List<string> steamIds, string apiKey)
    {
        var summaries = new List<PlayerSummary>();

        // The API accepts up to 100 Steam IDs per call
        foreach (var batch in steamIds.Chunk(100))
        {
            var ids = string.Join(",", batch);
            var url = $"http://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/?key={apiKey}&steamids={ids}";
            var response = await _httpClient.GetStringAsync(url);
            var doc = JsonDocument.Parse(response);

            if (doc.RootElement.TryGetProperty("response", out var resp) &&
                resp.TryGetProperty("players", out var players))
            {
                foreach (var player in players.EnumerateArray())
                {
                    summaries.Add(new PlayerSummary
                    {
                        SteamId = player.GetProperty("steamid").GetString()!,
                        Name = player.GetProperty("personaname").GetString() ?? "Unknown",
                        ProfileUrl = player.TryGetProperty("profileurl", out var pu) ? pu.GetString() ?? "" : ""
                    });
                }
            }
        }

        return summaries;
    }

    private sealed class PlayerSummary
    {
        public required string SteamId { get; init; }
        public required string Name { get; init; }
        public required string ProfileUrl { get; init; }
    }
}

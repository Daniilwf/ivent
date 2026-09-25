using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace GameEvent.Infrastructure.Pool.Metadata;

/// <summary>What an external base says about a game (D-118): a cover picture to download, the release year, the Steam app.</summary>
public sealed record CoverCandidate(string ImageUrl, int? Year, string? SteamAppId, string Source);

/// <summary>Length of the main story (SPEC «Длина игры»: HowLongToBeat); null — no data, the admin enters it.</summary>
public interface IHoursProvider
{
    Task<decimal?> FindHoursAsync(string title, CancellationToken ct);
}

/// <summary>A cover and the release year (SPEC «Обложки игр»: Steam or IGDB); null — nothing found.</summary>
public interface ICoverProvider
{
    string Name { get; }

    Task<CoverCandidate?> FindCoverAsync(string title, CancellationToken ct);
}

/// <summary>Manual entry: no service asked, the admin types the hours (the main path, SPEC «HowLongToBeat»).</summary>
public sealed class ManualHours : IHoursProvider
{
    public Task<decimal?> FindHoursAsync(string title, CancellationToken ct) => Task.FromResult<decimal?>(null);
}

/// <summary>External services behind the pool (D-28, D-29, D-118); every one is optional.</summary>
public sealed record MetadataSettings
{
    /// <summary>Time for one service to answer; past it the answer is «no data».</summary>
    public int TimeoutSeconds { get; init; } = 8;

    /// <summary>Steam Store search, no key needed (D-28).</summary>
    public bool SteamEnabled { get; init; } = true;

    /// <summary>IGDB needs Twitch client credentials (<c>IGDB_CLIENT_ID</c>, <c>IGDB_CLIENT_SECRET</c>); without them it is off.</summary>
    public string? IgdbClientId { get; init; }

    public string? IgdbClientSecret { get; init; }

    /// <summary>
    /// HowLongToBeat has no official API and its search address changes (D-29): off unless the current address is set,
    /// e.g. <c>https://howlongtobeat.com/api/search</c>.
    /// </summary>
    public string? HltbSearchUrl { get; init; }

    /// <summary>Hosts covers are downloaded from (the URLs come from the services' answers).</summary>
    public IReadOnlyList<string> CoverHosts { get; init; } = ["steamstatic.com", "igdb.com"];
}

/// <summary>Steam Store: search by title, the vertical library capsule or the header, the release year (D-28).</summary>
public sealed partial class SteamCoverProvider(HttpClient http, ILogger<SteamCoverProvider> logger) : ICoverProvider
{
    public string Name => "steam";

    public async Task<CoverCandidate?> FindCoverAsync(string title, CancellationToken ct)
    {
        var search = await http.GetFromJsonAsync<JsonElement>(
            $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(title)}&l=english&cc=US", ct);
        if (!search.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var apps = items.EnumerateArray()
            .Where(i => i.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && i.TryGetProperty("name", out _))
            .ToList();
        var best = apps.FirstOrDefault(i => Same(i.GetProperty("name").GetString(), title));
        if (best.ValueKind == JsonValueKind.Undefined)
        {
            best = apps.FirstOrDefault();
        }

        if (best.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        var appId = best.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture);
        int? year = null;
        try
        {
            var details = await http.GetFromJsonAsync<JsonElement>($"https://store.steampowered.com/api/appdetails?appids={appId}&l=english", ct);
            if (details.TryGetProperty(appId, out var app) && app.TryGetProperty("data", out var data)
                && data.TryGetProperty("release_date", out var release) && release.TryGetProperty("date", out var date))
            {
                year = YearOf(date.GetString());
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or NotSupportedException)
        {
            // The year is a nicety: the cover stands without it
            LogDetailsFailed(logger, appId, e.Message);
        }

        return new CoverCandidate($"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg", year, appId, Name);
    }

    internal static int? YearOf(string? date) =>
        date is not null && YearPattern().Match(date) is { Success: true } match ? int.Parse(match.Value, CultureInfo.InvariantCulture) : null;

    internal static bool Same(string? a, string? b) =>
        a is not null && b is not null && Normalize(a) == Normalize(b);

    private static string Normalize(string text) =>
        new([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    [GeneratedRegex(@"(19|20)\d{2}")]
    private static partial Regex YearPattern();

    [LoggerMessage(Level = LogLevel.Information, Message = "Steam app details of {AppId} unavailable: {Reason}")]
    private static partial void LogDetailsFailed(ILogger logger, string appId, string reason);
}

/// <summary>IGDB through Twitch client credentials (D-28): the cover in the big size and the first release year.</summary>
public sealed class IgdbCoverProvider(HttpClient http, MetadataSettings settings, TimeProviderClock clock) : ICoverProvider
{
    private string? _token;
    private DateTimeOffset _tokenUntil;

    public string Name => "igdb";

    public async Task<CoverCandidate?> FindCoverAsync(string title, CancellationToken ct)
    {
        if (settings.IgdbClientId is not { Length: > 0 } clientId || settings.IgdbClientSecret is not { Length: > 0 } secret)
        {
            return null;
        }

        var token = await TokenAsync(clientId, secret, ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games")
        {
            // IGDB's own query language; the title is quoted, its quotes and backslashes escaped
            Content = new StringContent($"search \"{title.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"; fields name,first_release_date,cover.image_id; limit 5;"),
        };
        request.Headers.Add("Client-ID", clientId);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var games = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (games.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var withCover = games.EnumerateArray()
            .Where(g => g.TryGetProperty("cover", out var cover) && cover.TryGetProperty("image_id", out _))
            .ToList();
        var best = withCover.FirstOrDefault(g => g.TryGetProperty("name", out var name) && SteamCoverProvider.Same(name.GetString(), title));
        if (best.ValueKind == JsonValueKind.Undefined)
        {
            best = withCover.FirstOrDefault();
        }

        if (best.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        var imageId = best.GetProperty("cover").GetProperty("image_id").GetString();
        if (imageId is null || !imageId.All(char.IsLetterOrDigit))
        {
            return null;
        }

        int? year = best.TryGetProperty("first_release_date", out var released) && released.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(released.GetInt64()).Year
            : null;
        return new CoverCandidate($"https://images.igdb.com/igdb/image/upload/t_cover_big/{imageId}.jpg", year, null, Name);
    }

    private async Task<string> TokenAsync(string clientId, string secret, CancellationToken ct)
    {
        if (_token is not null && clock.Now < _tokenUntil)
        {
            return _token;
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["grant_type"] = "client_credentials",
        });
        using var response = await http.PostAsync("https://id.twitch.tv/oauth2/token", form, ct);
        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        _token = answer.GetProperty("access_token").GetString() ?? throw new JsonException("No access token.");
        var seconds = answer.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number ? expires.GetInt64() : 3600;
        _tokenUntil = clock.Now.AddSeconds(Math.Max(60, seconds - 60));
        return _token;
    }
}

/// <summary>Wall-clock time for token lifetimes outside the engine (the engine's IClock is for game time).</summary>
public sealed class TimeProviderClock(TimeProvider time)
{
    public DateTimeOffset Now => time.GetUtcNow();
}

/// <summary>
/// HowLongToBeat, unofficial and changing (D-29): off unless its current search address is configured; the main story
/// time of the closest title, rounded to half an hour.
/// </summary>
public sealed class HltbHoursProvider(HttpClient http, MetadataSettings settings) : IHoursProvider
{
    public async Task<decimal?> FindHoursAsync(string title, CancellationToken ct)
    {
        if (settings.HltbSearchUrl is not { Length: > 0 } url || !url.StartsWith("https://howlongtobeat.com/", StringComparison.Ordinal))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new
            {
                searchType = "games",
                searchTerms = title.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                searchPage = 1,
                size = 5,
            }),
        };
        request.Headers.Referrer = new Uri("https://howlongtobeat.com/");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!answer.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var games = data.EnumerateArray().Where(g => g.TryGetProperty("comp_main", out var main) && main.ValueKind == JsonValueKind.Number && main.GetInt64() > 0).ToList();
        var best = games.FirstOrDefault(g => g.TryGetProperty("game_name", out var name) && SteamCoverProvider.Same(name.GetString(), title));
        if (best.ValueKind == JsonValueKind.Undefined)
        {
            best = games.FirstOrDefault();
        }

        return best.ValueKind == JsonValueKind.Undefined
            ? null
            : Math.Round(best.GetProperty("comp_main").GetInt64() / 3600m * 2, MidpointRounding.AwayFromZero) / 2;
    }
}

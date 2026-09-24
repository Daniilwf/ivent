using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Rolls;
using GameEvent.Web.Seasons;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// G10, G11 over HTTP: the admin sees how many games each category has available and which players have no game left
/// (SPEC «Уточнения»: пустой пул → сигнал в админке; D-92). <c>GET /api/admin/seasons/{id}/pool-stats</c> returns
/// <c>{categories:[{category, weight, available}], playersWithoutGames:[{id, name}]}</c>; admin only.
/// Responses are read as raw JSON: the response type is designed here and added with the endpoint.
/// </summary>
public sealed class PoolStatsApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string StatsUrl(Guid seasonId) => $"/api/admin/seasons/{seasonId}/pool-stats";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    private async Task<JsonDocument> StatsAsync(Guid? seasonId = null)
    {
        var admin = await _site.SignedInAsync("admin");
        var response = await admin.GetAsync(StatsUrl(seasonId ?? SiteFactory.SeasonId), Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    private static List<(string Category, int Weight, int Available)> Categories(JsonDocument stats) =>
        [.. stats.RootElement.GetProperty("categories").EnumerateArray().Select(c =>
            (c.GetProperty("category").GetString()!, c.GetProperty("weight").GetInt32(), c.GetProperty("available").GetInt32()))];

    private static List<(Guid Id, string Name)> WithoutGames(JsonDocument stats) =>
        [.. stats.RootElement.GetProperty("playersWithoutGames").EnumerateArray().Select(p =>
            (p.GetProperty("id").GetGuid(), p.GetProperty("name").GetString()!))];

    private async Task<Guid> OfferOfAsync(HttpClient player) =>
        (await player.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Offer!.Id;

    // ---- Success ----

    [Fact]
    public async Task Admin_sees_available_games_per_category_and_nobody_without_games()
    {
        using var stats = await StatsAsync();

        Assert.Equal([("Horror", 1, 3)], Categories(stats));
        Assert.Empty(WithoutGames(stats));
    }

    [Fact]
    public async Task Offered_game_is_not_available_in_the_counts()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var roll = await vasya.PostAsJsonAsync($"/api/seasons/{SiteFactory.SeasonId}/roll", new { commandId = Guid.NewGuid() }, Ct);
        roll.EnsureSuccessStatusCode();

        using var stats = await StatsAsync();

        Assert.Equal([("Horror", 1, 2)], Categories(stats));
    }

    [Fact]
    public async Task Player_who_excluded_every_game_is_the_signal()
    {
        // Given Вася declares «Уже проходил» on each of the three games in turn (sent through the queue)
        var vasya = await _site.SignedInAsync("vasya");
        var player = _site.Players["vasya"];
        await _site.SendAsync(new RollGame(player));
        for (var i = 0; i < 3; i++)
        {
            await _site.SendAsync(new DeclareAlreadyPlayed(player, await OfferOfAsync(vasya)));
        }

        using var stats = await StatsAsync();

        // Then the admin sees him without games; the counts do not subtract personal exclusions (D-92)
        Assert.Equal([(player, "vasya")], WithoutGames(stats));
        Assert.Equal([("Horror", 1, 3)], Categories(stats));
    }

    [Fact]
    public async Task Response_uses_camel_case_names()
    {
        using var stats = await StatsAsync();

        var root = stats.RootElement;
        Assert.Equal(["categories", "playersWithoutGames"], root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["available", "category", "weight"],
            root.GetProperty("categories")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Season_that_is_not_running_has_no_signal()
    {
        // A draft season: nobody is rolling there, so nobody is a signal
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddDays(1);
        var draft = await _site.CreateSeasonAsync();

        using var stats = await StatsAsync(draft);

        Assert.Empty(WithoutGames(stats));
        Assert.Equal([("Horror", 1, 3)], Categories(stats));
    }

    // ---- Refused ----

    [Fact]
    public async Task Anonymous_is_unauthorized()
    {
        var client = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(StatsUrl(SiteFactory.SeasonId), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("petya")]
    [InlineData("masha")]
    [InlineData("zritel")]
    public async Task Players_and_spectators_are_forbidden(string login)
    {
        var client = await _site.SignedInAsync(login);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(StatsUrl(SiteFactory.SeasonId), Ct)).StatusCode);
    }

    [Fact]
    public async Task Many_requests_in_a_row_are_limited_per_user()
    {
        // The endpoint folds the whole season log on every call (D-92): an admin hammering it gets 429
        var admin = await _site.SignedInAsync("admin");
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 40; i++)
        {
            codes.Add((await admin.GetAsync(StatsUrl(SiteFactory.SeasonId), Ct)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, codes[0]);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        Assert.All(codes, c => Assert.True(c is HttpStatusCode.OK or HttpStatusCode.TooManyRequests, $"Unexpected {c}."));

        // Once limited, it stays limited within the window: no OK after the first 429
        var firstLimited = codes.IndexOf(HttpStatusCode.TooManyRequests);
        Assert.All(codes.Skip(firstLimited), c => Assert.Equal(HttpStatusCode.TooManyRequests, c));
    }

    [Fact]
    public async Task Unknown_season_is_not_found()
    {
        var admin = await _site.SignedInAsync("admin");

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(StatsUrl(Guid.NewGuid()), Ct)).StatusCode);
    }

    [Fact]
    public async Task Invalid_season_id_is_not_found()
    {
        var admin = await _site.SignedInAsync("admin");

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/admin/seasons/not-a-guid/pool-stats", Ct)).StatusCode);
    }
}

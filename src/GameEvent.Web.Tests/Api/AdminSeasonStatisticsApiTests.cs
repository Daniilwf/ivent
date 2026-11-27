using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.Api;

public sealed class AdminSeasonStatisticsApiTests : IAsyncLifetime
{
    private const string AdminSeasons = "/api/admin/seasons";
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Guid SeasonId => SiteFactory.SeasonId;
    private static string AdminSeason => $"{AdminSeasons}/{SeasonId}";
    private static string Season => $"/api/seasons/{SeasonId}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_draft_without_participants_has_an_empty_private_report()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        var draft = await CreateDraftAsync(admin);

        Assert.Empty(await ReadPlayersAsync(admin, draft));
    }

    [Fact]
    public async Task The_report_shows_the_latest_own_action_even_after_later_admin_corrections()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        await PostOkAsync(vasya, $"{Season}/roll");
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        await PostOkAsync(vasya, $"{Season}/start");
        var lastAction = _site.Clock.UtcNow;
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        foreach (var playerId in _site.Players.Values)
        {
            await PostOkAsync(admin, $"{AdminSeason}/players/{playerId}/adjust", new
            {
                commandId = Guid.NewGuid(),
                pointsDelta = 1,
                comment = "Admin correction",
            });
        }

        var players = await ReadPlayersAsync(admin, SeasonId);

        Assert.Equal(2, players.Length);
        Assert.Contains(new PlayerRow(_site.Players["vasya"], "vasya", 0, 0, lastAction), players);
        Assert.Contains(new PlayerRow(_site.Players["petya"], "petya", 0, 0, null), players);
    }

    [Fact]
    public async Task A_landed_attack_only_counts_in_its_season_and_a_whole_undo_removes_it()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        var other = await CreateDraftAsync(admin);
        await PostOkAsync(admin, $"{AdminSeasons}/{other}/players", new
        {
            commandId = Guid.NewGuid(),
            userId = _site.Users["vasya"],
        });
        var hit = await LandAttackAsync(admin, vasya);

        AssertAttackCounts(await ReadPlayersAsync(admin, SeasonId), (1, 0), (0, 1));
        var otherPlayer = Assert.Single(await ReadPlayersAsync(admin, other));
        Assert.Equal(("vasya", 0, 0, (DateTimeOffset?)null),
            (otherPlayer.Name, otherPlayer.AttacksMade, otherPlayer.AttacksReceived, otherPlayer.LastActionAt));
        Assert.NotEqual(_site.Players["vasya"], otherPlayer.PlayerId);

        await PostOkAsync(admin, $"{AdminSeason}/undo", new
        {
            commandId = Guid.NewGuid(),
            targetCommandId = hit,
            comment = "Wrong target",
        });

        AssertAttackCounts(await ReadPlayersAsync(admin, SeasonId), (0, 0), (0, 0));
    }

    [Fact]
    public async Task An_archived_season_keeps_its_attack_and_activity_report()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        await LandAttackAsync(admin, vasya);
        var lastAction = _site.Clock.UtcNow;
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddDays(1);
        foreach (var to in new[] { "closing", "finished", "archived" })
        {
            await PostOkAsync(admin, $"{AdminSeason}/status", new { commandId = Guid.NewGuid(), to });
        }

        var players = await ReadPlayersAsync(admin, SeasonId);

        AssertAttackCounts(players, (1, 0), (0, 1));
        Assert.Equal(lastAction, players.Single(p => p.PlayerId == _site.Players["vasya"]).LastActionAt);
        Assert.Null(players.Single(p => p.PlayerId == _site.Players["petya"]).LastActionAt);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("vasya", HttpStatusCode.Forbidden)]
    [InlineData("zritel", HttpStatusCode.Forbidden)]
    public async Task Only_the_admin_can_read_a_seasons_statistics(string? login, HttpStatusCode status)
    {
        using var host = WithStatistics();
        using var client = login is null ? host.CreateClient() : await SignInAsync(host, login);

        using var response = await client.GetAsync($"{AdminSeason}/statistics", Ct);

        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_or_malformed_season_is_not_found()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");

        using var unknown = await admin.GetAsync($"{AdminSeasons}/{Guid.NewGuid()}/statistics", Ct);
        using var malformed = await admin.GetAsync($"{AdminSeasons}/not-a-guid/statistics", Ct);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
    }

    [Fact]
    public async Task Statistics_are_off_by_default()
    {
        using var admin = await _site.SignedInAsync("admin");

        using var response = await admin.GetAsync($"{AdminSeason}/statistics", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private WebApplicationFactory<Program> WithStatistics() =>
        _site.WithWebHostBuilder(builder => builder.UseSetting("Site:AdminStatistics", "true"));

    private static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> host, string login)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(client);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { login, password = SiteFactory.Password }, Ct);
        response.EnsureSuccessStatusCode();
        await SiteFactory.RefreshCsrfAsync(client);
        return client;
    }

    private static async Task<Guid> CreateDraftAsync(HttpClient admin)
    {
        var seasonId = Guid.NewGuid();
        await PostOkAsync(admin, AdminSeasons, new { commandId = Guid.NewGuid(), seasonId, name = "Another season" });
        return seasonId;
    }

    private async Task<Guid> LandAttackAsync(HttpClient admin, HttpClient attacker)
    {
        using var currentRules = await admin.GetAsync($"{Season}/rules", Ct);
        var current = await OkAsync(currentRules);
        var ruleset = JsonNode.Parse(current.GetProperty("ruleset").GetRawText())!;
        ruleset["features"]!["items"] = true;
        using var updatedRules = await admin.PutAsJsonAsync($"{AdminSeason}/rules", new
        {
            commandId = Guid.NewGuid(),
            expectedVersion = current.GetProperty("version").GetInt32(),
            ruleset,
        }, Ct);
        await OkAsync(updatedRules);
        await PostOkAsync(admin, $"{AdminSeason}/content/publish", new
        {
            commandId = Guid.NewGuid(),
            expectedVersion = 0,
            pack = EconomyApiTests.RepositoryPack(),
            comment = "Test pack",
        });
        await PostOkAsync(admin, $"{AdminSeason}/players/{_site.Players["vasya"]}/inventory", new
        {
            commandId = Guid.NewGuid(),
            giveObjectId = "bird-thief",
            comment = "Test item",
        });
        var target = _site.Players["petya"];
        await PostOkAsync(admin, $"{AdminSeason}/players/{target}/adjust", new
        {
            commandId = Guid.NewGuid(),
            coinsDelta = 10,
            comment = "Coins available to steal",
        });
        using var economyResponse = await attacker.GetAsync($"{Season}/economy", Ct);
        var economy = await OkAsync(economyResponse);
        var itemId = Assert.Single(economy.GetProperty("items").EnumerateArray()).GetProperty("instanceId").GetGuid();

        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        var commandId = Guid.NewGuid();
        await PostOkAsync(attacker, $"{Season}/items/{itemId}/use", new { commandId, targetPlayerId = target });
        return commandId;
    }

    private static async Task<PlayerRow[]> ReadPlayersAsync(HttpClient admin, Guid seasonId)
    {
        using var response = await admin.GetAsync($"{AdminSeasons}/{seasonId}/statistics", Ct);
        var report = await OkAsync(response);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return report.GetProperty("players").EnumerateArray()
            .Select(p =>
            {
                var lastAction = p.GetProperty("lastActionAt");
                return new PlayerRow(p.GetProperty("playerId").GetGuid(), p.GetProperty("name").GetString()!,
                    p.GetProperty("attacksMade").GetInt32(), p.GetProperty("attacksReceived").GetInt32(),
                    lastAction.ValueKind == JsonValueKind.Null ? null : lastAction.GetDateTimeOffset());
            })
            .ToArray();
    }

    private void AssertAttackCounts(PlayerRow[] players, (int Made, int Received) vasya, (int Made, int Received) petya)
    {
        Assert.Equal(2, players.Length);
        var attacker = players.Single(p => p.PlayerId == _site.Players["vasya"]);
        var target = players.Single(p => p.PlayerId == _site.Players["petya"]);
        Assert.Equal(vasya, (attacker.AttacksMade, attacker.AttacksReceived));
        Assert.Equal(petya, (target.AttacksMade, target.AttacksReceived));
    }

    private sealed record PlayerRow(Guid PlayerId, string Name, int AttacksMade, int AttacksReceived, DateTimeOffset? LastActionAt);
}

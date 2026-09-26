using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The inactivity hint (SE5, D-123): no action of the player's own for the rules' <c>inactiveHintDays</c> (3 by default),
/// counted from the latest of the season's start, the player's joining and their last action; only while the season is
/// on, the flag is not set and the player is not frozen by the first finish.
/// </summary>
public sealed class InactiveHintTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Players(Guid seasonId) => $"/api/admin/seasons/{seasonId}/players";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task The_hint_comes_at_exactly_the_rules_days()
    {
        var admin = await _site.SignedInAsync("admin");
        var start = _site.Clock.UtcNow;

        _site.Clock.UtcNow = start.AddDays(3).AddMinutes(-1);
        var before = await HintAsync(admin, SiteFactory.SeasonId, _site.Players["petya"]);
        _site.Clock.UtcNow = start.AddDays(3);
        var at = await HintAsync(admin, SiteFactory.SeasonId, _site.Players["petya"]);

        Assert.False(before);
        Assert.True(at);
    }

    [Fact]
    public async Task The_admins_corrections_are_not_the_players_actions()
    {
        var admin = await _site.SignedInAsync("admin");
        var start = _site.Clock.UtcNow;
        _site.Clock.UtcNow = start.AddDays(2);
        await PostOkAsync(admin, $"{Players(SiteFactory.SeasonId)}/{_site.Players["petya"]}/adjust", new { commandId = Guid.NewGuid(), comment = "Бонус", pointsDelta = 1 });

        _site.Clock.UtcNow = start.AddDays(3);

        Assert.True(await HintAsync(admin, SiteFactory.SeasonId, _site.Players["petya"]));
    }

    [Fact]
    public async Task A_player_joining_mid_season_is_quiet_from_the_joining()
    {
        var admin = await _site.SignedInAsync("admin");
        var start = _site.Clock.UtcNow;
        _site.Clock.UtcNow = start.AddDays(10);
        await PostOkAsync(admin, Players(SiteFactory.SeasonId), new { commandId = Guid.NewGuid(), userId = _site.Users["masha"] });
        var masha = await PlayerIdAsync(admin, SiteFactory.SeasonId, "masha");

        _site.Clock.UtcNow = start.AddDays(11);
        var nextDay = await HintAsync(admin, SiteFactory.SeasonId, masha);
        _site.Clock.UtcNow = start.AddDays(13);
        var laterOn = await HintAsync(admin, SiteFactory.SeasonId, masha);

        Assert.False(nextDay);
        Assert.True(laterOn);
    }

    [Fact]
    public async Task A_season_long_in_draft_counts_from_its_start()
    {
        var admin = await _site.SignedInAsync("admin");
        var created = _site.Clock.UtcNow;
        var seasonId = Guid.NewGuid();
        await PostOkAsync(admin, "/api/admin/seasons", new { commandId = Guid.NewGuid(), seasonId, name = "Осень" });
        await PostOkAsync(admin, Players(seasonId), new { commandId = Guid.NewGuid(), userId = _site.Users["masha"] });
        var masha = await PlayerIdAsync(admin, seasonId, "masha");

        _site.Clock.UtcNow = created.AddDays(5);
        var inDraft = await HintAsync(admin, seasonId, masha);
        await PostOkAsync(admin, $"/api/admin/seasons/{seasonId}/status", new { commandId = Guid.NewGuid(), to = "active" });
        _site.Clock.UtcNow = created.AddDays(6);
        var dayAfterStart = await HintAsync(admin, seasonId, masha);
        _site.Clock.UtcNow = created.AddDays(8);
        var quiet = await HintAsync(admin, seasonId, masha);

        Assert.False(inDraft);
        Assert.False(dayAfterStart);
        Assert.True(quiet);
    }

    [Fact]
    public async Task No_hint_once_the_season_is_closing()
    {
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/status", new { commandId = Guid.NewGuid(), to = "closing" });

        _site.Clock.UtcNow = _site.Clock.UtcNow.AddDays(5);

        Assert.False(await HintAsync(admin, SiteFactory.SeasonId, _site.Players["petya"]));
    }

    [Fact]
    public async Task No_hint_for_the_frozen_first_finisher()
    {
        // Вася finishes first on day 0 and his proof is approved: frozen, nothing more is expected of him
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(admin, $"/api/test/seasons/{SiteFactory.SeasonId}/scenarios/finish-soon", new { player = "vasya" });
        await PostOkAsync(vasya, $"/api/seasons/{SiteFactory.SeasonId}/roll");
        await PostOkAsync(vasya, $"/api/seasons/{SiteFactory.SeasonId}/start");
        await PostOkAsync(vasya, $"/api/seasons/{SiteFactory.SeasonId}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });
        var run = (await OkAsync(await vasya.GetAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct))).GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetGuid();
        await PostOkAsync(vasya, $"/api/seasons/{SiteFactory.SeasonId}/runs/{run}/proof", new { commandId = Guid.NewGuid(), links = new[] { "https://imgur.com/a/credits" } });
        await PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/runs/{run}/approve", new { commandId = Guid.NewGuid() });

        _site.Clock.UtcNow = _site.Clock.UtcNow.AddDays(5);

        Assert.False(await HintAsync(admin, SiteFactory.SeasonId, _site.Players["vasya"]));
        Assert.True(await HintAsync(admin, SiteFactory.SeasonId, _site.Players["petya"]));
    }

    [Fact]
    public async Task Invalid_player_changes_are_refused()
    {
        var admin = await _site.SignedInAsync("admin");
        var petya = _site.Players["petya"];
        var other = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{Players(SiteFactory.SeasonId)}/{petya}/inactive", new { isInactive = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"{Players(SiteFactory.SeasonId)}/{other}/inactive", new { commandId = Guid.NewGuid(), isInactive = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"{Players(other)}/{petya}/inactive", new { commandId = Guid.NewGuid(), isInactive = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"{Players(other)}/{petya}/adjust", new { commandId = Guid.NewGuid(), comment = "нет сезона", pointsDelta = 1 }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Players(SiteFactory.SeasonId), new { commandId = Guid.NewGuid(), userId = Guid.Empty }, Ct)).StatusCode);
    }

    // ---- Helpers ----

    private async Task<bool> HintAsync(HttpClient admin, Guid seasonId, Guid playerId) =>
        (await PlayersAsync(admin, seasonId)).Single(p => p.GetProperty("id").GetGuid() == playerId).GetProperty("inactiveHint").GetBoolean();

    private async Task<Guid> PlayerIdAsync(HttpClient admin, Guid seasonId, string login) =>
        (await PlayersAsync(admin, seasonId)).Single(p => p.GetProperty("userId").GetGuid() == _site.Users[login]).GetProperty("id").GetGuid();

    private static async Task<List<JsonElement>> PlayersAsync(HttpClient admin, Guid seasonId) =>
        [.. (await OkAsync(await admin.GetAsync(Players(seasonId), Ct))).EnumerateArray()];

    private static async Task PostOkAsync(HttpClient client, string url, object? body = null)
    {
        var response = await client.PostAsJsonAsync(url, body ?? new { commandId = Guid.NewGuid() }, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Seasons and players for the admin (SPEC «Админка», E2, SE1, SE4, SE5, D-123): the list of seasons, creating one, adding
/// and correcting players, the inactivity flag and the hint about quiet players. Each endpoint: success, another role,
/// invalid input.
/// </summary>
public sealed class AdminPlayerApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Players => $"/api/admin/seasons/{SiteFactory.SeasonId}/players";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Everyone_signed_in_sees_the_seasons_newest_first()
    {
        var admin = await _site.SignedInAsync("admin");
        var created = Guid.NewGuid();
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(1);
        await OkAsync(await admin.PostAsJsonAsync("/api/admin/seasons", new { commandId = Guid.NewGuid(), seasonId = created, name = "Весна" }, Ct));

        var seasons = await JsonAsync(await (await _site.SignedInAsync("zritel")).GetAsync("/api/seasons", Ct));

        Assert.Equal([created, SiteFactory.SeasonId], seasons.EnumerateArray().Select(s => s.GetProperty("id").GetGuid()));
        Assert.Equal("draft", seasons[0].GetProperty("status").GetString());
        Assert.Equal("Весна", seasons[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await (await _site.AnonymousAsync()).GetAsync("/api/seasons", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_admin_creates_a_season_once_per_command()
    {
        var admin = await _site.SignedInAsync("admin");
        var request = new { commandId = Guid.NewGuid(), seasonId = Guid.NewGuid(), name = "  Лето  ", deadline = _site.Clock.UtcNow.AddDays(30) };

        var first = await OkAsync(await admin.PostAsJsonAsync("/api/admin/seasons", request, Ct));
        var again = await OkAsync(await admin.PostAsJsonAsync("/api/admin/seasons", request, Ct));
        var sameId = await admin.PostAsJsonAsync("/api/admin/seasons", request with { commandId = Guid.NewGuid() }, Ct);

        Assert.False(first.GetProperty("duplicate").GetBoolean());
        Assert.True(again.GetProperty("duplicate").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, sameId.StatusCode);
        var season = await JsonAsync(await admin.GetAsync($"/api/seasons/{request.seasonId}", Ct));
        Assert.Equal("Лето", season.GetProperty("name").GetString());
        Assert.Equal(request.deadline, season.GetProperty("deadline").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("""{"seasonId":"SEASON","name":"Без команды"}""")]
    [InlineData("""{"commandId":"COMMAND","name":"Без сезона"}""")]
    [InlineData("""{"commandId":"COMMAND","seasonId":"SEASON","name":"   "}""")]
    [InlineData("""{"commandId":"COMMAND","seasonId":"SEASON"}""")]
    public async Task A_season_without_ids_or_a_name_is_invalid(string body)
    {
        var admin = await _site.SignedInAsync("admin");

        var answer = await admin.PostAsync("/api/admin/seasons", Json(body), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Only_the_admin_creates_seasons_and_manages_players(string login)
    {
        var client = await _site.SignedInAsync(login);
        var vasya = _site.Players["vasya"];

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/seasons", new { commandId = Guid.NewGuid(), seasonId = Guid.NewGuid(), name = "Моё" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Players, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Players, new { commandId = Guid.NewGuid(), userId = _site.Users["masha"] }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Players}/{vasya}/adjust", new { commandId = Guid.NewGuid(), comment = "себе", pointsDelta = 100 }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Players}/{vasya}/inactive", new { commandId = Guid.NewGuid(), isInactive = true }, Ct)).StatusCode);
    }

    [Fact]
    public async Task The_admin_adds_a_player_mid_season_with_a_position_and_a_balance()
    {
        var admin = await _site.SignedInAsync("admin");
        var cells = (await JsonAsync(await admin.GetAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct))).GetProperty("cells").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        var request = new { commandId = Guid.NewGuid(), userId = _site.Users["masha"], cellId = cells[3], points = 12, coins = 5 };

        await OkAsync(await admin.PostAsJsonAsync(Players, request, Ct));
        var again = await OkAsync(await admin.PostAsJsonAsync(Players, request, Ct));

        Assert.True(again.GetProperty("duplicate").GetBoolean());
        var masha = (await PlayersAsync(admin)).Single(p => p.GetProperty("userId").GetGuid() == _site.Users["masha"]);
        Assert.Equal((cells[3], 12, 5, "masha"), (masha.GetProperty("cellId").GetString(), masha.GetProperty("points").GetInt32(), masha.GetProperty("coins").GetInt32(), masha.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Only_an_active_account_with_the_player_role_joins_and_only_once()
    {
        var admin = await _site.SignedInAsync("admin");

        var spectator = await admin.PostAsJsonAsync(Players, new { commandId = Guid.NewGuid(), userId = _site.Users["zritel"] }, Ct);
        var theAdmin = await admin.PostAsJsonAsync(Players, new { commandId = Guid.NewGuid(), userId = _site.Users["admin"] }, Ct);
        var unknown = await admin.PostAsJsonAsync(Players, new { commandId = Guid.NewGuid(), userId = Guid.NewGuid() }, Ct);
        var twice = await admin.PostAsJsonAsync(Players, new { commandId = Guid.NewGuid(), userId = _site.Users["vasya"] }, Ct);
        var noCommand = await admin.PostAsJsonAsync(Players, new { userId = _site.Users["masha"] }, Ct);
        var noSeason = await admin.PostAsJsonAsync($"/api/admin/seasons/{Guid.NewGuid()}/players", new { commandId = Guid.NewGuid(), userId = _site.Users["masha"] }, Ct);

        Assert.Equal("account.notAPlayer", await CodeAsync(spectator));
        Assert.Equal("account.notAPlayer", await CodeAsync(theAdmin));
        Assert.Equal("account.unknown", await CodeAsync(unknown));
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noCommand.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noSeason.StatusCode);
    }

    [Fact]
    public async Task The_admin_corrects_a_player_with_a_comment()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = _site.Players["vasya"];

        await OkAsync(await admin.PostAsJsonAsync($"{Players}/{vasya}/adjust", new { commandId = Guid.NewGuid(), comment = "Компенсация за сбой", pointsDelta = 7, coinsDelta = 3, resourceDeltas = new[] { new { resource = "freeRerolls", delta = 1 } } }, Ct));

        var row = (await PlayersAsync(admin)).Single(p => p.GetProperty("id").GetGuid() == vasya);
        Assert.Equal((7, 3, 1), (row.GetProperty("points").GetInt32(), row.GetProperty("coins").GetInt32(), row.GetProperty("resources").GetProperty("freeRerolls").GetInt32()));
    }

    [Fact]
    public async Task A_correction_without_a_comment_or_of_an_unknown_player_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = _site.Players["vasya"];

        var noComment = await admin.PostAsJsonAsync($"{Players}/{vasya}/adjust", new { commandId = Guid.NewGuid(), pointsDelta = 7 }, Ct);
        var noResource = await admin.PostAsJsonAsync($"{Players}/{vasya}/adjust", new { commandId = Guid.NewGuid(), comment = "ресурс", resourceDeltas = new[] { new { delta = 1 } } }, Ct);
        var unknown = await admin.PostAsJsonAsync($"{Players}/{Guid.NewGuid()}/adjust", new { commandId = Guid.NewGuid(), comment = "кто это", pointsDelta = 1 }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, noComment.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noResource.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
    }

    [Fact]
    public async Task The_admin_marks_a_player_inactive_and_back()
    {
        var admin = await _site.SignedInAsync("admin");
        var petya = _site.Players["petya"];

        await OkAsync(await admin.PostAsJsonAsync($"{Players}/{petya}/inactive", new { commandId = Guid.NewGuid(), isInactive = true }, Ct));
        var marked = (await PlayersAsync(admin)).Single(p => p.GetProperty("id").GetGuid() == petya).GetProperty("isInactive").GetBoolean();
        var twice = await admin.PostAsJsonAsync($"{Players}/{petya}/inactive", new { commandId = Guid.NewGuid(), isInactive = true }, Ct);
        await OkAsync(await admin.PostAsJsonAsync($"{Players}/{petya}/inactive", new { commandId = Guid.NewGuid(), isInactive = false }, Ct));

        Assert.True(marked);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.False((await PlayersAsync(admin)).Single(p => p.GetProperty("id").GetGuid() == petya).GetProperty("isInactive").GetBoolean());
    }

    [Fact]
    public async Task The_hint_shows_players_quiet_for_the_rules_days_and_who_is_playing()
    {
        // The default rules hint after 3 days. Вася acts on day 2 and starts a run; Петя never acts.
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddDays(2);
        (await vasya.PostAsJsonAsync($"/api/seasons/{SiteFactory.SeasonId}/roll", new { commandId = Guid.NewGuid() }, Ct)).EnsureSuccessStatusCode();
        (await vasya.PostAsJsonAsync($"/api/seasons/{SiteFactory.SeasonId}/start", new { commandId = Guid.NewGuid() }, Ct)).EnsureSuccessStatusCode();
        var actedAt = _site.Clock.UtcNow;
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddDays(2);

        var rows = await PlayersAsync(admin);
        var v = rows.Single(p => p.GetProperty("id").GetGuid() == _site.Players["vasya"]);
        var p = rows.Single(p => p.GetProperty("id").GetGuid() == _site.Players["petya"]);

        Assert.Equal((false, true, actedAt), (v.GetProperty("inactiveHint").GetBoolean(), v.GetProperty("playing").GetBoolean(), v.GetProperty("lastActionAt").GetDateTimeOffset()));
        Assert.Equal((true, false, JsonValueKind.Null), (p.GetProperty("inactiveHint").GetBoolean(), p.GetProperty("playing").GetBoolean(), p.GetProperty("lastActionAt").ValueKind));

        // A player marked inactive needs no hint
        await OkAsync(await admin.PostAsJsonAsync($"{Players}/{_site.Players["petya"]}/inactive", new { commandId = Guid.NewGuid(), isInactive = true }, Ct));
        Assert.False((await PlayersAsync(admin)).Single(r => r.GetProperty("id").GetGuid() == _site.Players["petya"]).GetProperty("inactiveHint").GetBoolean());
    }

    [Fact]
    public async Task The_players_of_an_unknown_season_are_not_found()
    {
        var admin = await _site.SignedInAsync("admin");

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/seasons/{Guid.NewGuid()}/players", Ct)).StatusCode);
    }

    // ---- Helpers ----

    private async Task<List<JsonElement>> PlayersAsync(HttpClient admin) =>
        [.. (await JsonAsync(await admin.GetAsync(Players, Ct))).EnumerateArray()];

    private static StringContent Json(string body) =>
        new(body.Replace("COMMAND", Guid.NewGuid().ToString(), StringComparison.Ordinal).Replace("SEASON", Guid.NewGuid().ToString(), StringComparison.Ordinal), System.Text.Encoding.UTF8, "application/json");

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await OkAsync(response);

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}

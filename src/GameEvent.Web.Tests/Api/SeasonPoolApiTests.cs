using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Web.Pool;
using GameEvent.Web.Seasons;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// H6 over HTTP (SPEC «Статусы игры в сезоне», D-160): <c>GET /api/seasons/{id}/games</c> lists the games with a status
/// in the season — «Уже прошёл Вася, 12.10», «Сейчас играет Вася», drop marks of other players, and why a game never
/// comes to the viewer again. Everyone signed in reads it.
/// </summary>
public sealed class SeasonPoolApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string GamesUrl(Guid seasonId) => $"/api/seasons/{seasonId}/games";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    private async Task<List<SeasonGameView>> GamesAsync(string login)
    {
        var client = await _site.SignedInAsync(login);
        var response = await client.GetAsync(GamesUrl(SiteFactory.SeasonId), Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<List<SeasonGameView>>(s_json, Ct))!;
    }

    // A view's marks are a list: compared by their text, the records by value
    private static (Guid, RollMissReason?, string?, DateTimeOffset?, string, ExclusionReason?) Row(SeasonGameView v) =>
        (v.GameId, v.Taken, v.TakenBy, v.CompletedAt, string.Join("; ", v.Marks), v.ExcludedForMe);

    private static (Guid, RollMissReason?, string?, DateTimeOffset?, string, ExclusionReason?) Row(
        Guid game, RollMissReason? taken, string? by, IEnumerable<GameMarkView> marks, ExclusionReason? excluded) =>
        (game, taken, by, null, string.Join("; ", marks), excluded);

    private async Task<Guid> ActiveGameAsync(string login)
    {
        var client = await _site.SignedInAsync(login);
        var season = await client.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct);
        return season!.Me!.ActiveRun?.Game.Id ?? season.Me.Offer!.Id;
    }

    // ---- Success ----

    [Fact]
    public async Task Nothing_is_listed_before_the_first_roll()
    {
        Assert.Empty(await GamesAsync("vasya"));
    }

    [Fact]
    public async Task Offered_and_started_games_are_being_played_by_their_player()
    {
        var vasya = _site.Players["vasya"];
        await _site.SendAsync(new RollGame(vasya));
        var offered = await ActiveGameAsync("vasya");

        Assert.Equal([Row(offered, RollMissReason.BeingPlayed, "vasya", [], null)], (await GamesAsync("petya")).Select(Row));

        await _site.SendAsync(new StartRun(vasya));

        Assert.Equal([Row(offered, RollMissReason.BeingPlayed, "vasya", [], null)], (await GamesAsync("zritel")).Select(Row));
    }

    [Fact]
    public async Task Completed_game_says_who_and_when()
    {
        var vasya = _site.Players["vasya"];
        await _site.SendAsync(new RollGame(vasya));
        await _site.SendAsync(new StartRun(vasya));
        var game = await ActiveGameAsync("vasya");
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(5);
        await _site.SendAsync(new CompleteRun(vasya, Difficulty.Normal, null, null, false, null));

        var view = Assert.Single(await GamesAsync("petya"));

        Assert.Equal((game, RollMissReason.CompletedInSeason, "vasya", _site.Clock.UtcNow), (view.GameId, view.Taken!.Value, view.TakenBy, view.CompletedAt));
    }

    [Fact]
    public async Task Drop_is_a_mark_for_others_and_an_exclusion_for_the_one_who_dropped()
    {
        var vasya = _site.Players["vasya"];
        await _site.SendAsync(new RollGame(vasya));
        await _site.SendAsync(new StartRun(vasya));
        var game = await ActiveGameAsync("vasya");
        await _site.SendAsync(new DropRun(vasya));

        // Петя sees the game free, with Вася's mark
        Assert.Equal([Row(game, null, null, [new GameMarkView("vasya", GameMarkKind.Dropped)], null)], (await GamesAsync("petya")).Select(Row));

        // Вася sees no mark of his own: the game never comes to him again
        Assert.Equal([Row(game, null, null, [], ExclusionReason.Dropped)], (await GamesAsync("vasya")).Select(Row));
    }

    [Fact]
    public async Task Tech_reroll_is_a_mark_with_its_kind()
    {
        var vasya = _site.Players["vasya"];
        await _site.SendAsync(new RollGame(vasya));
        await _site.SendAsync(new StartRun(vasya));
        var game = await ActiveGameAsync("vasya");
        await _site.SendAsync(new TechReroll(vasya, TechRerollReason.DoesNotLaunch, null));

        var view = Assert.Single(await GamesAsync("petya"), v => v.GameId == game);

        Assert.Equal([new GameMarkView("vasya", GameMarkKind.TechRerolled)], view.Marks);
    }

    [Fact]
    public async Task Response_uses_camel_case_names_and_string_enums()
    {
        await _site.SendAsync(new RollGame(_site.Players["vasya"]));
        var client = await _site.SignedInAsync("petya");

        using var json = JsonDocument.Parse(await client.GetStringAsync(GamesUrl(SiteFactory.SeasonId), Ct));

        var row = json.RootElement[0];
        Assert.Equal(
            ["completedAt", "excludedForMe", "gameId", "marks", "taken", "takenBy"],
            row.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("beingPlayed", row.GetProperty("taken").GetString());
    }

    // ---- Refused ----

    [Fact]
    public async Task Anonymous_is_unauthorized()
    {
        var client = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(GamesUrl(SiteFactory.SeasonId), Ct)).StatusCode);
    }

    [Fact]
    public async Task Unknown_season_and_the_global_log_are_not_found()
    {
        var client = await _site.SignedInAsync("vasya");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(GamesUrl(Guid.NewGuid()), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(GamesUrl(Guid.Empty), Ct)).StatusCode);
    }

    [Fact]
    public async Task Malformed_season_id_is_not_found()
    {
        var client = await _site.SignedInAsync("vasya");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/seasons/not-a-guid/games", Ct)).StatusCode);
    }

    // ---- The contract ----

    [Fact]
    public async Task Pool_game_schema_keeps_its_own_fields()
    {
        // D-161: two records named GameView became one OpenAPI schema and the pool's lost its tags, cover and author
        var client = await _site.AnonymousAsync();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json", Ct));
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        var game = schemas.GetProperty("GameView").GetProperty("properties");
        Assert.All(["tags", "cover", "author", "note", "isDeleted"], name => Assert.True(game.TryGetProperty(name, out _), name));
        Assert.True(schemas.TryGetProperty("RunGameView", out _));
    }
}

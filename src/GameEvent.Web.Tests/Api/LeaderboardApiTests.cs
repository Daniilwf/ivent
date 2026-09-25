using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Ranking;
using GameEvent.Engine.Rulesets;
using GameEvent.Infrastructure.EventLog;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The leaderboard in the season view (C9b, P5, P7, P11; D-100): <c>leaderboard</c> — the rows in place order,
/// [{playerId, place, points, cellsToFinish, isFirst, provisional}], exactly the engine's <see cref="Leaderboard.Build"/>
/// of the season folded from the log. The first finisher is on top whatever his points. Anyone who may see the season
/// sees it, spectators too. Responses are read as raw JSON, so the tests compile before the DTOs exist.
/// </summary>
public sealed class LeaderboardApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Leaderboard_of_the_view_is_the_engines_leaderboard_of_the_season()
    {
        // Петя gets 50 points from the admin; Вася finishes (fewer points, but first); both are in the view
        var vasya = await _site.SignedInAsync("vasya");
        await _site.SendAsync(new AdjustPlayer(_site.Players["petya"], "Бонус за конкурс", PointsDelta: 50));
        await FinishAsync(vasya, "vasya");

        var rows = Rows(await SeasonJsonAsync(vasya));

        Assert.Equal(await ExpectedAsync(), rows);
        Assert.Equal((_site.Players["vasya"], 1, true, true), (rows[0].PlayerId, rows[0].Place, rows[0].IsFirst, rows[0].Provisional));
        Assert.Equal(0, rows[0].CellsToFinish);
        Assert.Equal((_site.Players["petya"], 2, 50), (rows[1].PlayerId, rows[1].Place, rows[1].Points));
    }

    [Fact]
    public async Task Without_a_finisher_the_leaderboard_goes_by_points()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await _site.SendAsync(new AdjustPlayer(_site.Players["petya"], "Бонус", PointsDelta: 3));

        var rows = Rows(await SeasonJsonAsync(vasya));

        Assert.Equal(await ExpectedAsync(), rows);
        Assert.Equal([_site.Players["petya"], _site.Players["vasya"]], rows.Select(r => r.PlayerId));
        Assert.All(rows, r => Assert.False(r.IsFirst));
        Assert.Equal(RulesetJson.Default().Map.LinearLength, rows[1].CellsToFinish);
    }

    [Fact]
    public async Task Approved_first_finish_is_no_longer_provisional()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await FinishAsync(vasya, "vasya");
        var admin = await _site.SignedInAsync("admin");

        await PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/runs/{runId}/approve", new { commandId = Guid.NewGuid(), comment = "Видел на стриме" });

        var first = Rows(await SeasonJsonAsync(vasya))[0];
        Assert.Equal((true, false), (first.IsFirst, first.Provisional));
    }

    [Fact]
    public async Task Spectator_sees_the_leaderboard()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await FinishAsync(vasya, "vasya");
        var spectator = await _site.SignedInAsync("zritel");

        var rows = Rows(await SeasonJsonAsync(spectator));

        Assert.Equal(await ExpectedAsync(), rows);
        Assert.Equal(_site.Players["vasya"], rows[0].PlayerId);
    }

    // ---- Tiebreakers from the projection (D-100) ----

    [Fact]
    public async Task Equal_points_rank_by_counted_runs_and_a_rejected_run_does_not_count()
    {
        // Вася completes A and B, the admin rejects B (1 counted run); Петя completes two (2 counted runs). The admin
        // then evens the points, Вася first — so were the rejected run counted, the earlier points would put Вася on top
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        await CompleteAsync(vasya);
        var runB = await CompleteAsync(vasya);
        await _site.SendAsync(new RejectProof(runB, "На скрине другая игра"));
        await CompleteAsync(petya);
        await CompleteAsync(petya);
        await EvenPointsAsync(vasya, "vasya", "petya");

        var rows = Rows(await SeasonJsonAsync(vasya));

        Assert.Equal(await ExpectedAsync(), rows);
        Assert.Equal([_site.Players["petya"], _site.Players["vasya"]], rows.Select(r => r.PlayerId));
        Assert.Equal([1, 2], rows.Select(r => r.Place));
    }

    [Fact]
    public async Task Equal_points_and_runs_rank_by_the_earlier_final_score()
    {
        // One run each; the admin evens the points, Петя first — his final score is earlier
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        await CompleteAsync(vasya);
        await CompleteAsync(petya);
        await EvenPointsAsync(vasya, "petya", "vasya");

        var rows = Rows(await SeasonJsonAsync(vasya));

        Assert.Equal(await ExpectedAsync(), rows);
        Assert.Equal([_site.Players["petya"], _site.Players["vasya"]], rows.Select(r => r.PlayerId));
        Assert.Equal([1, 2], rows.Select(r => r.Place));
    }

    [Fact]
    public async Task Equal_points_and_runs_the_other_way_round()
    {
        // The same with Вася evened first: Вася is on top, so the order is not the player id's
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        await CompleteAsync(vasya);
        await CompleteAsync(petya);
        await EvenPointsAsync(vasya, "vasya", "petya");

        var rows = Rows(await SeasonJsonAsync(vasya));

        Assert.Equal(await ExpectedAsync(), rows);
        Assert.Equal([_site.Players["vasya"], _site.Players["petya"]], rows.Select(r => r.PlayerId));
        Assert.Equal([1, 2], rows.Select(r => r.Place));
    }

    // ---- A revoked finish ----

    [Fact]
    public async Task Revoked_first_finish_puts_the_next_finisher_on_top_provisionally()
    {
        // Вася and Петя finish from the cell before the finish; the admin rejects Вася's finishing run — its dice exceed
        // the surplus (one step was needed), so his finish goes and Петя is first, not yet approved
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        var vasyaRun = await FinishAsync(vasya, "vasya");
        await FinishAsync(petya, "petya");
        var admin = await _site.SignedInAsync("admin");

        await PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/runs/{vasyaRun}/reject", new { commandId = Guid.NewGuid(), comment = "На скрине другая игра" });

        var rows = Rows(await SeasonJsonAsync(vasya));
        Assert.Equal(await ExpectedAsync(), rows);
        Assert.Equal((_site.Players["petya"], 1, true, true, 0), (rows[0].PlayerId, rows[0].Place, rows[0].IsFirst, rows[0].Provisional, rows[0].CellsToFinish));
        Assert.Equal((_site.Players["vasya"], 2, false), (rows[1].PlayerId, rows[1].Place, rows[1].IsFirst));
        Assert.NotEqual(0, rows[1].CellsToFinish);
    }

    // ---- Helpers ----

    private async Task<Guid> CompleteAsync(HttpClient client)
    {
        await PostOkAsync(client, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(client, Url("start"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(client, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        return Guid.Parse((await SeasonJsonAsync(client)).GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetString()!);
    }

    /// <summary>The admin brings both players to the same points above both, <paramref name="first"/> first.</summary>
    private async Task EvenPointsAsync(HttpClient client, string first, string second)
    {
        var players = (await SeasonJsonAsync(client)).GetProperty("players").EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetGuid(), p => p.GetProperty("points").GetInt32());
        var target = players.Values.Max() + 10;
        foreach (var login in new[] { first, second })
        {
            var id = _site.Players[login];
            await _site.SendAsync(new AdjustPlayer(id, "Выравнивание", PointsDelta: target - players[id]));
        }
    }

    private async Task<List<LeaderboardRow>> ExpectedAsync()
    {
        await using var db = _site.NewDb();
        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
        return [.. Leaderboard.Build(state)];
    }

    private static List<LeaderboardRow> Rows(JsonElement season) =>
        [.. season.GetProperty("leaderboard").EnumerateArray().Select(r => new LeaderboardRow(
            r.GetProperty("playerId").GetGuid(),
            r.GetProperty("place").GetInt32(),
            r.GetProperty("points").GetInt32(),
            r.GetProperty("cellsToFinish").ValueKind == JsonValueKind.Null ? null : r.GetProperty("cellsToFinish").GetInt32(),
            r.GetProperty("isFirst").GetBoolean(),
            r.GetProperty("provisional").GetBoolean()))];

    /// <summary>The admin puts the player one cell before the finish; the player rolls, starts and completes.</summary>
    private async Task<Guid> FinishAsync(HttpClient client, string login)
    {
        var beforeFinish = $"c{RulesetJson.Default().Map.LinearLength - 1}";
        await _site.SendAsync(new AdjustPlayer(_site.Players[login], "Перенос для теста", CellId: beforeFinish));
        await PostOkAsync(client, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(client, Url("start"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(client, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        return Guid.Parse((await SeasonJsonAsync(client)).GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetString()!);
    }

    private static async Task<JsonElement> SeasonJsonAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        return doc.RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> PostOkAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return response;
    }
}

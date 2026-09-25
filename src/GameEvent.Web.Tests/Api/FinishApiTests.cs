using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The finish in the season view (C9a, P2, P4, D-99): every player has <c>players[].finishOrder</c> — the order among
/// finishers, null when not finished; the player's own turn has <c>me.finish</c> — {order, frozen}, null when not
/// finished. The first finish is provisional (not frozen) until the admin approves its proof. Responses are read as raw
/// JSON, so the tests compile before the DTOs exist.
/// </summary>
public sealed class FinishApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Season_view_shows_the_finish_order_and_my_finish()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        var before = await SeasonJsonAsync(vasya);
        Assert.All(before.GetProperty("players").EnumerateArray(), p => Assert.Equal(JsonValueKind.Null, p.GetProperty("finishOrder").ValueKind));
        Assert.Equal(JsonValueKind.Null, before.GetProperty("me").GetProperty("finish").ValueKind);

        // When Вася, one cell before the finish, completes a run
        await FinishAsync(vasya, "vasya");

        // Then everyone sees his order; he sees his own finish, provisional; Петя has none
        var season = await SeasonJsonAsync(petya);
        Assert.Equal(1, PlayerOf(season, _site.Players["vasya"]).GetProperty("finishOrder").GetInt32());
        Assert.Equal(JsonValueKind.Null, PlayerOf(season, _site.Players["petya"]).GetProperty("finishOrder").ValueKind);
        Assert.Equal(JsonValueKind.Null, season.GetProperty("me").GetProperty("finish").ValueKind);
        var mine = (await SeasonJsonAsync(vasya)).GetProperty("me").GetProperty("finish");
        Assert.Equal(1, mine.GetProperty("order").GetInt32());
        Assert.False(mine.GetProperty("frozen").GetBoolean());
    }

    [Fact]
    public async Task Approving_the_first_finish_shows_the_player_frozen()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await FinishAsync(vasya, "vasya");
        var admin = await _site.SignedInAsync("admin");

        await PostOkAsync(
            admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/runs/{runId}/approve", new { commandId = Guid.NewGuid(), comment = "Видел на стриме" });

        var mine = (await SeasonJsonAsync(vasya)).GetProperty("me").GetProperty("finish");
        Assert.Equal(1, mine.GetProperty("order").GetInt32());
        Assert.True(mine.GetProperty("frozen").GetBoolean());
    }

    [Fact]
    public async Task Second_finisher_is_shown_with_order_two_and_not_frozen()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        await FinishAsync(vasya, "vasya");

        await FinishAsync(petya, "petya");

        var season = await SeasonJsonAsync(petya);
        Assert.Equal(2, PlayerOf(season, _site.Players["petya"]).GetProperty("finishOrder").GetInt32());
        var mine = season.GetProperty("me").GetProperty("finish");
        Assert.Equal((2, false), (mine.GetProperty("order").GetInt32(), mine.GetProperty("frozen").GetBoolean()));
    }

    [Fact]
    public async Task Two_finishes_sent_at_once_get_orders_one_and_two_by_the_queue()
    {
        // «Проверка на дыры»: two players one cell before the finish complete at the same moment — the single command queue
        // (L7) runs one after the other, so exactly one is first and the other second, never two firsts
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        var beforeFinish = $"c{RulesetJson.Default().Map.LinearLength - 1}";
        foreach (var (client, login) in new[] { (vasya, "vasya"), (petya, "petya") })
        {
            await _site.SendAsync(new AdjustPlayer(_site.Players[login], "Перенос для теста", CellId: beforeFinish));
            await PostOkAsync(client, Url("roll"), new { commandId = Guid.NewGuid() });
            await PostOkAsync(client, Url("start"), new { commandId = Guid.NewGuid() });
        }

        await Task.WhenAll(
            PostOkAsync(vasya, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" }),
            PostOkAsync(petya, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" }));

        var season = await SeasonJsonAsync(vasya);
        var orders = new[] { "vasya", "petya" }
            .Select(login => PlayerOf(season, _site.Players[login]).GetProperty("finishOrder").GetInt32())
            .Order();
        Assert.Equal([1, 2], orders);
    }

    // ---- Helpers ----

    // ---- The turn of a finisher (D-99) ----

    [Fact]
    public async Task Frozen_first_rerolls_in_free_mode_and_has_no_drop_penalty()
    {
        // Вася finishes, the admin approves: frozen. His next reroll is free («freeMode», 0 coins), his drop costs nothing
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await FinishAsync(vasya, "vasya");
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/runs/{runId}/approve", new { commandId = Guid.NewGuid(), comment = "Видел на стриме" });

        await PostOkAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        var rolling = (await SeasonJsonAsync(vasya)).GetProperty("me");
        var next = rolling.GetProperty("nextReroll");
        Assert.Equal(("freeMode", 0), (next.GetProperty("payment").GetString(), next.GetProperty("coins").GetInt32()));

        await PostOkAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });
        var playing = (await SeasonJsonAsync(vasya)).GetProperty("me");
        Assert.Equal("playing", playing.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, playing.GetProperty("dropPenalty").ValueKind);
    }

    [Fact]
    public async Task Later_finisher_drop_penalty_does_not_affect_the_position()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        await FinishAsync(vasya, "vasya");
        await FinishAsync(petya, "petya");

        await PostOkAsync(petya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(petya, Url("start"), new { commandId = Guid.NewGuid() });

        var penalty = (await SeasonJsonAsync(petya)).GetProperty("me").GetProperty("dropPenalty");
        Assert.Equal(JsonValueKind.Object, penalty.ValueKind);
        Assert.False(penalty.GetProperty("affectsPosition").GetBoolean());
        Assert.True(penalty.GetProperty("affectsPoints").GetBoolean());
    }

    [Fact]
    public async Task Proof_queue_marks_the_runs_that_decide_a_finish()
    {
        // Вася: run A from the start (not a finish), then run B from the cell before the finish — both decide his finish;
        // Петя's run from the start decides nothing
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        var runA = await CompleteAsync(vasya);
        var runB = await FinishAsync(vasya, "vasya");
        var petyaRun = await CompleteAsync(petya);
        var admin = await _site.SignedInAsync("admin");

        using var queue = JsonDocument.Parse(await admin.GetStringAsync($"/api/admin/seasons/{SiteFactory.SeasonId}/proofs", Ct));

        var decides = queue.RootElement.EnumerateArray().ToDictionary(i => i.GetProperty("runId").GetGuid(), i => i.GetProperty("decidesFinish").GetBoolean());
        Assert.Equal([runA, runB, petyaRun], decides.Keys);
        Assert.Equal([true, true, false], decides.Values);
    }

    private async Task<Guid> CompleteAsync(HttpClient client)
    {
        await PostOkAsync(client, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(client, Url("start"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(client, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        return Guid.Parse((await SeasonJsonAsync(client)).GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetString()!);
    }

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

    private static JsonElement PlayerOf(JsonElement season, Guid playerId) =>
        season.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == playerId);

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

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The limit of runs waiting for the admin's check (D-134, <c>season.maxUncheckedRuns</c>, 2 by default): the season
/// view tells the player how many wait and the limit; at the limit the roll is refused with <c>roll.tooManyUnchecked</c>;
/// the admin's check opens it again. Another player is not held by it.
/// </summary>
public sealed class UncheckedLimitApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Season => $"/api/seasons/{SiteFactory.SeasonId}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Two_runs_waiting_for_the_check_hold_the_roll_until_the_admin_checks_one()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");

        var first = await PlayOneAsync(vasya);
        Assert.Equal((1, 2), await UncheckedAsync(vasya));
        await PlayOneAsync(vasya);
        Assert.Equal((2, 2), await UncheckedAsync(vasya));

        var refused = await vasya.PostAsJsonAsync($"{Season}/roll", new { commandId = Guid.NewGuid() }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("roll.tooManyUnchecked", (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
        Assert.Equal("idle", (await MeAsync(vasya)).GetProperty("phase").GetString());

        // A proof can still be sent while the roll waits; a sent proof still waits; the admin's check opens the roll
        await PostOkAsync(vasya, $"{Season}/runs/{first}/proof", new { commandId = Guid.NewGuid(), links = new[] { "https://imgur.com/a/credits" } });
        Assert.Equal((2, 2), await UncheckedAsync(vasya));
        await PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/runs/{first}/approve", new { commandId = Guid.NewGuid() });

        Assert.Equal((1, 2), await UncheckedAsync(vasya));
        await PostOkAsync(vasya, $"{Season}/roll");
    }

    [Fact]
    public async Task A_rejected_run_does_not_wait_either()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        var first = await PlayOneAsync(vasya);
        await PlayOneAsync(vasya);

        await PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/runs/{first}/reject", new { commandId = Guid.NewGuid(), comment = "Нет пруфа" });

        Assert.Equal((1, 2), await UncheckedAsync(vasya));
        await PostOkAsync(vasya, $"{Season}/roll");
    }

    [Fact]
    public async Task Another_players_waiting_runs_do_not_hold_my_roll()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        await PlayOneAsync(vasya);
        await PlayOneAsync(vasya);

        Assert.Equal((0, 2), await UncheckedAsync(petya));
        await PostOkAsync(petya, $"{Season}/roll");
    }

    [Fact]
    public async Task Without_a_limit_the_view_says_nothing_and_the_roll_is_open()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        await ChangeLimitAsync(admin, null);
        await PlayOneAsync(vasya);
        await PlayOneAsync(vasya);

        Assert.Equal(JsonValueKind.Null, (await MeAsync(vasya)).GetProperty("unchecked").ValueKind);
        await PostOkAsync(vasya, $"{Season}/roll");
    }

    // ---- The admin's proof queue marks the players whose roll is closed (H8, D-134) ----

    [Fact]
    public async Task Queue_marks_the_runs_of_a_player_whose_roll_the_limit_closed()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        var admin = await _site.SignedInAsync("admin");
        await PlayOneAsync(vasya);
        await PlayOneAsync(petya);

        // Below the limit nobody is marked
        Assert.All(await QueueAsync(admin), item => Assert.False(item.GetProperty("rollClosed").GetBoolean()));

        await PlayOneAsync(vasya);
        var queue = await QueueAsync(admin);

        Assert.Equal(3, queue.Count);
        var vasyaId = (await MeAsync(vasya)).GetProperty("playerId").GetGuid();
        Assert.All(queue, item => Assert.Equal(item.GetProperty("playerId").GetGuid() == vasyaId, item.GetProperty("rollClosed").GetBoolean()));
    }

    [Fact]
    public async Task Queue_marks_nobody_without_a_limit()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        await ChangeLimitAsync(admin, null);
        await PlayOneAsync(vasya);
        await PlayOneAsync(vasya);

        var queue = await QueueAsync(admin);

        Assert.Equal(2, queue.Count);
        Assert.All(queue, item => Assert.False(item.GetProperty("rollClosed").GetBoolean()));
    }

    [Fact]
    public async Task The_check_takes_the_mark_off()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        var first = await PlayOneAsync(vasya);
        await PlayOneAsync(vasya);

        await PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/runs/{first}/approve", new { commandId = Guid.NewGuid(), comment = "Видел на стриме" });

        var left = Assert.Single(await QueueAsync(admin));
        Assert.False(left.GetProperty("rollClosed").GetBoolean());
    }

    // ---- Helpers ----

    private static async Task<IReadOnlyList<JsonElement>> QueueAsync(HttpClient admin) =>
        [.. (await OkAsync(await admin.GetAsync($"/api/admin/seasons/{SiteFactory.SeasonId}/proofs", Ct))).EnumerateArray()];

    private static async Task ChangeLimitAsync(HttpClient admin, int? limit)
    {
        using var current = JsonDocument.Parse(await admin.GetStringAsync($"{Season}/rules", Ct));
        var ruleset = JsonNode.Parse(current.RootElement.GetProperty("ruleset").GetRawText())!;
        ruleset["season"]!["maxUncheckedRuns"] = limit;
        var response = await admin.PutAsJsonAsync(
            $"/api/admin/seasons/{SiteFactory.SeasonId}/rules",
            new { commandId = Guid.NewGuid(), expectedVersion = current.RootElement.GetProperty("version").GetInt32(), ruleset },
            Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Rolls, starts and completes one game; returns the completed run's id</summary>
    private static async Task<Guid> PlayOneAsync(HttpClient player)
    {
        await PostOkAsync(player, $"{Season}/roll");
        var me = await MeAsync(player);
        if (me.GetProperty("choice").ValueKind == JsonValueKind.Object)
        {
            var choice = me.GetProperty("choice");
            var option = choice.GetProperty("options").EnumerateArray().First(o => o.GetProperty("game").ValueKind == JsonValueKind.Object);
            await PostOkAsync(player, $"{Season}/choose", new { commandId = Guid.NewGuid(), choiceId = choice.GetProperty("id").GetGuid(), optionId = option.GetProperty("id").GetGuid() });
        }
        else
        {
            await PostOkAsync(player, $"{Season}/start");
        }

        var hoursKnown = (await MeAsync(player)).GetProperty("activeRun").GetProperty("game").GetProperty("hours").ValueKind == JsonValueKind.Number;
        await PostOkAsync(player, $"{Season}/complete", hoursKnown
            ? new { commandId = Guid.NewGuid(), difficulty = "normal" }
            : new { commandId = Guid.NewGuid(), difficulty = "normal", hours = 5m, hoursSource = "HowLongToBeat" });
        return (await MeAsync(player)).GetProperty("lastCompleted").GetProperty("id").GetGuid();
    }

    private static async Task<(int Count, int Limit)> UncheckedAsync(HttpClient player)
    {
        var waiting = (await MeAsync(player)).GetProperty("unchecked");
        return (waiting.GetProperty("count").GetInt32(), waiting.GetProperty("limit").GetInt32());
    }

    private static async Task<JsonElement> MeAsync(HttpClient player) =>
        (await OkAsync(await player.GetAsync(Season, Ct))).GetProperty("me");

}

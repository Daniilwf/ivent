using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Queue;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Findings of the exploratory testing of stages 1 and 2, area «season» (K1S): what the player's own turn card and the
/// current season show, and the game actions sent from a tab that showed an older turn (D-600…D-604).
/// </summary>
public sealed class TurnCardApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Season => $"/api/seasons/{SiteFactory.SeasonId}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- K1S-10, K1S-14: the current season ----

    [Fact]
    public async Task A_new_draft_does_not_hide_the_closing_season_the_player_still_sends_proofs_to()
    {
        // Given Вася's season is closing and the admin prepares a new draft with Вася in it
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/status", new { commandId = Guid.NewGuid(), to = "closing" });
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddDays(1);
        var draft = await _site.CreateSeasonAsync();
        await SendToAsync(draft, new AddSeasonPlayer(Guid.NewGuid(), _site.Users["vasya"], "vasya"));

        // Then Вася still sees the closing season
        Assert.Equal(SiteFactory.SeasonId, await CurrentAsync("vasya"));
    }

    [Fact]
    public async Task The_running_season_comes_before_a_newer_finished_one_and_a_newer_draft()
    {
        // Given a newer season that has finished, with Вася in it, and a newer draft still
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddDays(1);
        var finished = await _site.CreateSeasonAsync();
        await SendToAsync(finished, new AddSeasonPlayer(Guid.NewGuid(), _site.Users["vasya"], "vasya"));
        foreach (var status in new[] { SeasonStatus.Active, SeasonStatus.Closing, SeasonStatus.Finished })
        {
            await SendToAsync(finished, new ChangeSeasonStatus(status));
        }

        _site.Clock.UtcNow = _site.Clock.UtcNow.AddDays(1);
        await _site.CreateSeasonAsync();

        // Then Вася and a spectator see the season that is running
        Assert.Equal(SiteFactory.SeasonId, await CurrentAsync("vasya"));
        Assert.Equal(SiteFactory.SeasonId, await CurrentAsync("zritel"));
    }

    [Fact]
    public async Task A_player_only_in_a_draft_sees_the_draft()
    {
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddDays(1);
        var draft = await _site.CreateSeasonAsync();
        await SendToAsync(draft, new AddSeasonPlayer(Guid.NewGuid(), _site.Users["masha"], "masha"));

        Assert.Equal(draft, await CurrentAsync("masha"));
    }

    // ---- K1S-1, K1S-9: every run that waits takes a proof; a rejection is told on the card ----

    [Fact]
    public async Task An_older_run_waiting_for_the_check_is_on_the_card_and_takes_a_proof()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var first = await PlayOneAsync(vasya);
        var second = await PlayOneAsync(vasya);

        var me = await MeAsync(vasya);
        Assert.Equal(second, me.GetProperty("lastCompleted").GetProperty("id").GetGuid());
        var check = Assert.Single(me.GetProperty("checks").EnumerateArray());
        Assert.Equal(first, check.GetProperty("id").GetGuid());
        Assert.Equal("completed", check.GetProperty("status").GetString());

        await PostOkAsync(vasya, $"{Season}/runs/{first}/proof", new { commandId = Guid.NewGuid(), links = new[] { "https://imgur.com/a/credits" } });

        var sent = Assert.Single((await MeAsync(vasya)).GetProperty("checks").EnumerateArray());
        Assert.Equal("pending", sent.GetProperty("proof").GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_approved_run_leaves_the_card()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        var first = await PlayOneAsync(vasya);
        await PlayOneAsync(vasya);

        await PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/runs/{first}/approve", new { commandId = Guid.NewGuid(), comment = "Видел на стриме" });

        Assert.Empty((await MeAsync(vasya)).GetProperty("checks").EnumerateArray());
    }

    [Fact]
    public async Task A_rejection_of_an_older_run_is_on_the_card_with_its_comment_until_the_next_roll()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        var first = await PlayOneAsync(vasya);
        await PlayOneAsync(vasya);

        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(5);
        await PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/runs/{first}/reject", new { commandId = Guid.NewGuid(), comment = "На ссылке нет титров" });

        var rejected = Assert.Single((await MeAsync(vasya)).GetProperty("checks").EnumerateArray());
        Assert.Equal(first, rejected.GetProperty("id").GetGuid());
        Assert.Equal("rejected", rejected.GetProperty("status").GetString());
        Assert.Equal("На ссылке нет титров", rejected.GetProperty("proof").GetProperty("comment").GetString());

        // The next roll moves on: the rejection stays in the feed
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(5);
        await PostOkAsync(vasya, $"{Season}/roll");
        Assert.Empty((await MeAsync(vasya)).GetProperty("checks").EnumerateArray());
    }

    // ---- K1S-2: the form shows the proof that was sent ----

    [Fact]
    public async Task The_sent_proof_names_its_witness()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var run = await PlayOneAsync(vasya);

        await PostOkAsync(vasya, $"{Season}/runs/{run}/proof", new { commandId = Guid.NewGuid(), links = Array.Empty<string>(), witnessId = _site.Players["petya"] });

        var proof = (await MeAsync(vasya)).GetProperty("lastCompleted").GetProperty("proof");
        Assert.Equal(_site.Players["petya"], proof.GetProperty("witnessId").GetGuid());
    }

    // ---- K1S-3: the player sees their coins ----

    [Fact]
    public async Task The_player_sees_their_own_coins()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await _site.SendAsync(new AdjustPlayer(_site.Players["vasya"], "Стартовый капитал", CoinsDelta: 20));

        Assert.Equal(20, (await MeAsync(vasya)).GetProperty("coins").GetInt32());
    }

    // ---- K1S-11: a tab that showed an older turn ----

    [Fact]
    public async Task A_reroll_expecting_the_free_one_after_it_was_spent_is_refused_and_takes_no_coins()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await _site.SendAsync(new AdjustPlayer(_site.Players["vasya"], "Стартовый капитал", CoinsDelta: 20));
        await PostOkAsync(vasya, $"{Season}/roll");
        await PostOkAsync(vasya, $"{Season}/reroll", new { commandId = Guid.NewGuid(), expectedPayment = "freeThisRoll" });

        var stale = await vasya.PostAsJsonAsync($"{Season}/reroll", new { commandId = Guid.NewGuid(), expectedPayment = "freeThisRoll" }, Ct);

        await AssertConflictAsync(stale, RejectionCodes.TurnStale);
        Assert.Equal(20, (await MeAsync(vasya)).GetProperty("coins").GetInt32());
    }

    [Fact]
    public async Task A_start_of_a_game_no_longer_offered_is_refused()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, $"{Season}/roll");
        var seen = (await MeAsync(vasya)).GetProperty("offer").GetProperty("id").GetGuid();
        await PostOkAsync(vasya, $"{Season}/reroll");
        Assert.NotEqual(seen, (await MeAsync(vasya)).GetProperty("offer").GetProperty("id").GetGuid());

        var stale = await vasya.PostAsJsonAsync($"{Season}/start", new { commandId = Guid.NewGuid(), gameId = seen }, Ct);

        await AssertConflictAsync(stale, RejectionCodes.TurnStale);
        Assert.Equal("rolling", (await MeAsync(vasya)).GetProperty("phase").GetString());
    }

    [Fact]
    public async Task A_drop_of_a_run_that_is_not_the_active_one_is_refused()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, $"{Season}/roll");
        await PostOkAsync(vasya, $"{Season}/start");

        var stale = await vasya.PostAsJsonAsync($"{Season}/drop", new { commandId = Guid.NewGuid(), runId = Guid.NewGuid() }, Ct);

        await AssertConflictAsync(stale, RejectionCodes.TurnStale);
        Assert.Equal("playing", (await MeAsync(vasya)).GetProperty("phase").GetString());
    }

    [Fact]
    public async Task An_unknown_expected_payment_is_invalid()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, $"{Season}/roll");

        var response = await vasya.PostAsJsonAsync($"{Season}/reroll", new { commandId = Guid.NewGuid(), expectedPayment = 99 }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_spectator_cannot_send_a_reroll_with_an_expected_payment()
    {
        var zritel = await _site.SignedInAsync("zritel");

        var response = await zritel.PostAsJsonAsync($"{Season}/reroll", new { commandId = Guid.NewGuid(), expectedPayment = "freeThisRoll" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- K1S-16: two seasons with one name ----

    [Fact]
    public async Task A_second_season_with_the_same_name_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync("/api/admin/seasons", new { commandId = Guid.NewGuid(), seasonId = Guid.NewGuid(), name = " тестовый СЕЗОН " }, Ct);

        await AssertConflictAsync(response, "season.nameTaken");
    }

    // ---- Helpers ----

    private static async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private async Task SendToAsync(Guid seasonId, ICommand command)
    {
        var outcome = await _site.Services.GetRequiredService<CommandBus>().SendAsync(new CommandEnvelope(Guid.NewGuid(), seasonId, command, AuthorId: null), Ct);
        Assert.True(outcome.IsAccepted, outcome.Rejection?.ToString());
    }

    private async Task<Guid> CurrentAsync(string login)
    {
        var client = await _site.SignedInAsync(login);
        return (await OkAsync(await client.GetAsync("/api/seasons/current", Ct))).GetProperty("id").GetGuid();
    }

    /// <summary>Rolls, starts and completes one game; returns the completed run's id</summary>
    private async Task<Guid> PlayOneAsync(HttpClient player)
    {
        // One minute apart: the latest completed run is the latest started one
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(1);
        await PostOkAsync(player, $"{Season}/roll");
        await PostOkAsync(player, $"{Season}/start");
        await PostOkAsync(player, $"{Season}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });
        return (await MeAsync(player)).GetProperty("lastCompleted").GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> MeAsync(HttpClient player) =>
        (await OkAsync(await player.GetAsync(Season, Ct))).GetProperty("me");
}

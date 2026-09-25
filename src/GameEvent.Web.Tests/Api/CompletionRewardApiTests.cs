using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Rulesets;
using GameEvent.Web.Seasons;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The completion reward over HTTP (C7a, D-96): <c>POST complete</c> takes an optional <c>hoursSource</c>,
/// <c>challengeDone</c> and <c>review {rating, text}</c>; <c>POST runs/{runId}/review</c> reviews one's own completed
/// run later. Shape errors (rating outside 1..10, text over 2000, source over 300 characters, missing fields) are 400
/// at the request level; rule refusals are 409 with the engine code. The season view shows the last completed run's
/// challenge dice and review. The new fields are read from raw JSON, so the tests compile before the DTOs change.
/// </summary>
public sealed class CompletionRewardApiTests : IAsyncLifetime
{
    private const string Source = "https://howlongtobeat.com/game/2231";

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    private static string ReviewUrl(Guid runId) => Url($"runs/{runId}/review");

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- Complete with the new parts ----

    [Fact]
    public async Task Completion_with_challenge_and_review_logs_every_part_and_the_view_shows_them()
    {
        await EnableChallengesAsync();
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });

        var complete = await PostAsync(vasya, Url("complete"), new
        {
            commandId = Guid.NewGuid(),
            difficulty = "extreme",
            challengeDone = true,
            review = new { rating = 9, text = "Страшно и красиво" },
        });

        Assert.Equal(
            ["run-completed", "completion-rolled", "points-changed", "player-moved", "coins-changed", "manual-effect-created", "run-reviewed"],
            await TypesAsync(complete));

        var me = await MeJsonAsync(vasya);
        var last = me.GetProperty("lastCompleted");
        var dice = last.GetProperty("dice").EnumerateArray().Select(d => d.GetProperty("value").GetInt32()).ToList();
        var challenge = last.GetProperty("challengeDice").EnumerateArray().ToList();
        Assert.Single(challenge); // default ruleset: challengeBonus.extraDice 1
        Assert.All(challenge, d => Assert.Equal(6, d.GetProperty("sides").GetInt32()));
        var total = dice.Sum() + challenge.Sum(d => d.GetProperty("value").GetInt32());
        Assert.Equal(total, last.GetProperty("total").GetInt32());
        Assert.Equal(total, await PointsOfAsync(vasya, "vasya"));
        var review = last.GetProperty("review");
        Assert.Equal(9, review.GetProperty("rating").GetInt32());
        Assert.Equal("Страшно и красиво", review.GetProperty("text").GetString());

        // The good event of «выше сложной» waits among the manual effects
        var effect = Assert.Single(me.GetProperty("manualEffects").EnumerateArray());
        Assert.Equal(("good", "difficulty"), (effect.GetProperty("drawEvent").GetString(), effect.GetProperty("source").GetString()));
    }

    [Fact]
    public async Task Challenge_with_the_feature_off_is_a_conflict_and_changes_nothing()
    {
        // D-96 (1): the default ruleset has features.challenges off
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });
        await using (var before = _site.NewDb())
        {
            var logLength = await before.Events.CountAsync(Ct);

            var response = await vasya.PostAsJsonAsync(
                Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal", challengeDone = true }, Ct);

            await AssertConflictAsync(response, "feature.disabled");
            await using var after = _site.NewDb();
            Assert.Equal(logLength, await after.Events.CountAsync(Ct));
        }

        var me = await MeJsonAsync(vasya);
        Assert.Equal("playing", me.GetProperty("phase").GetString());
        Assert.Equal(0, await PointsOfAsync(vasya, "vasya"));
    }

    [Fact]
    public async Task Challenges_are_disabled_in_the_view_by_default()
    {
        var vasya = await _site.SignedInAsync("vasya");

        Assert.False((await MeJsonAsync(vasya)).GetProperty("challengesEnabled").GetBoolean());
    }

    [Fact]
    public async Task Challenges_are_enabled_in_the_view_with_the_flag()
    {
        await EnableChallengesAsync();
        var vasya = await _site.SignedInAsync("vasya");

        Assert.True((await MeJsonAsync(vasya)).GetProperty("challengesEnabled").GetBoolean());
    }

    [Fact]
    public async Task Completion_without_a_claim_carries_challenge_done_false_with_the_feature_off()
    {
        // db9dc14: the form always sends challengeDone; false must pass while challenges are off
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });

        var complete = await PostAsync(vasya, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal", challengeDone = false });

        Assert.Contains("run-completed", await TypesAsync(complete));
        Assert.Equal(0, (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("challengeDice").GetArrayLength());
    }

    [Fact]
    public async Task Review_text_is_trimmed_on_the_server()
    {
        // D-96 (4)
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });

        await PostAsync(vasya, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal", review = new { rating = 8, text = "  Отлично  " } });

        var review = (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("review");
        Assert.Equal("Отлично", review.GetProperty("text").GetString());
        await using var db = _site.NewDb();
        Assert.Equal("Отлично", Assert.Single(await db.Reviews.AsNoTracking().ToListAsync(Ct)).Text);
    }

    [Fact]
    public async Task Later_review_text_is_trimmed_and_blank_is_no_text()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        await PostAsync(vasya, ReviewUrl(runId), new { commandId = Guid.NewGuid(), rating = 6, text = "  Отлично  " });
        var trimmed = (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("review");
        Assert.Equal("Отлично", trimmed.GetProperty("text").GetString());

        await PostAsync(vasya, ReviewUrl(runId), new { commandId = Guid.NewGuid(), rating = 6, text = "   " });
        var blank = (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("review");
        Assert.Equal(JsonValueKind.Null, blank.GetProperty("text").ValueKind);
    }

    [Fact]
    public async Task Plain_completion_pays_coins_and_has_no_challenge_dice_or_review()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });

        var complete = await PostAsync(vasya, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });

        Assert.Equal(["run-completed", "completion-rolled", "points-changed", "player-moved", "coins-changed"], await TypesAsync(complete));
        var last = (await MeJsonAsync(vasya)).GetProperty("lastCompleted");
        Assert.Equal(0, last.GetProperty("challengeDice").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("review").ValueKind);
        await using var db = _site.NewDb();
        var row = await db.SeasonPlayers.AsNoTracking().SingleAsync(p => p.Id == _site.Players["vasya"], Ct);
        Assert.True(row.Coins >= 3, $"Completion coins are at least the minimum, got {row.Coins}."); // default: min 3
    }

    [Fact]
    public async Task Estimate_needs_a_source_when_the_game_has_no_hours()
    {
        await RemovePoolHoursAsync();
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });

        // Without a source: the engine's rule, 409 with its code
        var refused = await vasya.PostAsJsonAsync(Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal", estimatedHours = 6 }, Ct);
        await AssertConflictAsync(refused, "run.hoursSourceRequired");
        Assert.Equal("playing", (await MeJsonAsync(vasya)).GetProperty("phase").GetString());

        // With a source: completed, the source kept with the run
        await PostAsync(vasya, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal", estimatedHours = 6, hoursSource = Source });
        await using var db = _site.NewDb();
        Assert.Equal(Source, (await db.Runs.AsNoTracking().SingleAsync(Ct)).HoursSource);
    }

    [Fact]
    public async Task Completion_with_an_invalid_review_changes_nothing()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });

        var response = await vasya.PostAsJsonAsync(
            Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal", review = new { rating = 11, text = "ok" } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("playing", (await MeJsonAsync(vasya)).GetProperty("phase").GetString());
    }

    [Theory]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000101","difficulty":"hard","review":{"rating":0}}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000102","difficulty":"hard","review":{"rating":11}}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000103","difficulty":"hard","review":{"text":"без оценки"}}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000104","difficulty":"hard","review":{"rating":"десять"}}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000105","difficulty":"hard","challengeDone":"yes"}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000106","difficulty":"hard","hoursSource":42}""")]
    public async Task Invalid_completion_parts_are_a_bad_request(string body)
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });

        var response = await vasya.PostAsync(Url("complete"), new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("playing", (await MeJsonAsync(vasya)).GetProperty("phase").GetString());
    }

    [Fact]
    public async Task Review_text_over_2000_characters_is_a_bad_request()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });

        var response = await vasya.PostAsJsonAsync(
            Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal", review = new { rating = 5, text = new string('я', 2001) } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Hours_source_over_300_characters_is_a_bad_request()
    {
        await RemovePoolHoursAsync();
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });

        var response = await vasya.PostAsJsonAsync(
            Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal", estimatedHours = 6, hoursSource = new string('я', 301) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("playing", (await MeJsonAsync(vasya)).GetProperty("phase").GetString());
    }

    // ---- Review later ----

    [Fact]
    public async Task Player_reviews_their_completed_run_later_and_can_change_the_review()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        var first = await PostAsync(vasya, ReviewUrl(runId), new { commandId = Guid.NewGuid(), rating = 7, text = "Неплохо" });
        Assert.Equal(["run-reviewed"], await TypesAsync(first));
        var review = (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("review");
        Assert.Equal((7, "Неплохо"), (review.GetProperty("rating").GetInt32(), review.GetProperty("text").GetString()));

        // A newer review replaces the earlier one; the text is optional
        await PostAsync(vasya, ReviewUrl(runId), new { commandId = Guid.NewGuid(), rating = 4 });
        var changed = (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("review");
        Assert.Equal(4, changed.GetProperty("rating").GetInt32());
        Assert.Equal(JsonValueKind.Null, changed.GetProperty("text").ValueKind);
        await using var db = _site.NewDb();
        var row = Assert.Single(await db.Reviews.AsNoTracking().ToListAsync(Ct));
        Assert.Equal((runId, 4, (string?)null), (row.RunId, row.Rating, row.Text));
    }

    [Fact]
    public async Task Repeating_a_review_with_the_same_command_id_acts_once()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        var body = new { commandId = Guid.NewGuid(), rating = 6, text = "Норм" };

        await PostAsync(vasya, ReviewUrl(runId), body);
        var again = await PostAsync(vasya, ReviewUrl(runId), body);

        Assert.True((await again.Content.ReadFromJsonAsync<CommandResponse>(Ct))!.Duplicate);
        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Events.CountAsync(e => e.Type == "run-reviewed", Ct));
    }

    [Fact]
    public async Task Another_players_run_is_a_conflict_with_not_yours()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        var petya = await _site.SignedInAsync("petya");

        var response = await petya.PostAsJsonAsync(ReviewUrl(runId), new { commandId = Guid.NewGuid(), rating = 1, text = "Плохо" }, Ct);

        await AssertConflictAsync(response, "run.notYours");
        Assert.Equal(JsonValueKind.Null, (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("review").ValueKind);
    }

    [Fact]
    public async Task Run_being_played_is_a_conflict_with_not_completed()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });
        var runId = Guid.Parse((await MeJsonAsync(vasya)).GetProperty("activeRun").GetProperty("id").GetString()!);

        var response = await vasya.PostAsJsonAsync(ReviewUrl(runId), new { commandId = Guid.NewGuid(), rating = 5 }, Ct);

        await AssertConflictAsync(response, "run.notCompleted");
    }

    [Fact]
    public async Task Unknown_run_is_a_conflict_with_unknown_run()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(ReviewUrl(Guid.NewGuid()), new { commandId = Guid.NewGuid(), rating = 5 }, Ct);

        await AssertConflictAsync(response, "run.unknown");
    }

    [Theory]
    [InlineData("zritel")]
    [InlineData("admin")]
    [InlineData("masha")]
    public async Task Spectator_admin_and_player_outside_the_season_are_forbidden_to_review(string login)
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        var client = await _site.SignedInAsync(login);

        var response = await client.PostAsJsonAsync(ReviewUrl(runId), new { commandId = Guid.NewGuid(), rating = 5 }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = _site.NewDb();
        Assert.Equal(0, await db.Events.CountAsync(e => e.Type == "run-reviewed", Ct));
    }

    [Fact]
    public async Task Anonymous_review_is_unauthorized()
    {
        var client = await _site.AnonymousAsync();

        var response = await client.PostAsJsonAsync(ReviewUrl(Guid.NewGuid()), new { commandId = Guid.NewGuid(), rating = 5 }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Review_without_the_antiforgery_token_is_refused()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        vasya.DefaultRequestHeaders.Remove(Hosting.Csrf.HeaderName);

        var response = await vasya.PostAsJsonAsync(ReviewUrl(runId), new { commandId = Guid.NewGuid(), rating = 5 }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Review_in_an_unknown_season_is_not_found()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(
            $"/api/seasons/{Guid.NewGuid()}/runs/{Guid.NewGuid()}/review", new { commandId = Guid.NewGuid(), rating = 5 }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"commandId":"00000000-0000-0000-0000-000000000000","rating":5}""")]
    [InlineData("""{"rating":5}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000201"}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000202","rating":0}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000203","rating":11}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000204","rating":null}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000205","rating":5,"text":42}""")]
    [InlineData("not json")]
    public async Task Invalid_review_is_a_bad_request(string body)
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        var response = await vasya.PostAsync(ReviewUrl(runId), new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Review_text_over_2000_characters_later_is_a_bad_request()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        var response = await vasya.PostAsJsonAsync(ReviewUrl(runId), new { commandId = Guid.NewGuid(), rating = 5, text = new string('я', 2001) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("review").ValueKind);
    }

    [Fact]
    public async Task Review_text_of_exactly_2000_characters_is_accepted()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        await PostAsync(vasya, ReviewUrl(runId), new { commandId = Guid.NewGuid(), rating = 5, text = new string('я', 2000) });

        Assert.Equal(2000, (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("review").GetProperty("text").GetString()!.Length);
    }

    // ---- Helpers ----

    private async Task<Guid> CompletedRunAsync(HttpClient client)
    {
        await PostAsync(client, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostAsync(client, Url("start"), new { commandId = Guid.NewGuid() });
        await PostAsync(client, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        return Guid.Parse((await MeJsonAsync(client)).GetProperty("lastCompleted").GetProperty("id").GetString()!);
    }

    /// <summary>Turns <c>features.challenges</c> on in the seeded season (D-96 (1)).</summary>
    private Task EnableChallengesAsync()
    {
        var ruleset = RulesetJson.Default();
        return _site.SendAsync(new ChangeRuleset(ruleset with { Features = ruleset.Features with { Challenges = true } }));
    }

    private async Task RemovePoolHoursAsync()
    {
        await using var db = _site.NewDb();
        await db.Games.ExecuteUpdateAsync(g => g.SetProperty(x => x.Hours, (decimal?)null), Ct);
    }

    private async Task<int> PointsOfAsync(HttpClient client, string login)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        return doc.RootElement.GetProperty("players").EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == _site.Players[login]).GetProperty("points").GetInt32();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return response;
    }

    private static async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task<JsonElement> MeJsonAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        return doc.RootElement.GetProperty("me").Clone();
    }

    private static async Task<List<string>> TypesAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. doc.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()!)];
    }
}

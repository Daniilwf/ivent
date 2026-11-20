using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Rulesets;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.Api;

/// <summary>The public reactions on a review use the signed-in account and the review's season (SPEC «Лента и общение»).</summary>
public sealed class ReviewReactionApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_player_and_a_spectator_have_separate_selections_on_an_existing_review()
    {
        var (gameId, runId) = await CompleteAsync();
        await EnableReactionsAsync();
        var petya = await _site.SignedInAsync("petya");
        var spectator = await _site.SignedInAsync("zritel");
        var author = await _site.SignedInAsync("vasya");
        var url = Reaction(gameId, runId);

        Assert.Equal([("🔥", 0, false), ("❤️", 0, false)], await ReadReactionsAsync(petya, gameId, runId));
        await SetAsync(petya, url, "🔥", true);
        await SetAsync(petya, url, "❤️", true);
        await SetAsync(spectator, url, "🔥", true);

        Assert.Equal([("🔥", 2, true), ("❤️", 1, true)], await ReadReactionsAsync(petya, gameId, runId));
        Assert.Equal([("🔥", 2, true), ("❤️", 1, false)], await ReadReactionsAsync(spectator, gameId, runId));
        Assert.Equal([("🔥", 2, false), ("❤️", 1, false)], await ReadReactionsAsync(author, gameId, runId));

        await SetAsync(spectator, url, "🔥", false);

        Assert.Equal([("🔥", 1, false), ("❤️", 1, false)], await ReadReactionsAsync(spectator, gameId, runId));
        Assert.Equal([("🔥", 1, true), ("❤️", 1, true)], await ReadReactionsAsync(petya, gameId, runId));
    }

    [Fact]
    public async Task Two_tabs_and_an_http_retry_leave_one_reaction_on_the_review()
    {
        var (gameId, runId) = await CompleteAsync();
        await EnableReactionsAsync();
        var firstTab = await _site.SignedInAsync("petya");
        var secondTab = await _site.SignedInAsync("petya");
        var url = Reaction(gameId, runId);
        var retryId = Guid.NewGuid();

        await Task.WhenAll(SetAsync(firstTab, url, "🔥", true, retryId), SetAsync(secondTab, url, "🔥", true));
        await SetAsync(firstTab, url, "🔥", true, retryId);

        Assert.Equal(("🔥", 1, true), (await ReadReactionsAsync(secondTab, gameId, runId))[0]);
        var unchangedId = Guid.NewGuid();
        await SetAsync(firstTab, url, "🔥", true, unchangedId);

        await Task.WhenAll(SetAsync(firstTab, url, "🔥", false), SetAsync(secondTab, url, "🔥", false));
        await SetAsync(firstTab, url, "🔥", true, unchangedId);

        Assert.Equal(("🔥", 0, false), (await ReadReactionsAsync(firstTab, gameId, runId))[0]);
    }

    [Fact]
    public async Task Hidden_reaction_events_do_not_take_spaces_on_the_feed_page()
    {
        var (gameId, runId) = await CompleteAsync();
        await EnableReactionsAsync();
        var spectator = await _site.SignedInAsync("zritel");
        var baseline = await OkAsync(await spectator.GetAsync(Season("feed?limit=2"), Ct));
        var original = baseline.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("sequence").GetInt64()).ToArray();
        Assert.NotEmpty(original);

        for (var i = 0; i < 3; i++)
        {
            await SetAsync(spectator, Reaction(gameId, runId), "🔥", true);
        }

        var feed = await OkAsync(await spectator.GetAsync(Season("feed?limit=2"), Ct));
        Assert.Equal(original, feed.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("sequence").GetInt64()));
    }

    [Fact]
    public async Task Disabled_reactions_leave_the_review_readable_and_refuse_writes()
    {
        var (gameId, runId) = await CompleteAsync();
        var spectator = await _site.SignedInAsync("zritel");

        var review = await ReadRunAsync(spectator, gameId, runId);
        Assert.Equal(7, review.GetProperty("rating").GetInt32());
        Assert.False(review.TryGetProperty("reviewReactions", out _));
        var response = await spectator.PutAsJsonAsync(Reaction(gameId, runId), new { commandId = Guid.NewGuid(), emoji = "🔥", selected = true }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_run_without_a_review_has_no_reactions_and_an_unknown_or_mismatched_target_is_not_found()
    {
        var (gameId, runId) = await CompleteAsync(withReview: false);
        await EnableReactionsAsync();
        var spectator = await _site.SignedInAsync("zritel");
        var request = new { commandId = Guid.NewGuid(), emoji = "🔥", selected = true };

        Assert.False((await ReadRunAsync(spectator, gameId, runId)).TryGetProperty("reviewReactions", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await spectator.PutAsJsonAsync(Reaction(gameId, runId), request, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await spectator.PutAsJsonAsync(Reaction(gameId, Guid.NewGuid()), request, Ct)).StatusCode);

        // A real review requested through another existing game's address must also be refused.
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, Season($"runs/{runId}/review"), new { commandId = Guid.NewGuid(), rating = 7 });
        var pool = await OkAsync(await spectator.GetAsync("/api/pool", Ct));
        var otherGameId = pool.EnumerateArray().Select(game => game.GetProperty("id").GetGuid()).First(id => id != gameId);
        Assert.Equal(HttpStatusCode.NotFound, (await spectator.PutAsJsonAsync(Reaction(otherGameId, runId), request, Ct)).StatusCode);
        Assert.Equal(("🔥", 0, false), (await ReadReactionsAsync(spectator, gameId, runId))[0]);
    }

    [Fact]
    public async Task A_spectator_cannot_react_to_a_review_of_a_deleted_game()
    {
        var (gameId, runId) = await CompleteAsync();
        await EnableReactionsAsync();
        var admin = await _site.SignedInAsync("admin");
        var spectator = await _site.SignedInAsync("zritel");
        var url = Reaction(gameId, runId);
        await SetAsync(spectator, url, "🔥", true);
        await PostOkAsync(admin, $"/api/admin/pool/{gameId}/delete", new { commandId = Guid.NewGuid(), reason = "Дубль" });

        Assert.Equal(HttpStatusCode.NotFound, (await spectator.GetAsync($"/api/pool/{gameId}/runs", Ct)).StatusCode);
        var response = await spectator.PutAsJsonAsync(url, new { commandId = Guid.NewGuid(), emoji = "🔥", selected = false }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_reaction_needs_a_session_an_antiforgery_token_and_a_valid_payload()
    {
        var (gameId, runId) = await CompleteAsync();
        var url = Reaction(gameId, runId);
        var anonymous = await _site.AnonymousAsync();
        var spectator = await _site.SignedInAsync("zritel");
        var request = new { commandId = Guid.NewGuid(), emoji = "🔥", selected = true };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(url, request, Ct)).StatusCode);
        spectator.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await spectator.PutAsJsonAsync(url, request, Ct)).StatusCode);

        await SiteFactory.RefreshCsrfAsync(spectator);
        var malformed = await spectator.PutAsJsonAsync(url, new { commandId = Guid.NewGuid(), emoji = "🔥", selected = "yes" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        var missingSelection = await spectator.PutAsJsonAsync(url, new { commandId = Guid.NewGuid(), emoji = "🔥" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, missingSelection.StatusCode);
    }

    [Fact]
    public async Task An_emoji_outside_the_season_configuration_never_changes_the_review()
    {
        var (gameId, runId) = await CompleteAsync();
        await EnableReactionsAsync();
        var spectator = await _site.SignedInAsync("zritel");

        var response = await spectator.PutAsJsonAsync(Reaction(gameId, runId), new { commandId = Guid.NewGuid(), emoji = "😂", selected = true }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal([("🔥", 0, false), ("❤️", 0, false)], await ReadReactionsAsync(spectator, gameId, runId));
    }

    private async Task<(Guid GameId, Guid RunId)> CompleteAsync(bool withReview = true)
    {
        var player = await _site.SignedInAsync("vasya");
        await PostOkAsync(player, Season("roll"));
        await PostOkAsync(player, Season("start"));
        await PostOkAsync(player, Season("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        var completed = (await OkAsync(await player.GetAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct))).GetProperty("me").GetProperty("lastCompleted");
        var runId = completed.GetProperty("id").GetGuid();
        if (withReview)
        {
            await PostOkAsync(player, Season($"runs/{runId}/review"), new { commandId = Guid.NewGuid(), rating = 7, text = "Стоит пройти" });
        }

        return (completed.GetProperty("game").GetProperty("id").GetGuid(), runId);
    }

    private Task EnableReactionsAsync()
    {
        var rules = RulesetJson.Default();
        return _site.SendAsync(new ChangeRuleset(rules with
        {
            Features = rules.Features with { Reactions = true },
            Social = rules.Social with { Reactions = ["🔥", "❤️"] },
        }, ExpectedVersion: null));
    }

    private static string Season(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    private static string Reaction(Guid gameId, Guid runId) => $"/api/pool/{gameId}/runs/{runId}/review-reactions";

    private static async Task SetAsync(HttpClient client, string url, string emoji, bool selected, Guid? commandId = null)
    {
        var response = await client.PutAsJsonAsync(url, new { commandId = commandId ?? Guid.NewGuid(), emoji, selected }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<JsonElement> ReadRunAsync(HttpClient client, Guid gameId, Guid runId)
    {
        var runs = await OkAsync(await client.GetAsync($"/api/pool/{gameId}/runs", Ct));
        return Assert.Single(runs.EnumerateArray(), run => run.GetProperty("runId").GetGuid() == runId);
    }

    private static async Task<(string Emoji, int Count, bool Selected)[]> ReadReactionsAsync(HttpClient client, Guid gameId, Guid runId)
    {
        var run = await ReadRunAsync(client, gameId, runId);
        return [.. run.GetProperty("reviewReactions").EnumerateArray()
            .Select(reaction => (reaction.GetProperty("emoji").GetString()!, reaction.GetProperty("count").GetInt32(), reaction.GetProperty("selected").GetBoolean()))];
    }
}

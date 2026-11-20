using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Rulesets;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Api.ApiCalls;
using static GameEvent.Web.Tests.Api.PollSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

public sealed class FinalNominationCandidateApiTests : IAsyncLifetime
{
    private const string ReviewCategory = "bestReview";
    private const string GameCategory = "gameOfSeason";
    private const string FirstReview = "The first nominated review";
    private const string SecondReview = "The second nominated review";
    private readonly SiteFactory _site = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        await _site.SendAsync(NominationRules());
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task The_admin_picker_lists_only_public_credited_games_and_textual_reviews()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync("vasya");
        using var secondPlayer = await _site.SignedInAsync("petya");
        using var anonymous = _site.CreateClient();
        var first = await CompleteAsync(player, FirstReview);
        var second = await CompleteAsync(secondPlayer, text: null);
        await ApproveAsync(admin, first.RunId);

        using var unauthorized = await anonymous.GetAsync(CandidatesUrl(GameCategory), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var forbidden = await player.GetAsync(CandidatesUrl(GameCategory), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var missing = await admin.GetAsync(AdminUrl("nominations/candidates"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var invalid = await admin.GetAsync(CandidatesUrl("ordinary"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var review = Assert.Single(await CandidatesAsync(admin, ReviewCategory));
        Assert.Equal(first.RunId, review.GetProperty("id").GetGuid());
        Assert.Equal(FirstReview, review.GetProperty("detail").GetString());
        Assert.Equal($"/games/{first.GameId:D}", review.GetProperty("href").GetString());
        var game = Assert.Single(await CandidatesAsync(admin, GameCategory));
        Assert.Equal(first.GameId, game.GetProperty("id").GetGuid());

        await ApproveAsync(admin, second.RunId);
        Assert.Equal(new[] { first.GameId, second.GameId }.Order(),
            (await CandidatesAsync(admin, GameCategory)).Select(option => option.GetProperty("id").GetGuid()).Order());
        Assert.Equal(first.RunId, Assert.Single(await CandidatesAsync(admin, ReviewCategory)).GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_deleted_game_cancels_pending_ballots_or_masks_published_candidates(bool published)
    {
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync("vasya");
        using var secondPlayer = await _site.SignedInAsync("petya");
        using var spectator = await _site.SignedInAsync("zritel");
        var first = await CompleteAsync(player, FirstReview);
        var second = await CompleteAsync(secondPlayer, SecondReview);
        await ApproveAsync(admin, first.RunId);
        await ApproveAsync(admin, second.RunId);
        await PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "closing" });
        var reviewPoll = await StartAsync(admin, ReviewCategory, [first.RunId, second.RunId]);
        var gamePoll = await StartAsync(admin, GameCategory, [first.GameId, second.GameId]);

        var current = await CardAsync(spectator, reviewPoll);
        Assert.Equal(FirstReview, Option(current, first.RunId).GetProperty("detail").GetString());
        if (published)
        {
            await VoteOkAsync(player, reviewPoll, first.RunId.ToString("D"));
            await VoteOkAsync(player, gamePoll, first.GameId.ToString("D"));
            _site.Clock.UtcNow = current.GetProperty("closesAt").GetDateTimeOffset();
            await TickAsync(_site);
            await PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "finished" });
        }

        using var visibleFeed = await spectator.GetAsync(Url("feed"), Ct);
        Assert.Contains(FirstReview, (await OkAsync(visibleFeed)).GetRawText(), StringComparison.Ordinal);

        var gameAdmin = $"/api/admin/pool/{first.GameId:D}";
        await PostOkAsync(admin, $"{gameAdmin}/delete", new { commandId = Guid.NewGuid(), reason = "Removed from the public pool" });
        foreach (var (pollId, candidateId) in new[] { (reviewPoll, first.RunId), (gamePoll, first.GameId) })
        {
            var card = await CardAsync(spectator, pollId);
            Assert.Equal(published ? "closed" : "cancelled", card.GetProperty("status").GetString());
            Assert.Equal(published ? candidateId.ToString("D") : null, card.GetProperty("result").GetString());
            var hidden = Option(card, candidateId);
            Assert.True(hidden.GetProperty("unavailable").GetBoolean());
            Assert.Equal("", hidden.GetProperty("label").GetString());
            Assert.False(hidden.TryGetProperty("detail", out _));
            Assert.False(hidden.TryGetProperty("href", out _));
            Assert.False(hidden.TryGetProperty("thumbnailUrl", out _));
        }

        using var feed = await spectator.GetAsync(Url("feed"), Ct);
        var publicFeed = (await OkAsync(feed)).GetRawText();
        Assert.DoesNotContain(FirstReview, publicFeed, StringComparison.Ordinal);
        Assert.Contains(SecondReview, publicFeed, StringComparison.Ordinal);

        Assert.Equal(second.RunId, Assert.Single(await CandidatesAsync(admin, ReviewCategory)).GetProperty("id").GetGuid());
        if (!published)
        {
            await PostOkAsync(admin, $"{gameAdmin}/restore");
            Assert.Equal("cancelled", (await CardAsync(spectator, reviewPoll)).GetProperty("status").GetString());
            Assert.Equal("cancelled", (await CardAsync(spectator, gamePoll)).GetProperty("status").GetString());
            var replacement = await StartAsync(admin, GameCategory, [first.GameId, second.GameId]);
            Assert.Equal("open", (await CardAsync(spectator, replacement)).GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Restoring_a_game_reconciles_ballots_left_open_after_its_deletion()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync("vasya");
        using var secondPlayer = await _site.SignedInAsync("petya");
        using var spectator = await _site.SignedInAsync("zritel");
        var first = await CompleteAsync(player, FirstReview);
        var second = await CompleteAsync(secondPlayer, SecondReview);
        await ApproveAsync(admin, first.RunId);
        await ApproveAsync(admin, second.RunId);
        await PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "closing" });
        var reviewPoll = await StartAsync(admin, ReviewCategory, [first.RunId, second.RunId]);
        var gamePoll = await StartAsync(admin, GameCategory, [first.GameId, second.GameId]);

        // Reproduce the persisted state after deletion commits and before its per-season cancellations run.
        await using (var db = _site.NewDb())
        {
            var game = await db.Games.SingleAsync(item => item.Id == first.GameId, Ct);
            game.IsDeleted = true;
            game.DeletionReason = "Deletion committed before the consumer stopped";
            await db.SaveChangesAsync(Ct);
        }

        foreach (var (pollId, candidateId) in new[] { (reviewPoll, first.RunId), (gamePoll, first.GameId) })
        {
            var card = await CardAsync(spectator, pollId);
            Assert.Equal("open", card.GetProperty("status").GetString());
            Assert.True(Option(card, candidateId).GetProperty("unavailable").GetBoolean());
        }

        var restoreUrl = $"/api/admin/pool/{first.GameId:D}/restore";
        var restore = new { commandId = Guid.NewGuid() };
        await PostOkAsync(admin, restoreUrl, restore);
        foreach (var pollId in new[] { reviewPoll, gamePoll })
        {
            Assert.Equal("cancelled", (await CardAsync(spectator, pollId)).GetProperty("status").GetString());
        }
        using var publicCard = await spectator.GetAsync($"/api/pool/{first.GameId:D}", Ct);
        Assert.Equal(HttpStatusCode.OK, publicCard.StatusCode);

        await PostOkAsync(admin, restoreUrl, restore);
        foreach (var pollId in new[] { reviewPoll, gamePoll })
        {
            Assert.Equal("cancelled", (await CardAsync(spectator, pollId)).GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Editing_a_winning_review_does_not_replace_the_text_in_published_results_or_the_ballot()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync("vasya");
        using var secondPlayer = await _site.SignedInAsync("petya");
        using var spectator = await _site.SignedInAsync("zritel");
        var first = await CompleteAsync(player, FirstReview);
        var second = await CompleteAsync(secondPlayer, SecondReview);
        await ApproveAsync(admin, first.RunId);
        await ApproveAsync(admin, second.RunId);
        await PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "closing" });
        var pollId = Guid.NewGuid();
        using var started = await admin.PostAsJsonAsync(AdminUrl("nominations/polls"),
            new { commandId = Guid.NewGuid(), pollId, category = ReviewCategory, candidateIds = new[] { first.RunId, second.RunId } }, Ct);
        var accepted = await OkAsync(started);
        Assert.DoesNotContain("reviewVersions", accepted.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("textHash", accepted.GetRawText(), StringComparison.Ordinal);
        var opening = await CardAsync(spectator, pollId);
        Assert.Equal(FirstReview, Option(opening, first.RunId).GetProperty("detail").GetString());
        await VoteOkAsync(player, pollId, first.RunId.ToString("D"));
        _site.Clock.UtcNow = opening.GetProperty("closesAt").GetDateTimeOffset();
        await TickAsync(_site);
        await PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "finished" });

        using var original = await spectator.GetAsync(Url("nominations"), Ct);
        var winner = Assert.Single((await OkAsync(original)).GetProperty("voted").EnumerateArray());
        Assert.Equal(FirstReview, winner.GetProperty("winner").GetProperty("detail").GetString());

        const string Replacement = "An unrelated review posted after the ballot";
        await PostOkAsync(player, Url($"runs/{first.RunId:D}/review"),
            new { commandId = Guid.NewGuid(), rating = 8, text = Replacement });
        using var gameRuns = await spectator.GetAsync($"/api/pool/{first.GameId:D}/runs", Ct);
        Assert.Contains(Replacement, (await OkAsync(gameRuns)).GetRawText(), StringComparison.Ordinal);

        using var current = await spectator.GetAsync(Url("nominations"), Ct);
        var recorded = Assert.Single((await OkAsync(current)).GetProperty("voted").EnumerateArray());
        Assert.Equal(pollId, recorded.GetProperty("pollId").GetGuid());
        Assert.True(recorded.GetProperty("hasWinner").GetBoolean());
        Assert.Equal(JsonValueKind.Null, recorded.GetProperty("winner").ValueKind);

        var ballot = await CardAsync(spectator, pollId);
        Assert.Equal("closed", ballot.GetProperty("status").GetString());
        Assert.Equal(first.RunId.ToString("D"), ballot.GetProperty("result").GetString());
        var hidden = Option(ballot, first.RunId);
        Assert.True(hidden.GetProperty("unavailable").GetBoolean());
        Assert.Empty(hidden.GetProperty("label").GetString()!);
        Assert.False(hidden.TryGetProperty("detail", out _));
        Assert.False(hidden.TryGetProperty("href", out _));
        Assert.False(hidden.TryGetProperty("thumbnailUrl", out _));
        Assert.Equal(SecondReview, Option(ballot, second.RunId).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_poll_id_in_use_is_refused_in_a_second_season_without_changing_either_ballot()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync("vasya");
        using var secondPlayer = await _site.SignedInAsync("petya");
        var first = await CompleteAsync(player, FirstReview);
        var second = await CompleteAsync(secondPlayer, SecondReview);
        await ApproveAsync(admin, first.RunId);
        await ApproveAsync(admin, second.RunId);
        await PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "closing" });
        var occupiedPollId = await StartAsync(admin, ReviewCategory, [first.RunId, second.RunId]);

        var secondSeason = await _site.CreateSeasonAsync();
        var configured = await _site.Services.GetRequiredService<CommandBus>().SendAsync(
            new CommandEnvelope(Guid.NewGuid(), secondSeason, NominationRules(), AuthorId: null), Ct);
        Assert.True(configured.IsAccepted, configured.Rejection?.Detail);

        var secondAdmin = $"/api/admin/seasons/{secondSeason:D}";
        foreach (var login in new[] { "vasya", "petya" })
        {
            await PostOkAsync(admin, $"{secondAdmin}/players", new { commandId = Guid.NewGuid(), userId = _site.Users[login] });
        }
        await PostOkAsync(admin, $"{secondAdmin}/status", new { commandId = Guid.NewGuid(), to = "active" });
        var third = await CompleteAsync(player, FirstReview, secondSeason);
        var fourth = await CompleteAsync(secondPlayer, SecondReview, secondSeason);
        await ApproveAsync(admin, third.RunId, secondSeason);
        await ApproveAsync(admin, fourth.RunId, secondSeason);
        await PostOkAsync(admin, $"{secondAdmin}/status", new { commandId = Guid.NewGuid(), to = "closing" });

        var secondBallots = $"{secondAdmin}/nominations/polls";
        var candidateIds = new[] { third.RunId, fourth.RunId };
        using var response = await admin.PostAsJsonAsync(secondBallots,
            new { commandId = Guid.NewGuid(), pollId = occupiedPollId, category = ReviewCategory, candidateIds }, Ct);
        await AssertConflictAsync(response, "poll.nominationAlreadyStarted");

        Assert.Equal("open", (await CardAsync(player, occupiedPollId)).GetProperty("status").GetString());
        var secondPollsPath = $"/api/seasons/{secondSeason:D}/polls";
        using var empty = await player.GetAsync(secondPollsPath, Ct);
        Assert.Empty((await OkAsync(empty)).EnumerateArray());
        var availableId = Guid.NewGuid();
        await PostOkAsync(admin, secondBallots, new { commandId = Guid.NewGuid(), pollId = availableId, category = ReviewCategory, candidateIds });
        using var opened = await player.GetAsync(secondPollsPath, Ct);
        Assert.Equal(availableId, Assert.Single((await OkAsync(opened)).EnumerateArray()).GetProperty("id").GetGuid());
    }

    private static ChangeRuleset NominationRules()
    {
        var rules = RulesetJson.Default();
        return new ChangeRuleset(rules with
        {
            Features = rules.Features with { SeasonNominations = true, Polls = true },
            Nominations = rules.Nominations with { DurationHours = 3, MinCandidates = 2, MaxCandidates = 20 },
        }, ExpectedVersion: null);
    }

    private static async Task<(Guid RunId, Guid GameId)> CompleteAsync(HttpClient actor, string? text, Guid? seasonId = null)
    {
        var season = $"/api/seasons/{seasonId ?? SiteFactory.SeasonId:D}";
        await PostOkAsync(actor, $"{season}/roll");
        await PostOkAsync(actor, $"{season}/start");
        await PostOkAsync(actor, $"{season}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });
        using var response = await actor.GetAsync(season, Ct);
        var run = (await OkAsync(response)).GetProperty("me").GetProperty("lastCompleted");
        var runId = run.GetProperty("id").GetGuid();
        await PostOkAsync(actor, $"{season}/runs/{runId:D}/review", new { commandId = Guid.NewGuid(), rating = 7, text });
        return (runId, run.GetProperty("game").GetProperty("id").GetGuid());
    }

    private static Task ApproveAsync(HttpClient admin, Guid runId, Guid? seasonId = null) =>
        PostOkAsync(admin, $"/api/admin/seasons/{seasonId ?? SiteFactory.SeasonId:D}/runs/{runId:D}/approve",
        new { commandId = Guid.NewGuid(), comment = "Checked for nominations" });

    private static async Task<Guid> StartAsync(HttpClient admin, string category, Guid[] candidateIds)
    {
        var pollId = Guid.NewGuid();
        await PostOkAsync(admin, AdminUrl("nominations/polls"), new { commandId = Guid.NewGuid(), pollId, category, candidateIds });
        return pollId;
    }

    private static string CandidatesUrl(string category) => AdminUrl($"nominations/candidates?category={category}");

    private static async Task<JsonElement[]> CandidatesAsync(HttpClient admin, string category)
    {
        using var response = await admin.GetAsync(CandidatesUrl(category), Ct);
        var view = await OkAsync(response);
        return [.. view.GetProperty("candidates").EnumerateArray()];
    }

    private static async Task<JsonElement> CardAsync(HttpClient viewer, Guid pollId)
    {
        using var response = await viewer.GetAsync(Url($"polls/{pollId:D}"), Ct);
        return await OkAsync(response);
    }

    private static JsonElement Option(JsonElement card, Guid candidateId) =>
        Assert.Single(card.GetProperty("options").EnumerateArray(),
            option => Guid.Parse(option.GetProperty("id").GetString()!) == candidateId);
}

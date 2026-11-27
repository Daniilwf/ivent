using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using NetVips;
using static GameEvent.Web.Tests.Api.ApiCalls;
using static GameEvent.Web.Tests.Api.PollSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>Season-closing ballots over HTTP (SPEC «Голосования», D-927 #13–15).</summary>
public sealed class FinalNominationPollApiTests : IAsyncLifetime
{
    private const int DurationHours = 3;
    private const string GalleryCategory = "funniestMoment";
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Ballots => AdminUrl("nominations/polls");

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        using var admin = await _site.SignedInAsync("admin");
        await ConfigureAsync(admin, SiteFactory.SeasonId);
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task An_admin_cannot_open_the_final_ballot_while_the_season_is_active()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync("vasya");
        var first = await PublishMomentAsync(player, SiteFactory.SeasonId, "First public moment");
        var second = await PublishMomentAsync(player, SiteFactory.SeasonId, "Second public moment");

        using var attempt = await StartAsync(admin, Guid.NewGuid(), [first, second]);

        await AssertStatusAsync(attempt, HttpStatusCode.Conflict);
        Assert.Empty((await StateAsync(_site)).Polls);
    }

    [Fact]
    public async Task A_final_ballot_waits_for_review_of_every_proof_even_with_valid_gallery_candidates()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync("vasya");
        var first = await PublishMomentAsync(player, SiteFactory.SeasonId, "First public moment");
        var second = await PublishMomentAsync(player, SiteFactory.SeasonId, "Second public moment");
        var runId = await CompleteAndSubmitProofAsync(player);
        await SetStatusAsync(admin, "closing");

        using var pending = await StartAsync(admin, Guid.NewGuid(), [first, second]);
        await AssertStatusAsync(pending, HttpStatusCode.Conflict);
        Assert.Empty((await StateAsync(_site)).Polls);

        await PostOkAsync(admin, AdminUrl($"runs/{runId}/approve"), new { commandId = Guid.NewGuid(), comment = "Proof checked" });
        var approvedPollId = Guid.NewGuid();
        using var approved = await StartAsync(admin, approvedPollId, [first, second]);
        await AssertStatusAsync(approved, HttpStatusCode.OK, HttpStatusCode.Created);
        Assert.Equal(approvedPollId, Assert.Single((await StateAsync(_site)).Polls.Keys));

        _site.Clock.UtcNow = (await CardAsync(player, approvedPollId)).GetProperty("closesAt").GetDateTimeOffset();
        await TickAsync(_site);
        var withoutVotes = await CardAsync(player, approvedPollId);
        Assert.Equal("closed", withoutVotes.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, withoutVotes.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task A_pending_irl_proof_alone_blocks_a_final_ballot_until_review()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync("vasya");
        await EventSeason.SetUpAsync(admin, EventSeason.Karaoke);
        var first = await PublishMomentAsync(player, SiteFactory.SeasonId, "First public moment");
        var second = await PublishMomentAsync(player, SiteFactory.SeasonId, "Second public moment");
        await EventSeason.DropAsync(player);
        var effectId = (await EventSeason.SingleEffectAsync(player)).GetProperty("id").GetGuid();
        await PostOkAsync(player, Url($"effects/{effectId}/irl-proof"),
            new { commandId = Guid.NewGuid(), link = "https://youtu.be/karaoke-night" });
        await SetStatusAsync(admin, "closing");

        var closing = await StateAsync(_site);
        Assert.Equal(RunStatus.Dropped, Assert.Single(closing.Runs.Values).Status);
        using var pending = await StartAsync(admin, Guid.NewGuid(), [first, second]);
        await AssertConflictAsync(pending, "season.proofsPending");
        Assert.Empty((await StateAsync(_site)).Polls);

        await PostOkAsync(admin, AdminUrl($"effects/{effectId}/irl-proof/review"),
            new { commandId = Guid.NewGuid(), approved = true });
        var pollId = Guid.NewGuid();
        using var accepted = await StartAsync(admin, pollId, [first, second]);
        await AssertStatusAsync(accepted, HttpStatusCode.OK, HttpStatusCode.Created);
        Assert.Equal("open", (await CardAsync(player, pollId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_final_ballot_uses_the_configured_duration_and_delays_results_until_its_timer()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync("vasya");
        using var otherPlayer = await _site.SignedInAsync("petya");
        using var spectator = await _site.SignedInAsync("zritel");
        var first = await PublishMomentAsync(player, SiteFactory.SeasonId, "First public moment");
        var second = await PublishMomentAsync(otherPlayer, SiteFactory.SeasonId, "Second public moment");
        var ordinary = await StartPollAsync(_site, admin, NominationJson());
        await SetStatusAsync(admin, "closing");
        var startedAt = _site.Clock.UtcNow;
        var pollId = Guid.NewGuid();

        using var start = await StartAsync(admin, pollId, [first, second]);
        await AssertStatusAsync(start, HttpStatusCode.OK, HttpStatusCode.Created);
        var card = await CardAsync(spectator, pollId);
        Assert.Equal("nomination", card.GetProperty("source").GetString());
        Assert.Equal("open", card.GetProperty("status").GetString());
        Assert.Equal(startedAt, card.GetProperty("startedAt").GetDateTimeOffset());
        var closesAt = card.GetProperty("closesAt").GetDateTimeOffset();
        Assert.Equal(startedAt.AddHours(DurationHours), closesAt);
        var options = ReadOptionIds(card);
        Assert.Equal(2, options.Length);
        Assert.Equal(new[] { first, second }.Order(), options.Select(Guid.Parse).Order());

        await VoteOkAsync(player, pollId, options[0]);
        await VoteOkAsync(otherPlayer, pollId, options[1]);
        await VoteOkAsync(player, ordinary, "Финиш");
        using var tooEarly = await admin.PostAsJsonAsync(AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "finished" }, Ct);
        await AssertStatusAsync(tooEarly, HttpStatusCode.Conflict);
        var closing = await StateAsync(_site);
        Assert.Equal(SeasonStatus.Closing, closing.Status);
        Assert.Null(closing.Result);

        _site.Clock.UtcNow = closesAt.AddTicks(-1);
        await TickAsync(_site);
        Assert.Equal("open", (await CardAsync(spectator, pollId)).GetProperty("status").GetString());
        _site.Clock.UtcNow = closesAt;
        await TickAsync(_site);

        var settled = await CardAsync(spectator, pollId);
        Assert.Equal("closed", settled.GetProperty("status").GetString());
        Assert.Contains(settled.GetProperty("result").GetString(), options);
        Assert.Equal([1, 1], settled.GetProperty("counts").EnumerateArray().Select(row => row.GetProperty("votes").GetInt32()));
        Assert.Equal("open", (await CardAsync(spectator, ordinary)).GetProperty("status").GetString());

        await SetStatusAsync(admin, "finished");
        var finished = await StateAsync(_site);
        Assert.Equal(SeasonStatus.Finished, finished.Status);
        Assert.NotNull(finished.Result);
        Assert.Equal(PollStatus.Open, finished.Polls[ordinary].Status);
        using var afterResults = await VoteAsync(otherPlayer, ordinary, "Дроп");
        await AssertConflictAsync(afterResults, "season.closed");
    }

    [Fact]
    public async Task The_ballot_rejects_other_roles_and_forbids_duplicates_and_candidates_outside_configured_bounds()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync("vasya");
        using var anonymous = _site.CreateClient();
        await ConfigureAsync(admin, SiteFactory.SeasonId, minCandidates: 3, maxCandidates: 3);
        var first = await PublishMomentAsync(player, SiteFactory.SeasonId, "First public moment");
        var second = await PublishMomentAsync(player, SiteFactory.SeasonId, "Second public moment");
        var third = await PublishMomentAsync(player, SiteFactory.SeasonId, "Third public moment");
        var fourth = await PublishMomentAsync(player, SiteFactory.SeasonId, "Fourth public moment");
        await SetStatusAsync(admin, "closing");
        var pollId = Guid.NewGuid();

        using var unsigned = await StartAsync(anonymous, pollId, [first, second, third]);
        await AssertStatusAsync(unsigned, HttpStatusCode.Unauthorized);
        using var forbidden = await StartAsync(player, pollId, [first, second, third]);
        await AssertStatusAsync(forbidden, HttpStatusCode.Forbidden);
        using var invalidCategory = await StartAsync(admin, pollId, [first, second, third], category: "ordinary");
        await AssertStatusAsync(invalidCategory, HttpStatusCode.BadRequest);

        foreach (var invalid in new Guid[][] { [first, second], [first, second, third, fourth], [first, second, second] })
        {
            using var response = await StartAsync(admin, pollId, invalid);
            await AssertStatusAsync(response, HttpStatusCode.BadRequest, HttpStatusCode.Conflict);
        }
        Assert.Empty((await StateAsync(_site)).Polls);

        using var withinBounds = await StartAsync(admin, pollId, [first, second, third]);
        await AssertStatusAsync(withinBounds, HttpStatusCode.OK, HttpStatusCode.Created);
        Assert.Equal(3, ReadOptionIds(await CardAsync(player, pollId)).Length);
    }

    [Fact]
    public async Task Turning_off_final_nominations_keeps_ordinary_polling_available()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync("vasya");
        await ConfigureAsync(admin, SiteFactory.SeasonId, nominationsEnabled: false);
        var first = await PublishMomentAsync(player, SiteFactory.SeasonId, "First public moment");
        var second = await PublishMomentAsync(player, SiteFactory.SeasonId, "Second public moment");
        var ordinary = await StartPollAsync(_site, admin, NominationJson());
        await SetStatusAsync(admin, "closing");

        using var disabled = await StartAsync(admin, Guid.NewGuid(), [first, second]);
        await AssertConflictAsync(disabled, "feature.disabled");
        Assert.Equal(ordinary, Assert.Single((await StateAsync(_site)).Polls.Keys));
        await VoteOkAsync(player, ordinary, "Финиш");
        await SetStatusAsync(admin, "finished");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hiding_a_candidate_before_results_cancels_the_ballot_and_allows_a_replacement(bool timerAlreadyClosed)
    {
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync("vasya");
        using var spectator = await _site.SignedInAsync("zritel");
        var first = await PublishMomentAsync(player, SiteFactory.SeasonId, "First public moment");
        var second = await PublishMomentAsync(player, SiteFactory.SeasonId, "Second public moment");
        var spare = await PublishMomentAsync(player, SiteFactory.SeasonId, "Spare public moment");
        var foreignSeason = await _site.CreateSeasonAsync();
        await ConfigureAsync(admin, foreignSeason);
        await PostOkAsync(admin, $"/api/admin/seasons/{foreignSeason}/players", new { commandId = Guid.NewGuid(), userId = _site.Users["vasya"] });
        await SetStatusAsync(admin, "active", foreignSeason);
        var foreign = await PublishMomentAsync(player, foreignSeason, "Another season's moment");
        await SetStatusAsync(admin, "closing");

        using var outOfScope = await StartAsync(admin, Guid.NewGuid(), [first, foreign]);
        await AssertStatusAsync(outOfScope, HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.Conflict);
        Assert.Empty((await StateAsync(_site)).Polls);

        var chosen = Guid.NewGuid();
        using var start = await StartAsync(admin, chosen, [first, second]);
        await AssertStatusAsync(start, HttpStatusCode.OK, HttpStatusCode.Created);
        var opening = await CardAsync(player, chosen);
        var firstOption = ReadOptionIds(opening).Single(id => Guid.Parse(id) == first);
        await VoteOkAsync(player, chosen, firstOption);
        if (timerAlreadyClosed)
        {
            _site.Clock.UtcNow = opening.GetProperty("closesAt").GetDateTimeOffset();
            await TickAsync(_site);
            var closed = await CardAsync(spectator, chosen);
            Assert.Equal("closed", closed.GetProperty("status").GetString());
            Assert.Equal(firstOption, closed.GetProperty("result").GetString());
        }

        await PostOkAsync(admin, AdminUrl($"gallery/{first}/hide"));
        var cancelled = await CardAsync(spectator, chosen);
        Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, cancelled.GetProperty("counts").ValueKind);
        Assert.Equal(JsonValueKind.Null, cancelled.GetProperty("result").ValueKind);

        using var cannotReuseHidden = await StartAsync(admin, Guid.NewGuid(), [first, spare]);
        await AssertStatusAsync(cannotReuseHidden, HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.Conflict);
        var replacement = Guid.NewGuid();
        using var restart = await StartAsync(admin, replacement, [second, spare]);
        await AssertStatusAsync(restart, HttpStatusCode.OK, HttpStatusCode.Created);
        Assert.Equal("open", (await CardAsync(spectator, replacement)).GetProperty("status").GetString());
    }

    private static Task<HttpResponseMessage> StartAsync(HttpClient actor, Guid pollId, Guid[] candidateIds, string category = GalleryCategory) =>
        actor.PostAsJsonAsync(Ballots, new { commandId = Guid.NewGuid(), pollId, category, candidateIds }, Ct);

    private static Task SetStatusAsync(HttpClient admin, string to, Guid? seasonId = null) =>
        PostOkAsync(admin, $"/api/admin/seasons/{seasonId ?? SiteFactory.SeasonId}/status", new { commandId = Guid.NewGuid(), to });

    private static async Task ConfigureAsync(
        HttpClient admin, Guid seasonId, int minCandidates = 2, int maxCandidates = 20, bool nominationsEnabled = true)
    {
        using var current = await admin.GetAsync($"/api/seasons/{seasonId}/rules", Ct);
        var view = await OkAsync(current);
        var rules = JsonNode.Parse(view.GetProperty("ruleset").GetRawText())!;
        var features = rules["features"]!;
        features["polls"] = true;
        features["gallery"] = true;
        features["seasonNominations"] = nominationsEnabled;
        var nominations = rules["nominations"]!;
        nominations["durationHours"] = DurationHours;
        nominations["minCandidates"] = minCandidates;
        nominations["maxCandidates"] = maxCandidates;
        using var updated = await admin.PutAsJsonAsync($"/api/admin/seasons/{seasonId}/rules", new
        {
            commandId = Guid.NewGuid(),
            expectedVersion = view.GetProperty("version").GetInt32(),
            ruleset = rules,
        }, Ct);
        await OkAsync(updated);
    }

    private static async Task<Guid> CompleteAndSubmitProofAsync(HttpClient player)
    {
        await PostOkAsync(player, Url("roll"));
        await PostOkAsync(player, Url("start"));
        await PostOkAsync(player, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        using var current = await player.GetAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct);
        var season = await OkAsync(current);
        var runId = season.GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetGuid();
        await PostOkAsync(player, Url($"runs/{runId}/proof"), new
        {
            commandId = Guid.NewGuid(),
            links = new[] { "https://example.com/credits.png" },
        });
        return runId;
    }

    private static async Task<Guid> PublishMomentAsync(HttpClient player, Guid seasonId, string caption)
    {
        var itemId = Guid.NewGuid();
        using var image = Image.Black(32, 24, bands: 3);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Guid.NewGuid().ToString()), "commandId" },
            { new StringContent(itemId.ToString()), "itemId" },
            { new StringContent(caption), "caption" },
        };
        var file = new ByteArrayContent(image.PngsaveBuffer());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "moment.png");
        using var published = await player.PostAsync($"/api/seasons/{seasonId}/gallery", form, Ct);
        await AssertStatusAsync(published, HttpStatusCode.Created);
        return itemId;
    }

    private static string[] ReadOptionIds(JsonElement ballot) =>
        [.. ballot.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("id").GetString()!)];

    private static async Task AssertStatusAsync(HttpResponseMessage response, params HttpStatusCode[] expected)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(expected.Contains(response.StatusCode), $"Expected {string.Join(" or ", expected)}; got {response.StatusCode}: {body}");
    }
}

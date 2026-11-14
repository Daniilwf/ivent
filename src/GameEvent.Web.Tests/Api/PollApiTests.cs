using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static GameEvent.Web.Tests.Api.PollSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Polls over HTTP (task 5.19; SPEC «Голосования», D-512…D-516, D-541, D-720, D-830): the list and the card, the vote, the
/// admin's start and early close. Every endpoint: success, another role or another player (401, 403 or the engine's
/// refusal), invalid input (400). EV40: two tabs of one user give one vote, by the queue and by the database's key.
/// Written by <c>test-author</c> before the endpoints: they fail on the missing routes.
/// </summary>
public sealed class PollApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        await PollsOnAsync(_site);
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- The admin starts a poll ----

    [Fact]
    public async Task The_admin_starts_a_poll_and_everyone_sees_it_in_the_list()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(AdminUrl("polls"), new { commandId = Guid.NewGuid(), poll = NominationJson() }, Ct);

        Assert.Equal(["poll-started"], await TypesAsync(response));
        var poll = Assert.Single((await StateAsync(_site)).Polls.Values);
        foreach (var login in new[] { "vasya", "zritel", "masha", "admin" })
        {
            var client = await _site.SignedInAsync(login);
            var list = await ApiCalls.OkAsync(await client.GetAsync(Url("polls"), Ct));
            var item = Assert.Single(list.EnumerateArray());
            Assert.Equal(poll.PollId, item.GetProperty("id").GetGuid());
            Assert.Equal("Лучший момент недели?", item.GetProperty("question").GetString());
            Assert.Equal("open", item.GetProperty("status").GetString());
            Assert.Equal(poll.ClosesAt, item.GetProperty("closesAt").GetDateTimeOffset());
        }
    }

    [Fact]
    public async Task The_card_shows_the_snapshot_of_options_and_my_own_vote_only_to_me()
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        var vasya = await _site.SignedInAsync("vasya");
        await VoteOkAsync(vasya, pollId, "Дроп");

        var mine = await CardAsync(vasya, pollId);
        var petyas = await CardAsync(await _site.SignedInAsync("petya"), pollId);

        Assert.Equal(pollId, mine.GetProperty("id").GetGuid());
        Assert.Equal(["Финиш", "Дроп", "Реролл"], mine.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("id").GetString()));
        Assert.Equal(["Финиш", "Дроп", "Реролл"], mine.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("label").GetString()));
        Assert.Equal("players", mine.GetProperty("voters").GetString());
        Assert.False(mine.GetProperty("anonymous").GetBoolean());
        Assert.Equal("Дроп", mine.GetProperty("myVote").GetString());
        Assert.Equal(JsonValueKind.Null, petyas.GetProperty("myVote").ValueKind);
    }

    [Fact]
    public async Task Without_live_counts_the_counts_are_hidden_until_the_close_and_then_shown_with_the_result()
    {
        // D-541: counts by option only in poll-closed unless the poll shows live counts
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        await VoteOkAsync(await _site.SignedInAsync("vasya"), pollId, "Дроп");
        await VoteOkAsync(await _site.SignedInAsync("petya"), pollId, "Дроп");

        var open = await CardAsync(admin, pollId);
        Assert.Equal(JsonValueKind.Null, open.GetProperty("counts").ValueKind);
        Assert.Equal(JsonValueKind.Null, open.GetProperty("result").ValueKind);

        var close = await admin.PostAsJsonAsync(AdminUrl($"polls/{pollId}/close"), new { commandId = Guid.NewGuid() }, Ct);

        Assert.Contains("poll-closed", await TypesAsync(close));
        var closed = await CardAsync(await _site.SignedInAsync("zritel"), pollId);
        Assert.Equal("closed", closed.GetProperty("status").GetString());
        Assert.Equal("Дроп", closed.GetProperty("result").GetString());
        var counts = closed.GetProperty("counts").EnumerateArray().ToDictionary(c => c.GetProperty("optionId").GetString()!, c => c.GetProperty("votes").GetInt32());
        Assert.Equal(new Dictionary<string, int> { ["Финиш"] = 0, ["Дроп"] = 2, ["Реролл"] = 0 }, counts);
    }

    [Fact]
    public async Task With_live_counts_the_counts_are_shown_while_the_poll_is_open()
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson(showLiveCounts: true));
        await VoteOkAsync(await _site.SignedInAsync("vasya"), pollId, "Реролл");

        var card = await CardAsync(await _site.SignedInAsync("petya"), pollId);

        var counts = card.GetProperty("counts").EnumerateArray().ToDictionary(c => c.GetProperty("optionId").GetString()!, c => c.GetProperty("votes").GetInt32());
        Assert.Equal(1, counts["Реролл"]);
        Assert.Equal(0, counts["Финиш"]);
        Assert.Equal(JsonValueKind.Null, card.GetProperty("result").ValueKind);
    }

    [Theory]
    [InlineData("""{"commandId":"{id}"}""")]
    [InlineData("""{"poll":{"question":"Q?","options":{"list":["a","b"]},"voters":"players","closesInHours":24}}""")]
    [InlineData("""{"commandId":"{id}","poll":{"question":"Q?","options":{"list":["a","b"]},"voters":"aliens","closesInHours":24}}""")]
    [InlineData("""{"commandId":"{id}","poll":{"options":{"list":["a","b"]},"voters":"players","closesInHours":24}}""")]
    [InlineData("""{"commandId":"{id}","poll":"Лучший момент?"}""")]
    public async Task Starting_a_poll_with_invalid_input_is_a_bad_request(string json)
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await PostJsonAsync(admin, AdminUrl("polls"), json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await StateAsync(_site)).Polls);
    }

    [Fact]
    public async Task A_poll_the_engine_refuses_is_a_conflict_with_its_code()
    {
        var admin = await _site.SignedInAsync("admin");

        // The site's pool has one category: a poll over the categories would offer one option (D-836: a list of one is
        // refused by the content's rule before, as content.invalid)
        await AssertConflictAsync(
            await admin.PostAsJsonAsync(
                AdminUrl("polls"),
                new { commandId = Guid.NewGuid(), poll = new { question = "Q?", options = new { from = "categories" }, voters = "players", closesInHours = 24 } },
                Ct),
            "poll.noOptions");
        await AssertConflictAsync(
            await admin.PostAsJsonAsync(AdminUrl("polls"), new { commandId = Guid.NewGuid(), poll = NominationJson(options: ["Один"]) }, Ct), "content.invalid");
        await AssertConflictAsync(
            await admin.PostAsJsonAsync(
                AdminUrl("polls"),
                new { commandId = Guid.NewGuid(), poll = new { question = "Q?", options = new { list = new[] { "a", "b" } }, voters = "players", closesInHours = 0 } },
                Ct),
            "content.invalid");
    }

    [Fact]
    public async Task With_polls_off_the_list_the_start_and_the_vote_are_refused()
    {
        await RulesAsync(_site, Flags(f => f with { Polls = false }));
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");

        await AssertConflictAsync(await vasya.GetAsync(Url("polls"), Ct), "feature.disabled");
        await AssertConflictAsync(await admin.PostAsJsonAsync(AdminUrl("polls"), new { commandId = Guid.NewGuid(), poll = NominationJson() }, Ct), "feature.disabled");
        await AssertConflictAsync(await VoteAsync(vasya, Guid.NewGuid(), "Финиш"), "feature.disabled");
    }

    // ---- The admin closes a poll early ----

    [Fact]
    public async Task A_poll_closed_once_cannot_be_closed_again_and_an_unknown_one_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        await ApiCalls.PostOkAsync(admin, AdminUrl($"polls/{pollId}/close"));

        await AssertConflictAsync(await admin.PostAsJsonAsync(AdminUrl($"polls/{pollId}/close"), new { commandId = Guid.NewGuid() }, Ct), "poll.closed");
        await AssertConflictAsync(await admin.PostAsJsonAsync(AdminUrl($"polls/{Guid.NewGuid()}/close"), new { commandId = Guid.NewGuid() }, Ct), "poll.unknown");
        await AssertConflictAsync(await VoteAsync(await _site.SignedInAsync("vasya"), pollId, "Финиш"), "poll.closed");
    }

    [Fact]
    public async Task Closing_without_a_command_id_is_a_bad_request()
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());

        var response = await PostJsonAsync(admin, AdminUrl($"polls/{pollId}/close"), "{}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Engine.Polls.PollStatus.Open, (await StateAsync(_site)).Polls[pollId].Status);
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    [InlineData("masha")]
    public async Task Players_and_spectators_cannot_start_or_close_a_poll(string login)
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        var client = await _site.SignedInAsync(login);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(AdminUrl("polls"), new { commandId = Guid.NewGuid(), poll = NominationJson() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(AdminUrl($"polls/{pollId}/close"), new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Single((await StateAsync(_site)).Polls);
        Assert.Equal(Engine.Polls.PollStatus.Open, (await StateAsync(_site)).Polls[pollId].Status);
    }

    // ---- The vote ----

    [Fact]
    public async Task A_vote_is_one_command_and_its_repeat_by_the_same_id_is_a_duplicate()
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        var vasya = await _site.SignedInAsync("vasya");
        var commandId = Guid.NewGuid();

        var first = await VoteAsync(vasya, pollId, "Финиш", commandId);
        var again = await VoteAsync(vasya, pollId, "Финиш", commandId);

        Assert.Equal(["vote-cast"], await TypesAsync(first));
        Assert.True((await ApiCalls.OkAsync(again)).GetProperty("duplicate").GetBoolean());
        Assert.Equal(1, await CountAsync(_site, "vote-cast"));
    }

    [Fact]
    public async Task A_changed_vote_replaces_the_first_and_the_card_shows_the_last()
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        var vasya = await _site.SignedInAsync("vasya");
        await VoteOkAsync(vasya, pollId, "Финиш");

        var change = await VoteAsync(vasya, pollId, "Реролл");

        Assert.Equal(["vote-changed"], await TypesAsync(change));
        Assert.Equal("Реролл", (await CardAsync(vasya, pollId)).GetProperty("myVote").GetString());
        var vote = Assert.Single((await StateAsync(_site)).Polls[pollId].Votes);
        Assert.Equal((_site.Users["vasya"], "Реролл"), (vote.UserId, vote.OptionId));
    }

    [Fact]
    public async Task EV40_two_tabs_sending_the_same_vote_at_once_give_one_vote_and_one_row()
    {
        // Н-22: the second tab is refused by «the same vote again», nothing is written; the database's key (PollId, UserId)
        // is the second line (EventsProjectionTests.A_second_vote_row_of_the_same_user_in_a_poll_is_refused_by_the_database)
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        var tab1 = await _site.SignedInAsync("vasya");
        var tab2 = await _site.SignedInAsync("vasya");

        var answers = await Task.WhenAll(VoteAsync(tab1, pollId, "Финиш"), VoteAsync(tab2, pollId, "Финиш"));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        await AssertConflictAsync(Assert.Single(answers, a => a.StatusCode != HttpStatusCode.OK), "poll.sameVote");
        Assert.Equal(1, await CountAsync(_site, "vote-cast"));
        Assert.Equal(0, await CountAsync(_site, "vote-changed"));
        await using var db = _site.NewDb();
        Assert.Equal(1L, await ScalarAsync(db, "SELECT count(*) FROM Vote"));
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task EV40_two_tabs_with_different_options_leave_one_vote_in_force()
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        var tab1 = await _site.SignedInAsync("vasya");
        var tab2 = await _site.SignedInAsync("vasya");

        var answers = await Task.WhenAll(VoteAsync(tab1, pollId, "Финиш"), VoteAsync(tab2, pollId, "Дроп"));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
        var vote = Assert.Single((await StateAsync(_site)).Polls[pollId].Votes);
        Assert.Equal(_site.Users["vasya"], vote.UserId);
        await using var db = _site.NewDb();
        Assert.Equal(1L, await ScalarAsync(db, "SELECT count(*) FROM Vote"));
        Assert.Equal(vote.OptionId, await ScalarAsync(db, "SELECT OptionId FROM Vote"));
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task The_voter_is_the_signed_in_user_whatever_the_body_says()
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(
            Url($"polls/{pollId}/vote"),
            new { commandId = Guid.NewGuid(), optionId = "Финиш", userId = _site.Users["petya"], playerId = _site.Players["petya"] },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var vote = Assert.Single((await StateAsync(_site)).Polls[pollId].Votes);
        Assert.Equal(_site.Users["vasya"], vote.UserId);

        // Петя has not voted: his own vote is a first one
        Assert.Equal(["vote-cast"], await TypesAsync(await VoteAsync(await _site.SignedInAsync("petya"), pollId, "Дроп")));
    }

    [Fact]
    public async Task A_player_of_another_season_and_a_spectator_do_not_vote_in_a_players_poll()
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());

        await AssertConflictAsync(await VoteAsync(await _site.SignedInAsync("masha"), pollId, "Финиш"), "poll.notVoter");
        await AssertConflictAsync(await VoteAsync(await _site.SignedInAsync("zritel"), pollId, "Финиш"), "poll.notVoter");
        Assert.Empty((await StateAsync(_site)).Polls[pollId].Votes);
    }

    [Fact]
    public async Task A_spectator_votes_in_a_poll_for_everyone_only_when_the_rules_allow_it()
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson(voters: "everyone"));
        var zritel = await _site.SignedInAsync("zritel");

        await AssertConflictAsync(await VoteAsync(zritel, pollId, "Финиш"), "poll.notVoter");

        await RulesAsync(_site, r => r with { Social = r.Social with { SpectatorsCanVote = true } });

        await VoteOkAsync(zritel, pollId, "Финиш");
        Assert.Equal(_site.Users["zritel"], Assert.Single((await StateAsync(_site)).Polls[pollId].Votes).UserId);
        Assert.Equal("Финиш", (await CardAsync(zritel, pollId)).GetProperty("myVote").GetString());
    }

    [Theory]
    [InlineData("""{"optionId":"Финиш"}""")]
    [InlineData("""{"commandId":"{id}"}""")]
    [InlineData("""{"commandId":"{id}","optionId":""}""")]
    [InlineData("""{"commandId":"{id}","optionId":5}""")]
    [InlineData("""{"commandId":"00000000-0000-0000-0000-000000000000","optionId":"Финиш"}""")]
    public async Task A_vote_with_invalid_input_is_a_bad_request(string json)
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());

        var response = await PostJsonAsync(await _site.SignedInAsync("vasya"), Url($"polls/{pollId}/vote"), json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await StateAsync(_site)).Polls[pollId].Votes);
    }

    [Fact]
    public async Task A_vote_for_an_option_not_in_the_snapshot_or_in_an_unknown_poll_is_a_conflict()
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        var vasya = await _site.SignedInAsync("vasya");

        await AssertConflictAsync(await VoteAsync(vasya, pollId, "Ничья"), "poll.unknownOption");
        await AssertConflictAsync(await VoteAsync(vasya, Guid.NewGuid(), "Финиш"), "poll.unknown");
    }

    [Fact]
    public async Task An_option_longer_than_any_option_can_be_is_a_bad_request()
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());

        var response = await VoteAsync(await _site.SignedInAsync("vasya"), pollId, new string('я', 1000));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Roles of the reads ----

    [Fact]
    public async Task The_anonymous_get_401_and_an_unknown_season_or_poll_is_not_found()
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        var anonymous = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url("polls"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url($"polls/{pollId}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await VoteAsync(anonymous, pollId, "Финиш")).StatusCode);
        var vasya = await _site.SignedInAsync("vasya");
        Assert.Equal(HttpStatusCode.NotFound, (await vasya.GetAsync(Url($"polls/{Guid.NewGuid()}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await vasya.GetAsync($"/api/seasons/{Guid.NewGuid()}/polls", Ct)).StatusCode);
    }

    // ---- SignalR ----

    [Fact]
    public async Task A_watcher_learns_about_a_started_and_a_closed_poll()
    {
        var (connection, updates) = await WatchAsync(_site, "petya");
        await using var hub = connection;
        var admin = await _site.SignedInAsync("admin");

        var pollId = await StartPollAsync(_site, admin, NominationJson());
        await ApiCalls.PostOkAsync(admin, AdminUrl($"polls/{pollId}/close"));

        await WaitForAsync(updates, "poll-started");
        await WaitForAsync(updates, "poll-closed");
    }
}

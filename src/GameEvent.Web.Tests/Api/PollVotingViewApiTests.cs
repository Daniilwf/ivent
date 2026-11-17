using System.Net;
using System.Text.Json;
using GameEvent.Engine.Players;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using static GameEvent.Web.Tests.Api.PollSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// SPEC «Голосования», «Финишировавший первым», D-513, D-516, D-541: the HTTP display availability is the current
/// session's ability to cast a new or different vote. It must agree with the real POST, preserve the poll snapshot
/// and anonymity, and use current change-vote / spectator rules. No rule implementation is read by these tests.
/// </summary>
public sealed class PollVotingViewApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        await PollsOnAsync(_site);
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Theory]
    [InlineData("vasya", "players", false, true)]
    [InlineData("vasya", "everyone", false, true)]
    [InlineData("petya", "players", true, true)]
    [InlineData("zritel", "players", true, false)]
    [InlineData("masha", "players", true, false)]
    [InlineData("admin", "players", true, false)]
    [InlineData("zritel", "everyone", false, false)]
    [InlineData("zritel", "everyone", true, true)]
    [InlineData("masha", "everyone", true, true)]
    [InlineData("admin", "everyone", false, false)]
    [InlineData("admin", "everyone", true, true)]
    public async Task The_list_and_card_describe_the_current_voter_and_match_the_post(
        string login, string voters, bool spectatorsCanVote, bool canVote)
    {
        var admin = await _site.SignedInAsync("admin");
        var current = await ApiCalls.OkAsync(await admin.GetAsync(Url("rules"), Ct));
        if (current.GetProperty("ruleset").GetProperty("social").GetProperty("spectatorsCanVote").GetBoolean() != spectatorsCanVote)
        {
            await RulesAsync(_site, r => r with { Social = r.Social with { SpectatorsCanVote = spectatorsCanVote } });
        }
        var pollId = await StartPollAsync(_site, admin, NominationJson(voters: voters));
        var client = await _site.SignedInAsync(login);

        await AssertVotingAsync(client, pollId, canVote, canVote ? null : "poll.notVoter");
        await AssertPostMatchesAsync(client, pollId, "Финиш", canVote ? null : "poll.notVoter");

        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task The_current_change_rule_controls_both_metadata_and_a_changed_vote_not_the_poll_start_rules()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await ChangeVoteRuleAsync(false);
        var pollId = await StartPollAsync(_site, admin, NominationJson());

        // Forbidding a change never forbids a user's first vote.
        await AssertVotingAsync(vasya, pollId, true, null, allowChangeVote: false);
        await VoteOkAsync(vasya, pollId, "Финиш");
        await AssertVotingAsync(vasya, pollId, false, "poll.alreadyVoted", allowChangeVote: false);
        await AssertPostMatchesAsync(vasya, pollId, "Дроп", "poll.alreadyVoted");

        await ChangeVoteRuleAsync(true);
        await AssertVotingAsync(vasya, pollId, true, null);
        Assert.Equal(["vote-changed"], await TypesAsync(await VoteAsync(vasya, pollId, "Дроп")));

        await ChangeVoteRuleAsync(false);
        var card = await AssertVotingAsync(vasya, pollId, false, "poll.alreadyVoted", allowChangeVote: false);
        Assert.Equal("Дроп", card.GetProperty("myVote").GetString());
        await AssertPostMatchesAsync(vasya, pollId, "Реролл", "poll.alreadyVoted");
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_current_spectator_permission_change_affects_an_existing_everyone_poll()
    {
        var admin = await _site.SignedInAsync("admin");
        var spectator = await _site.SignedInAsync("zritel");
        var pollId = await StartPollAsync(_site, admin, NominationJson(voters: "everyone"));
        await AssertVotingAsync(spectator, pollId, false, "poll.notVoter");
        await AssertPostMatchesAsync(spectator, pollId, "Финиш", "poll.notVoter");

        await RulesAsync(_site, r => r with { Social = r.Social with { SpectatorsCanVote = true } });
        await AssertVotingAsync(spectator, pollId, true, null);
        await AssertPostMatchesAsync(spectator, pollId, "Финиш");

        await RulesAsync(_site, r => r with { Social = r.Social with { SpectatorsCanVote = false } });
        var card = await AssertVotingAsync(spectator, pollId, false, "poll.notVoter");
        Assert.Equal("Финиш", card.GetProperty("myVote").GetString());
        await AssertPostMatchesAsync(spectator, pollId, "Дроп", "poll.notVoter");
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task Can_vote_means_a_new_or_different_option_while_the_same_option_is_still_refused()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        await VoteOkAsync(vasya, pollId, "Финиш");

        var card = await AssertVotingAsync(vasya, pollId, true, null);
        Assert.Equal("Финиш", card.GetProperty("myVote").GetString());
        await AssertPostMatchesAsync(vasya, pollId, "Финиш", "poll.sameVote");
        await AssertPostMatchesAsync(vasya, pollId, "Дроп");
        await AssertIntactAsync(_site);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_first_finisher_may_vote_in_an_admin_nomination_but_not_an_admin_poll_with_a_result_effect(
        bool approved, bool withEffect)
    {
        var admin = await _site.SignedInAsync("admin");
        await FinishAsync("vasya", approved: approved);
        var pollId = await StartPollAsync(_site, admin, withEffect ? EffectPollJson() : NominationJson());
        Assert.Equal(PollSource.Admin, (await StateAsync(_site)).Polls[pollId].Source);
        var first = await _site.SignedInAsync("vasya");

        await AssertVotingAsync(first, pollId, !withEffect, withEffect ? "poll.frozenVoter" : null);
        await AssertPostMatchesAsync(first, pollId, "Финиш", withEffect ? "poll.frozenVoter" : null);
        await AssertVotingAsync(await _site.SignedInAsync("petya"), pollId, true, null);
        await AssertIntactAsync(_site);
    }

    [Theory]
    [InlineData("event", false)]
    [InlineData("event", true)]
    [InlineData("globalEvent", false)]
    [InlineData("globalEvent", true)]
    public async Task An_event_source_has_an_effect_even_without_an_admin_on_result_snapshot(string source, bool approved)
    {
        var admin = await _site.SignedInAsync("admin");
        await EventSeason.SetUpAsync(admin, PollEventJson(source == "globalEvent" ? "all" : "self"));
        if (source == "globalEvent")
        {
            await RulesAsync(_site, Flags(f => f with { GlobalEvents = true }));
            await ApiCalls.PostOkAsync(admin, AdminUrl("global-events"), new { commandId = Guid.NewGuid(), objectId = "asking-event", durationHours = 48 });
        }
        else
        {
            await EventSeason.DropAsync(await _site.SignedInAsync("vasya"));
        }
        var poll = Assert.Single((await StateAsync(_site)).Polls.Values);
        Assert.Null(poll.OnResult);
        Assert.Equal(source == "event" ? PollSource.Event : PollSource.GlobalEvent, poll.Source);
        await FinishAsync("petya", approved: approved, beforeFinish: "c40");
        var first = await _site.SignedInAsync("petya");

        var card = await AssertVotingAsync(first, poll.PollId, false, "poll.frozenVoter");
        Assert.Equal(source, card.GetProperty("source").GetString());
        await AssertPostMatchesAsync(first, poll.PollId, "Финиш", "poll.frozenVoter");
        await AssertVotingAsync(await _site.SignedInAsync("vasya"), poll.PollId, true, null);
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_later_finisher_still_votes_in_a_poll_with_a_result_effect()
    {
        var admin = await _site.SignedInAsync("admin");
        await FinishAsync("vasya");
        await FinishAsync("petya");
        var pollId = await StartPollAsync(_site, admin, EffectPollJson());
        var later = await _site.SignedInAsync("petya");

        await AssertVotingAsync(later, pollId, true, null);
        await AssertPostMatchesAsync(later, pollId, "Финиш");
        await AssertIntactAsync(_site);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task The_polls_own_deadline_is_checked_before_the_timer_without_mutating_its_open_status(long offsetTicks, bool canVote)
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        _site.Clock.UtcNow = (await StateAsync(_site)).Polls[pollId].ClosesAt.AddTicks(offsetTicks);

        var card = await AssertVotingAsync(vasya, pollId, canVote, canVote ? null : "poll.closed");
        Assert.Equal("open", card.GetProperty("status").GetString());
        Assert.Equal(PollStatus.Open, (await StateAsync(_site)).Polls[pollId].Status);
        await AssertPostMatchesAsync(vasya, pollId, "Финиш", canVote ? null : "poll.closed");
        await AssertIntactAsync(_site);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_nomination_still_takes_votes_past_the_season_deadline_or_during_closing(bool moveToClosing)
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        await _site.SendAsync(new SetSeasonDeadline(_site.Clock.UtcNow.AddHours(1)));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(2);
        if (moveToClosing)
        {
            await ApiCalls.PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "closing" });
        }
        Assert.Equal(moveToClosing ? SeasonStatus.Closing : SeasonStatus.Active, (await StateAsync(_site)).Status);

        await AssertVotingAsync(vasya, pollId, true, null);
        await AssertPostMatchesAsync(vasya, pollId, "Финиш");
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_closed_poll_has_no_available_vote_and_keeps_its_public_counts()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        await VoteOkAsync(vasya, pollId, "Финиш");
        await ApiCalls.PostOkAsync(admin, AdminUrl($"polls/{pollId}/close"));

        var card = await AssertVotingAsync(vasya, pollId, false, "poll.closed");
        Assert.Equal("closed", card.GetProperty("status").GetString());
        Assert.Equal("Финиш", card.GetProperty("result").GetString());
        Assert.Equal(1, card.GetProperty("counts").EnumerateArray().Sum(c => c.GetProperty("votes").GetInt32()));
        await AssertPostMatchesAsync(vasya, pollId, "Дроп", "poll.closed");
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_deadline_cancelled_effect_poll_has_no_available_vote_counts_or_result()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        var pollId = await StartPollAsync(_site, admin, EffectPollJson());
        await VoteOkAsync(vasya, pollId, "Финиш");
        await _site.SendAsync(new SetSeasonDeadline(_site.Clock.UtcNow.AddHours(1)));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(2);
        await TickAsync(_site);

        var card = await AssertVotingAsync(vasya, pollId, false, "poll.closed");
        Assert.Equal("cancelled", card.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, card.GetProperty("counts").ValueKind);
        Assert.Equal(JsonValueKind.Null, card.GetProperty("result").ValueKind);
        await AssertPostMatchesAsync(vasya, pollId, "Дроп", "poll.closed");
        await AssertIntactAsync(_site);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_finished_or_archived_season_has_no_available_vote(bool archived)
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        foreach (var status in archived ? new[] { "closing", "finished", "archived" } : new[] { "closing", "finished" })
        {
            await ApiCalls.PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to = status });
        }

        await AssertVotingAsync(vasya, pollId, false, "season.closed");
        await AssertPostMatchesAsync(vasya, pollId, "Финиш", "season.closed");
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task Anonymous_counts_and_my_vote_remain_private_when_availability_differs_between_sessions()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        await ChangeVoteRuleAsync(false);
        var pollId = await StartPollAsync(_site, admin, NominationJson(anonymous: true));
        await VoteOkAsync(vasya, pollId, "Финиш");

        var mine = await AssertVotingAsync(vasya, pollId, false, "poll.alreadyVoted", allowChangeVote: false);
        var other = await AssertVotingAsync(petya, pollId, true, null, allowChangeVote: false);
        Assert.Equal("Финиш", mine.GetProperty("myVote").GetString());
        Assert.Equal(JsonValueKind.Null, other.GetProperty("myVote").ValueKind);
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("counts").ValueKind);
        Assert.Equal(JsonValueKind.Null, other.GetProperty("counts").ValueKind);
        Assert.DoesNotContain(_site.Users["vasya"].ToString(), other.GetRawText(), StringComparison.OrdinalIgnoreCase);

        await ApiCalls.PostOkAsync(admin, AdminUrl($"polls/{pollId}/close"));
        var closed = await AssertVotingAsync(petya, pollId, false, "poll.closed", allowChangeVote: false);
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("myVote").ValueKind);
        Assert.Equal(1, closed.GetProperty("counts").EnumerateArray().Sum(c => c.GetProperty("votes").GetInt32()));
        Assert.DoesNotContain(_site.Users["vasya"].ToString(), closed.GetRawText(), StringComparison.OrdinalIgnoreCase);
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task Disabling_polls_keeps_the_read_and_vote_guards_and_enabling_restores_current_metadata()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        await RulesAsync(_site, Flags(f => f with { Polls = false }));

        await AssertConflictAsync(await vasya.GetAsync(Url("polls"), Ct), "feature.disabled");
        await AssertConflictAsync(await vasya.GetAsync(Url($"polls/{pollId}"), Ct), "feature.disabled");
        await AssertPostMatchesAsync(vasya, pollId, "Финиш", "feature.disabled");

        await ChangeVoteRuleAsync(false);
        await RulesAsync(_site, Flags(f => f with { Polls = true }));
        await AssertVotingAsync(vasya, pollId, true, null, allowChangeVote: false);
        await AssertPostMatchesAsync(vasya, pollId, "Финиш");
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task An_unsigned_reader_gets_no_poll_or_voting_metadata()
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        var anonymous = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url("polls"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url($"polls/{pollId}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await VoteAsync(anonymous, pollId, "Финиш")).StatusCode);
    }

    private async Task<JsonElement> AssertVotingAsync(HttpClient client, Guid pollId, bool canVote, string? reason, bool allowChangeVote = true)
    {
        var before = (await StateAsync(_site)).Polls[pollId];
        var card = await CardAsync(client, pollId);
        var list = await ApiCalls.OkAsync(await client.GetAsync(Url("polls"), Ct));
        var row = Assert.Single(list.EnumerateArray(), item => item.GetProperty("id").GetGuid() == pollId);
        foreach (var view in new[] { card, row })
        {
            var voting = view.GetProperty("voting");
            Assert.Equal(JsonValueKind.Object, voting.ValueKind);
            Assert.Equal(canVote, voting.GetProperty("canVote").GetBoolean());
            Assert.Equal(reason, voting.GetProperty("reason").GetString());
            Assert.Equal(allowChangeVote, voting.GetProperty("allowChangeVote").GetBoolean());
            Assert.Equal(new[] { "allowChangeVote", "canVote", "reason" }, voting.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        }
        // Reads must never call Execute, spend a vote or fire the timer just to discover availability.
        Assert.Equal(before, (await StateAsync(_site)).Polls[pollId]);
        return card;
    }

    private static async Task AssertPostMatchesAsync(HttpClient client, Guid pollId, string option, string? reason = null)
    {
        var response = await VoteAsync(client, pollId, option);
        if (reason is null)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(option, (await CardAsync(client, pollId)).GetProperty("myVote").GetString());
        }
        else
        {
            await AssertConflictAsync(response, reason);
        }
    }

    private Task ChangeVoteRuleAsync(bool allowed) =>
        RulesAsync(_site, r => r with { Polls = r.Polls! with { AllowChangeVote = allowed } });

    private async Task FinishAsync(string login, bool approved = false, string? beforeFinish = null)
    {
        await _site.SendAsync(new AdjustPlayer(_site.Players[login], "Перенос для проверки финиша", CellId: beforeFinish ?? $"c{RulesetJson.Default().Map.LinearLength - 1}"));
        var player = await _site.SignedInAsync(login);
        await ApiCalls.PostOkAsync(player, Url("roll"));
        await ApiCalls.PostOkAsync(player, Url("start"));
        var runId = (await EventSeason.MeAsync(player)).GetProperty("activeRun").GetProperty("id").GetGuid();
        await ApiCalls.PostOkAsync(player, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        if (approved)
        {
            await ApiCalls.PostOkAsync(await _site.SignedInAsync("admin"), AdminUrl($"runs/{runId}/approve"), new { commandId = Guid.NewGuid(), comment = "Видел прохождение" });
        }
        var finish = (await StateAsync(_site)).Players[_site.Players[login]].Finish;
        Assert.NotNull(finish);
        Assert.Equal(approved, finish.Frozen);
    }

    private static object EffectPollJson() => new
    {
        question = "Кому монетку?",
        options = new { list = new[] { "Финиш", "Дроп", "Реролл" } },
        voters = "players",
        closesInHours = 24,
        onResult = new { target = new { selector = "all" }, actions = new[] { new { type = "changeResource", resource = "coins", amount = 1 } } },
    };

    private static string PollEventJson(string target) => $$"""
        { "id": "asking-event", "kind": "event", "name": "Вопрос всем",
          "description": "Выбери лучший момент, после голосования будет монетка.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "{{target}}" }, "actions": [
            { "type": "requestChoice", "prompt": "Лучший момент?", "options": { "list": ["Финиш", "Дроп", "Реролл"] },
              "vote": { "voters": "players", "anonymous": false, "closesInHours": 24 } },
            { "type": "changeResource", "resource": "coins", "amount": 1 }
          ] } }
        """;
}

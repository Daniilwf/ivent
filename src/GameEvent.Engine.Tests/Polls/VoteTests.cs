using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Polls.PollSetup;

namespace GameEvent.Engine.Tests.Polls;

/// <summary>
/// Votes (task 5.8, SPEC «Голосования»: «Один голос на человека. Зрители голосуют, только если это разрешено конфигом»;
/// «Проверка на дыры» — «Накрутка голосов»; EV24, EV40; D-513). A vote is a user's, not a player's: a player of the season
/// is found by the user, any other user is a spectator. One vote in force per user and poll, the last one counts while
/// <c>polls.allowChangeVote</c> holds; the same vote again (a second tab) writes nothing; the poll's time is checked by the
/// engine's clock, before the timer. Written by <c>test-author</c> from the SPEC, before the implementation.
/// </summary>
public class VoteTests
{
    // ---- S5.8-05 (EV24, EV40): the last vote counts ----

    [Fact]
    public void A_changed_vote_replaces_the_first_and_the_last_one_counts()
    {
        var s = Season();
        var poll = s.Start(ContentPoll() with { Anonymous = false });
        var vasya = s.Player("Вася").UserId;

        s.Voted(poll.PollId, "Вася", "Horror");
        Assert.IsType<VoteCast>(Assert.Single(s.Last.Events));

        s.Voted(poll.PollId, "Вася", "RPG");
        Assert.Equal(new VoteChanged(poll.PollId, vasya, "RPG", false, false), Assert.Single(s.Last.Events));
        Assert.Equal([new Vote(vasya, "RPG")], s.State.Polls[poll.PollId].Votes);

        s.CloseByTimer(poll.PollId);

        var closed = Assert.Single(s.LastEvents<PollClosed>());
        Assert.Equal(1, closed.Counts.Single(c => c.OptionId == "RPG").Votes);
        Assert.Equal(0, closed.Counts.Single(c => c.OptionId == "Horror").Votes);
        Assert.Equal("RPG", closed.Result);
    }

    // ---- S5.8-06 (EV24): a change forbidden by the ruleset ----

    [Fact]
    public void A_change_is_refused_when_the_ruleset_forbids_it()
    {
        var s = Season(Rules(allowChangeVote: false));
        var poll = s.Start(ContentPoll());
        s.Voted(poll.PollId, "Вася", "Horror");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(poll.PollId, "Вася", "RPG"), RejectionCodes.PollAlreadyVoted);

        // The first vote stays; another user still votes
        Assert.Equal([new Vote(s.Player("Вася").UserId, "Horror")], s.State.Polls[poll.PollId].Votes);
        s.Voted(poll.PollId, "Маша", "RPG");
    }

    // ---- S5.8-18 (EV40, EV43): two tabs — the same vote twice ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_same_vote_again_from_a_second_tab_writes_nothing(bool allowChangeVote)
    {
        // Н-22, default: refused with poll.sameVote and zero events (with a change forbidden too: it is no change)
        var s = Season(Rules(allowChangeVote: allowChangeVote));
        var poll = s.Start(ContentPoll());
        s.Voted(poll.PollId, "Вася", "Horror");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(poll.PollId, "Вася", "Horror"), RejectionCodes.PollSameVote);

        Assert.Single(s.State.Polls[poll.PollId].Votes, v => v.UserId == s.Player("Вася").UserId);
    }

    [Fact]
    public void A_vote_for_an_option_off_the_snapshot_is_refused()
    {
        var s = Season();
        var poll = s.Start(ContentPoll());

        // Puzzle is a category with no available game, so not in the snapshot; case matters; empty is nothing
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(poll.PollId, "Вася", "Puzzle"), RejectionCodes.PollUnknownOption);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(poll.PollId, "Вася", "horror"), RejectionCodes.PollUnknownOption);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(poll.PollId, "Вася", ""), RejectionCodes.PollUnknownOption);
    }

    [Fact]
    public void An_option_is_the_snapshot_even_when_the_pool_changes_after_the_start()
    {
        var s = Season();
        var poll = s.Start(ContentPoll());

        // Racing loses its only game after the start: the option of the snapshot is still voted and wins
        s.DeleteGame("Need for Speed");
        s.Voted(poll.PollId, "Вася", "Racing");
        s.CloseByTimer(poll.PollId);

        Assert.Equal("Racing", Assert.Single(s.LastEvents<PollClosed>()).Result);
    }

    // ---- S5.8-07 (EV24, EV40): spectators only by the ruleset and only in «everyone» ----

    [Theory]
    [InlineData(false, PollVoters.Everyone, false)]
    [InlineData(true, PollVoters.Players, false)]
    [InlineData(false, PollVoters.Players, false)]
    [InlineData(true, PollVoters.Everyone, true)]
    public void A_spectator_votes_only_in_a_poll_for_everyone_with_spectators_allowed(bool spectatorsCanVote, PollVoters voters, bool accepted)
    {
        var s = Season(Rules(Spectators(spectatorsCanVote)));
        var poll = s.Start(ListPoll(voters));

        if (accepted)
        {
            s.Act(new CastVote(poll.PollId, Spectator, "Финиш"));
            ScenarioAssert.Accepted(s);
            Assert.Equal([new Vote(Spectator, "Финиш")], s.State.Polls[poll.PollId].Votes);
        }
        else
        {
            ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new CastVote(poll.PollId, Spectator, "Финиш")), RejectionCodes.PollNotVoter);
        }

        // A player votes in both kinds of poll
        s.Voted(poll.PollId, "Петя", "Дроп");
    }

    [Fact]
    public void A_spectator_vote_is_one_vote_too()
    {
        var s = Season(Rules(Spectators(true)));
        var poll = s.Start(ListPoll(PollVoters.Everyone));
        s.Act(new CastVote(poll.PollId, Spectator, "Финиш"));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new CastVote(poll.PollId, Spectator, "Финиш")), RejectionCodes.PollSameVote);
        s.Act(new CastVote(poll.PollId, Spectator, "Дроп"));
        Assert.IsType<VoteChanged>(Assert.Single(s.Last.Events));
        Assert.Single(s.State.Polls[poll.PollId].Votes);
    }

    // ---- S5.8-08 (EV24): «players» — only the players of this season ----

    [Fact]
    public void In_a_players_poll_a_user_who_plays_another_season_is_not_a_voter()
    {
        // A player of another season is a user without a player here: a spectator of this season
        var s = Season(Rules(Spectators(true)));
        var poll = s.Start(ListPoll(PollVoters.Players));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new CastVote(poll.PollId, Spectator, "Финиш")), RejectionCodes.PollNotVoter);
    }

    [Fact]
    public void An_inactive_player_is_still_a_player_of_the_season_and_votes()
    {
        // SE5: inactive is out of the random targets, not out of the season
        var s = Season();
        s.Inactive("Коля");
        var poll = s.Start(ListPoll(PollVoters.Players));

        s.Voted(poll.PollId, "Коля", "Финиш");
    }

    // ---- S5.8-11 (EV24): the poll's time by the engine's clock, before the timer ----

    [Fact]
    public void A_vote_after_the_polls_time_is_refused_before_the_timer_and_the_result_is_by_the_earlier_votes()
    {
        var s = Season();
        var poll = s.Start(ContentPoll());
        s.Voted(poll.PollId, "Вася", "Horror");

        s.Advance(TimeSpan.FromHours(24).Add(TimeSpan.FromMinutes(1)));
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(poll.PollId, "Маша", "RPG"), RejectionCodes.PollClosed);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(poll.PollId, "Петя", "RPG"), RejectionCodes.PollClosed);
        Assert.Equal(PollStatus.Open, s.State.Polls[poll.PollId].Status);

        s.Act(new FireTimers());

        var closed = Assert.Single(s.LastEvents<PollClosed>());
        Assert.Equal("Horror", closed.Result);
        Assert.Equal(0, closed.Counts.Single(c => c.OptionId == "RPG").Votes);
    }

    [Fact]
    public void A_vote_a_minute_before_the_time_counts_and_at_the_time_it_does_not()
    {
        var s = Season();
        var poll = s.Start(ContentPoll());

        s.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1));
        s.Voted(poll.PollId, "Вася", "RPG");

        s.Advance(TimeSpan.FromMinutes(1));
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(poll.PollId, "Маша", "Horror"), RejectionCodes.PollClosed);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(poll.PollId, "Вася", "Horror"), RejectionCodes.PollClosed);
    }

    [Fact]
    public void A_change_after_the_close_is_refused()
    {
        var s = Season();
        var poll = s.Start(ContentPoll());
        s.Voted(poll.PollId, "Вася", "Horror");
        s.Act(new ClosePoll(poll.PollId));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(poll.PollId, "Вася", "RPG"), RejectionCodes.PollClosed);
        Assert.Equal([new Vote(s.Player("Вася").UserId, "Horror")], s.State.Polls[poll.PollId].Votes);
    }

    [Fact]
    public void Votes_are_kept_ordered_by_user_whatever_the_order_they_came_in()
    {
        var s = Season();
        var poll = s.Start(ListPoll());

        s.Voted(poll.PollId, "Коля", "Финиш").Voted(poll.PollId, "Вася", "Дроп").Voted(poll.PollId, "Маша", "Финиш");

        var votes = s.State.Polls[poll.PollId].Votes;
        Assert.Equal(votes.Select(v => v.UserId).Order(), votes.Select(v => v.UserId));
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Fact]
    public void One_user_votes_once_in_each_of_two_polls()
    {
        var s = Season();
        var first = s.Start(ListPoll());
        var second = s.Start(ListPoll(options: ["Да", "Нет"]));

        s.Voted(first.PollId, "Вася", "Финиш").Voted(second.PollId, "Вася", "Да");

        // The option of one poll is not an option of another
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(second.PollId, "Маша", "Финиш"), RejectionCodes.PollUnknownOption);
        Assert.Single(s.State.Polls[first.PollId].Votes);
        Assert.Single(s.State.Polls[second.PollId].Votes);
    }
}

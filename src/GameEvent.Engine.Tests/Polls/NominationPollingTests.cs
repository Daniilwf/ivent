using GameEvent.Engine.Gallery;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Polls;

public sealed class NominationPollingTests
{
    private static readonly Guid s_first = SequentialIds.Make(0xA6000000, 1);
    private static readonly Guid s_second = SequentialIds.Make(0xA6000000, 2);
    private static readonly Guid s_firstPoll = SequentialIds.Make(0xA7000000, 1);
    private static readonly Guid s_secondPoll = SequentialIds.Make(0xA7000000, 2);
    private static readonly Guid s_file = SequentialIds.Make(0xA8000000, 1);

    [Fact]
    public void Undoing_a_moderation_hide_restores_the_item_but_the_closed_ballot_stays_cancelled()
    {
        var season = GallerySeason();
        Start(season, s_firstPoll, VotedNomination.FunniestMoment, s_first, s_second);
        season.Act(new CastVote(s_firstPoll, season.Player("Вася").UserId, s_first.ToString("D")));
        ScenarioAssert.Accepted(season);
        season.CloseByTimer(s_firstPoll);
        Assert.Equal(s_first.ToString("D"), season.State.Polls[s_firstPoll].Result);

        season.Act(new HideGalleryMoment(s_first));
        ScenarioAssert.Accepted(season);
        Assert.Equal(s_firstPoll, Assert.Single(season.LastEvents<PollCancelled>()).PollId);
        var hide = season.LastCommandId;
        season.Act(new UndoCommand(hide, "The frame may be visible again"));
        ScenarioAssert.Accepted(season);

        Assert.True(season.State.GalleryItems[s_first].Visible);
        Assert.Equal(PollStatus.Cancelled, season.State.Polls[s_firstPoll].Status);
        Assert.Null(season.State.Polls[s_firstPoll].Result);
        Start(season, s_secondPoll, VotedNomination.FunniestMoment, s_first, s_second);
        Assert.Equal(season.State, SeasonEngine.Replay(season.StoredLog));
    }

    [Fact]
    public void A_pending_publication_protects_the_ballot_configuration_even_after_the_timer()
    {
        var season = GallerySeason();
        Start(season, s_firstPoll, VotedNomination.FunniestMoment, s_first, s_second);
        var withoutPolls = season.Ruleset with { Features = season.Ruleset.Features with { Polls = false } };
        ScenarioAssert.RejectsWithoutChanges(season, s => s.Act(new ChangeRuleset(withoutPolls)), RejectionCodes.NominationInPlay);
        season.CloseByTimer(s_firstPoll);
        var withoutCategory = season.Ruleset with
        {
            Nominations = season.Ruleset.Nominations with { Voted = [VotedNomination.BestReview] },
        };
        ScenarioAssert.RejectsWithoutChanges(season, s => s.Act(new ChangeRuleset(withoutCategory)), RejectionCodes.NominationInPlay);

        season.Act(new HideGalleryMoment(s_first));
        ScenarioAssert.Accepted(season);
        season.Act(new ChangeRuleset(withoutCategory));
        ScenarioAssert.Accepted(season);
    }

    [Fact]
    public void A_deleted_game_cancels_its_game_ballot_and_its_reviews_ballot_before_publication()
    {
        var season = NewSeason();
        var first = CompleteAndApprove(season, "Вася");
        var second = CompleteAndApprove(season, "Петя");
        season.MoveStatusTo(SeasonStatus.Closing);
        Start(season, s_firstPoll, VotedNomination.GameOfSeason, first.GameId, second.GameId);
        Start(season, s_secondPoll, VotedNomination.BestReview, first.RunId, second.RunId);

        season.Act(new CancelNominationsForDeletedGame(first.GameId));
        ScenarioAssert.Accepted(season);
        Assert.Equal([s_firstPoll, s_secondPoll], season.LastEvents<PollCancelled>().Select(e => e.PollId));
        Assert.All(season.State.Polls.Values, poll => Assert.Equal(PollStatus.Cancelled, poll.Status));
        var cancellation = season.LastCommandId;
        ScenarioAssert.RejectsWithoutChanges(season, s => s.Act(new UndoCommand(cancellation, "Restore the ballots")),
            RejectionCodes.UndoNotUndoable);

        season.Act(new CancelNominationsForDeletedGame(first.GameId));
        ScenarioAssert.Accepted(season);
        Assert.Empty(season.Last.Events);
        Assert.Equal(season.State, SeasonEngine.Replay(season.StoredLog));
    }

    [Fact]
    public void An_edited_review_without_text_cancels_the_review_ballot_and_leaves_the_game_ballot()
    {
        var season = NewSeason();
        var first = CompleteAndApprove(season, "Вася");
        var second = CompleteAndApprove(season, "Петя");
        season.MoveStatusTo(SeasonStatus.Closing);
        Start(season, s_firstPoll, VotedNomination.GameOfSeason, first.GameId, second.GameId);
        Start(season, s_secondPoll, VotedNomination.BestReview, first.RunId, second.RunId);
        season.Review("Вася", first.RunId, 9);

        Assert.Equal(s_secondPoll, Assert.Single(season.LastEvents<PollCancelled>()).PollId);
        Assert.Equal(PollStatus.Open, season.State.Polls[s_firstPoll].Status);
        Assert.Equal(PollStatus.Cancelled, season.State.Polls[s_secondPoll].Status);
    }

    [Fact]
    public void Replacing_the_review_text_cancels_its_ballot_and_an_undo_does_not_reopen_it()
    {
        var season = NewSeason();
        var first = CompleteAndApprove(season, "Вася");
        var second = CompleteAndApprove(season, "Петя");
        season.MoveStatusTo(SeasonStatus.Closing);
        Start(season, s_firstPoll, VotedNomination.BestReview, first.RunId, second.RunId);
        season.Act(new CastVote(s_firstPoll, season.Player("Вася").UserId, first.RunId.ToString("D")));
        ScenarioAssert.Accepted(season);

        season.Review("Вася", first.RunId, 8, " A public review ");
        Assert.Empty(season.LastEvents<PollCancelled>());
        Assert.Equal(PollStatus.Open, season.State.Polls[s_firstPoll].Status);

        season.Review("Вася", first.RunId, 8, "A different review");
        Assert.Equal(s_firstPoll, Assert.Single(season.LastEvents<PollCancelled>()).PollId);
        var changedReview = season.LastCommandId;
        season.Act(new UndoCommand(changedReview, "Restore the original review"));
        ScenarioAssert.Accepted(season);

        Assert.Equal("A public review", season.State.Runs[first.RunId].Review?.Text);
        Assert.Equal(PollStatus.Cancelled, season.State.Polls[s_firstPoll].Status);
        Assert.Equal(season.State, SeasonEngine.Replay(season.StoredLog));
    }

    private static Scenario NewSeason() => PollSetup.Season(PollSetup.Rules(rules => rules with
    {
        Features = rules.Features with { Gallery = true, SeasonNominations = true },
        Nominations = rules.Nominations with
        {
            Voted = [VotedNomination.BestReview, VotedNomination.GameOfSeason, VotedNomination.FunniestMoment],
            DurationHours = 3,
            MinCandidates = 2,
            MaxCandidates = 20,
        },
    }));

    private static Scenario GallerySeason()
    {
        var season = NewSeason();
        var player = season.Player("Вася");
        var now = season.Clock.UtcNow;
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        season.AppendCraftedEvents(
            new GalleryMomentPublished(s_first, player.UserId, player.PlayerId, s_file, "First moment", now, day),
            new GalleryMomentPublished(s_second, player.UserId, player.PlayerId, s_file, "Second moment", now, day));
        return season.MoveStatusTo(SeasonStatus.Closing);
    }

    private static RunState CompleteAndApprove(Scenario season, string player)
    {
        season.Roll(player).Start(player);
        var runId = season.Player(player).ActiveRunId!.Value;
        season.Complete(player, review: new RunReview(9, "A public review"));
        season.Act(new ApproveProof(runId, Comment: "Completion confirmed"));
        ScenarioAssert.Accepted(season);
        return season.State.Runs[runId];
    }

    private static void Start(Scenario season, Guid pollId, VotedNomination nomination, params Guid[] ids)
    {
        var context = season.Context() with { AvailableNominationCandidates = ids.ToHashSet() };
        var result = SeasonEngine.Execute(season.State, new StartNominationPoll(pollId, nomination, [.. ids]), context);
        Assert.True(result.IsAccepted, result.Rejection?.Detail);
        var started = Assert.IsType<NominationPollStarted>(Assert.Single(result.Events));
        Assert.Equal(pollId, started.PollId);
        season.AppendCraftedEvents(started);
        Assert.Equal(result.State, season.State);
    }
}

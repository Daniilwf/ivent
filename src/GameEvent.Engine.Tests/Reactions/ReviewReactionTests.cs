using FsCheck.Xunit;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Reactions;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Reactions;

/// <summary>
/// A reaction belongs to one user, one review, and one configured emoji (SPEC «Лента и общение» and «Роль зритель»).
/// It changes only the reaction set, including when undone or replayed from the stored log.
/// </summary>
public sealed class ReviewReactionTests
{
    private const string Fire = "🔥";
    private const string Laugh = "😂";
    private static readonly Guid s_spectator = SequentialIds.Make(0x0BAD0000, 1);

    private static (Scenario Scenario, Guid RunId) Completed(bool enabled = true, bool review = true)
    {
        var scenario = Scenario.New()
            .WithRuleset(rules => rules with { Features = rules.Features with { Reactions = enabled } })
            .WithCategory("Horror")
            .WithGame("Silent Hill", 6, "Horror")
            .WithGame("Alan Wake", 6, "Horror")
            .WithPlayers("Вася", "Петя")
            .RollTitle("Вася", "Silent Hill")
            .Start("Вася");
        var runId = scenario.Player("Вася").ActiveRunId!.Value;
        scenario.Complete("Вася", review: review ? new RunReview(8, "Отличная игра") : null);
        ScenarioAssert.Accepted(scenario);
        return (scenario, runId);
    }

    private static void Rejects(Scenario scenario, ICommand command, string code) =>
        ScenarioAssert.RejectsWithoutChanges(scenario, s => s.Act(command), code);

    [Fact]
    public void A_player_and_a_spectator_can_react_to_the_same_review_without_changing_gameplay()
    {
        var (s, runId) = Completed();
        var player = s.Player("Петя").UserId;
        var before = s.State;
        Assert.False(s.Ruleset.Social.SpectatorsCanVote);

        s.Act(new SetReviewReaction(runId, player, Fire, Selected: true));
        Assert.Equal(new ReviewReactionSet(runId, player, Fire, Selected: true), Assert.Single(s.Last.Events));
        s.Act(new SetReviewReaction(runId, s_spectator, Fire, Selected: true));
        Assert.Equal(new ReviewReactionSet(runId, s_spectator, Fire, Selected: true), Assert.Single(s.Last.Events));
        s.Act(new SetReviewReaction(runId, player, Laugh, Selected: true));

        Assert.True(s.State.ReviewReactions.SetEquals([
            new ReviewReaction(runId, player, Fire),
            new ReviewReaction(runId, s_spectator, Fire),
            new ReviewReaction(runId, player, Laugh),
        ]));

        s.Act(new SetReviewReaction(runId, player, Fire, Selected: false));
        Assert.Equal(new ReviewReactionSet(runId, player, Fire, Selected: false), Assert.Single(s.Last.Events));
        Assert.True(s.State.ReviewReactions.SetEquals([
            new ReviewReaction(runId, s_spectator, Fire),
            new ReviewReaction(runId, player, Laugh),
        ]));
        Assert.Equal(before.Players, s.State.Players);
        Assert.Equal(before.Runs, s.State.Runs);
    }

    [Fact]
    public void A_reaction_follows_the_review_identity_across_edits_and_does_not_affect_another_run()
    {
        var (s, firstRun) = Completed();
        var player = s.Player("Петя").UserId;
        s.Roll("Петя").Start("Петя");
        var secondRun = s.Player("Петя").ActiveRunId!.Value;
        s.Complete("Петя", review: new RunReview(7, "Другой отзыв"));
        ScenarioAssert.Accepted(s);

        s.Act(new SetReviewReaction(firstRun, player, Fire, Selected: true));
        s.Act(new SetReviewReaction(secondRun, player, Fire, Selected: true));
        s.Review("Вася", firstRun, 9, "Обновлённый отзыв");
        ScenarioAssert.Accepted(s);
        Assert.Contains(new ReviewReaction(firstRun, player, Fire), s.State.ReviewReactions);
        Assert.Equal(9, s.State.Runs[firstRun].Review!.Rating);

        s.Act(new SetReviewReaction(firstRun, player, Fire, Selected: false));
        Assert.Equal(new ReviewReaction(secondRun, player, Fire), Assert.Single(s.State.ReviewReactions));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Either_desired_state_is_refused_while_reactions_are_disabled(bool selected)
    {
        var (s, runId) = Completed(enabled: false);
        Rejects(s, new SetReviewReaction(runId, s_spectator, Fire, selected), RejectionCodes.FeatureDisabled);
    }

    [Fact]
    public void An_old_review_can_receive_a_reaction_when_the_flag_is_enabled_later()
    {
        var (s, runId) = Completed(enabled: false);
        var oldReview = s.State.Runs[runId].Review;
        Assert.Empty(SeasonEngine.Replay(s.StoredLog).ReviewReactions);

        s.WithRuleset(rules => rules with { Features = rules.Features with { Reactions = true } });
        s.Act(new SetReviewReaction(runId, s_spectator, Fire, Selected: true));

        Assert.Equal(oldReview, s.State.Runs[runId].Review);
        Assert.Equal(new ReviewReaction(runId, s_spectator, Fire), Assert.Single(s.State.ReviewReactions));
    }

    [Fact]
    public void The_archived_season_keeps_its_result_when_a_viewer_selects_or_removes_a_reaction()
    {
        var (s, runId) = Completed();
        s.Act(new Engine.Proofs.ApproveProof(runId, Comment: "Видел прохождение"));
        s.MoveStatusTo(SeasonStatus.Archived);
        var result = s.State.Result;

        s.Act(new SetReviewReaction(runId, s_spectator, Fire, Selected: true));
        ScenarioAssert.Accepted(s);
        Assert.Single(s.State.ReviewReactions);
        Rejects(s, new UndoCommand(s.LastCommandId, "Архивный сезон"), RejectionCodes.SeasonClosed);
        s.Act(new SetReviewReaction(runId, s_spectator, Fire, Selected: false));
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.State.ReviewReactions);
        Assert.Equal(result, s.State.Result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_run_without_a_review_and_an_unknown_run_are_refused(bool selected)
    {
        var (s, runId) = Completed(review: false);

        Rejects(s, new SetReviewReaction(runId, s_spectator, Fire, selected), "reviewReaction.reviewUnknown");
        Rejects(s, new SetReviewReaction(SequentialIds.Make(0x0BAD0000, 2), s_spectator, Fire, selected), "reviewReaction.reviewUnknown");
    }

    [Fact]
    public void A_review_of_a_deleted_game_is_not_a_reaction_target()
    {
        var (s, runId) = Completed();
        s.DeleteGame("Silent Hill");

        Rejects(s, new SetReviewReaction(runId, s_spectator, Fire, Selected: true), "reviewReaction.reviewUnknown");
    }

    [Fact]
    public void The_current_ruleset_selects_the_allowed_emoji_as_exact_strings()
    {
        var (s, runId) = Completed();
        s.WithRuleset(rules => rules with { Social = rules.Social with { Reactions = ["⚡", Laugh, "💀", "👏", "🤡", "❤️"] } });

        Rejects(s, new SetReviewReaction(runId, s_spectator, Fire, Selected: true), "reviewReaction.invalidEmoji");
        Rejects(s, new SetReviewReaction(runId, s_spectator, "❤", Selected: true), "reviewReaction.invalidEmoji");
        Rejects(s, new SetReviewReaction(runId, s_spectator, "", Selected: true), "reviewReaction.invalidEmoji");
        s.Act(new SetReviewReaction(runId, s_spectator, "⚡", Selected: true));

        Assert.Equal(new ReviewReaction(runId, s_spectator, "⚡"), Assert.Single(s.State.ReviewReactions));
    }

    [Fact]
    public void Undo_restores_membership_and_names_a_reaction_that_depends_on_the_review()
    {
        var (s, runId) = Completed();
        var completion = s.LastCommandId;
        s.Act(new SetReviewReaction(runId, s_spectator, Fire, Selected: true));
        var add = s.LastCommandId;
        Rejects(s, new UndoCommand(completion, "Исправление"), RejectionCodes.UndoDependents);
        Assert.Equal([add], s.Last.Rejection!.Related);

        s.Act(new SetReviewReaction(runId, s_spectator, Fire, Selected: false));
        var remove = s.LastCommandId;
        Assert.Empty(s.State.ReviewReactions);
        s.Act(new UndoCommand(remove, "Восстановление реакции"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(new ReviewReaction(runId, s_spectator, Fire), Assert.Single(s.State.ReviewReactions));

        s.Act(new UndoCommand(add, "Отмена реакции"));
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.State.ReviewReactions);
        s.Act(new UndoCommand(completion, "Исправление"));
        ScenarioAssert.Accepted(s);
        Assert.Null(s.State.Runs[runId].Review);

        var replay = SeasonEngine.Replay(s.StoredLog);
        Assert.Null(replay.Runs[runId].Review);
        Assert.Empty(replay.ReviewReactions);
    }

    [Fact]
    public void A_same_state_command_records_its_result_without_creating_a_second_reaction()
    {
        var (s, runId) = Completed();
        s.Act(new SetReviewReaction(runId, s_spectator, Fire, Selected: true));

        s.Act(new SetReviewReaction(runId, s_spectator, Fire, Selected: true));
        Assert.Equal(new ReviewReactionSet(runId, s_spectator, Fire, Selected: true), Assert.Single(s.Last.Events));
        Assert.Equal(new ReviewReaction(runId, s_spectator, Fire), Assert.Single(s.State.ReviewReactions));

        s.Act(new SetReviewReaction(runId, s_spectator, Fire, Selected: false));
        s.Act(new SetReviewReaction(runId, s_spectator, Fire, Selected: false));
        Assert.Equal(new ReviewReactionSet(runId, s_spectator, Fire, Selected: false), Assert.Single(s.Last.Events));
        Assert.Empty(s.State.ReviewReactions);
    }

    [Fact]
    public void A_later_confirmation_holds_the_reaction_when_the_first_selection_is_undone()
    {
        var (s, runId) = Completed();
        var keep = new SetReviewReaction(runId, s_spectator, Fire, Selected: true);
        s.Act(keep);
        var initial = s.LastCommandId;
        s.Act(keep);
        var confirmation = s.LastCommandId;

        Rejects(s, new UndoCommand(initial, "Исправление"), RejectionCodes.UndoDependents);
        Assert.Equal([confirmation], s.Last.Rejection!.Related);
        Assert.Single(s.State.ReviewReactions);
    }

    [Fact]
    public void Different_people_can_react_to_the_same_review_and_have_independent_undos()
    {
        var (s, runId) = Completed();
        var player = s.Player("Петя").UserId;
        s.Act(new SetReviewReaction(runId, s_spectator, Fire, Selected: true));
        var spectatorReaction = s.LastCommandId;
        s.Act(new SetReviewReaction(runId, player, Fire, Selected: true));

        s.Act(new UndoCommand(spectatorReaction, "Отмена реакции зрителя"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(new ReviewReaction(runId, player, Fire), Assert.Single(s.State.ReviewReactions));
    }

    [Property(MaxTest = 100)]
    public void Each_user_and_emoji_have_at_most_one_reaction_and_every_command_records_the_result(byte[] requests)
    {
        var (s, runId) = Completed();
        var player = s.Player("Петя").UserId;
        var expected = new HashSet<ReviewReaction>();

        foreach (var request in requests)
        {
            var actor = (request & 1) == 0 ? player : s_spectator;
            var emoji = (request & 2) == 0 ? Fire : Laugh;
            var selected = (request & 4) != 0;
            var key = new ReviewReaction(runId, actor, emoji);
            if (selected)
            {
                expected.Add(key);
            }
            else
            {
                expected.Remove(key);
            }

            s.Act(new SetReviewReaction(runId, actor, emoji, selected));
            ScenarioAssert.Accepted(s);
            Assert.Equal(new ReviewReactionSet(runId, actor, emoji, selected), Assert.Single(s.Last.Events));
            Assert.True(s.State.ReviewReactions.SetEquals(expected));
        }

        Assert.True(SeasonEngine.Replay(s.StoredLog).ReviewReactions.SetEquals(expected));
    }
}

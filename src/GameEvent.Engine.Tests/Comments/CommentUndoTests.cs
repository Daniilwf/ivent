using GameEvent.Engine.Comments;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Comments;

public sealed class CommentUndoTests
{
    private static readonly Guid s_account = SequentialIds.Make(0x0BAD0000, 1);
    private static readonly Guid s_firstComment = SequentialIds.Make(0x7C000000, 1);
    private static readonly Guid s_secondComment = SequentialIds.Make(0x7C000000, 2);
    private static readonly Guid s_thirdComment = SequentialIds.Make(0x7C000000, 3);

    [Fact]
    public void Undo_restores_the_comment_and_its_debit_without_holding_the_feed_source()
    {
        var s = WithComments().WithPlayers("Player");
        var player = s.Player("Player");
        s.Act(new AdjustPlayer(player.PlayerId, "Adjustment", PointsDelta: 5));
        ScenarioAssert.Accepted(s);
        var source = s.LastCommandId;
        var target = new CommentTarget(CommentTargetKind.FeedEntry, source);
        var first = AppendFeedCommand(s, new PostComment(s_firstComment, target, s_account, "First"));
        var firstBeforeEdit = s.State.Comments[s_firstComment];
        AppendFeedCommand(s, new PostComment(s_secondComment, target, s_account, "Second"));
        var edit = AppendFeedCommand(s, new EditComment(s_firstComment, target, s_account, "Edited"));
        Assert.Equal("Edited", s.State.Comments[s_firstComment].Text);

        s.Act(new UndoCommand(first, "Undo the first comment"));
        var dependentEdit = Assert.IsType<Rejection>(s.Last.Rejection);
        Assert.Equal(RejectionCodes.UndoDependents, dependentEdit.Code);
        Assert.Equal(edit, Assert.Single(dependentEdit.Related));

        s.Act(new UndoCommand(edit, "Undo the edit"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(firstBeforeEdit, s.State.Comments[s_firstComment]);

        s.Act(new UndoCommand(first, "Undo the first comment"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(s_secondComment, Assert.Single(s.State.Comments).Key);
        AppendFeedCommand(s, new PostComment(s_thirdComment, target, s_account, "Debited after undo"));

        s.Act(new UndoCommand(source, "Undo the source movement"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(player.Points, s.Player("Player").Points);
        Assert.Equal(2, s.State.Comments.Count);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Fact]
    public void Comments_hold_the_original_review_publication_and_stay_independent_of_later_review_edits()
    {
        var s = WithComments()
            .WithCategory("Category")
            .WithGame("A game", 6, "Category")
            .WithPlayers("Player")
            .RollTitle("Player", "A game")
            .Start("Player");
        var player = s.Player("Player");
        var runId = player.ActiveRunId!.Value;
        s.Complete("Player");
        ScenarioAssert.Accepted(s);
        s.Act(new ReviewRun(player.PlayerId, runId, new RunReview(8, "Original review")));
        ScenarioAssert.Accepted(s);
        var review = s.LastCommandId;
        var target = new CommentTarget(CommentTargetKind.Review, runId);
        s.Act(new PostComment(s_firstComment, target, s_account, "Comment on the review"));
        ScenarioAssert.Accepted(s);
        var post = s.LastCommandId;

        s.Act(new ReviewRun(player.PlayerId, runId, new RunReview(9, "Edited review")));
        ScenarioAssert.Accepted(s);
        var edit = s.LastCommandId;
        s.Act(new UndoCommand(edit, "Undo the later review edit"));
        ScenarioAssert.Accepted(s);
        Assert.Equal("Original review", s.State.Runs[runId].Review!.Text);
        Assert.Single(s.State.Comments);

        s.Act(new UndoCommand(review, "Undo the original review"));
        var dependentPost = Assert.IsType<Rejection>(s.Last.Rejection);
        Assert.Equal(RejectionCodes.UndoDependents, dependentPost.Code);
        Assert.Equal(post, Assert.Single(dependentPost.Related));

        s.Act(new UndoCommand(post, "Undo the comment"));
        ScenarioAssert.Accepted(s);
        s.Act(new UndoCommand(review, "Undo the original review"));
        ScenarioAssert.Accepted(s);
        Assert.Null(s.State.Runs[runId].Review);
        Assert.Empty(s.State.Comments);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    private static Scenario WithComments() =>
        Scenario.New().WithRuleset(rules => rules with
        {
            Features = rules.Features with { Comments = true },
            Social = rules.Social with { CommentsPerDay = 2 },
        });

    private static Guid AppendFeedCommand(Scenario scenario, ICommentCommand command)
    {
        var result = SeasonEngine.Execute(scenario.State, command, scenario.Context() with { CommentTargetAvailable = true });
        Assert.True(result.IsAccepted, result.Rejection?.Detail);
        scenario.AppendCraftedEvents([.. result.Events]);
        Assert.Equal(result.State, scenario.State);
        return scenario.History[^1].CommandId;
    }
}

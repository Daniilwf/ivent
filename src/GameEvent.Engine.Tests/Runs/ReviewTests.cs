using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Runs;

/// <summary>
/// A review of a completed run (W9; SPEC «Отзыв»: «необязательная оценка 1–10 и текст»; D-96): with the completion
/// (<see cref="CompleteRun.Review"/>) or later with <see cref="ReviewRun"/> on the player's own completed run; a newer
/// review replaces the earlier one. Text up to <see cref="Limits.MaxReviewLength"/>, blank text is no text, text is trimmed at the edges (D-96 (4)). Allowed
/// until the season is archived.
/// </summary>
public class ReviewTests
{
    private static Scenario Playing(decimal? hours = 6)
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", hours, "Horror").WithGame("Alan Wake", 6, "Horror")
            .WithPlayers("Вася", "Петя")
            .Roll("Вася").Start("Вася");
        ScenarioAssert.Accepted(s);
        return s;
    }

    /// <summary>Вася has completed one run (without a review); returns its id.</summary>
    private static (Scenario S, Guid RunId) Completed()
    {
        var s = Playing();
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.Complete("Вася");
        ScenarioAssert.Accepted(s);
        return (s, runId);
    }

    // ---- With the completion ----

    [Fact]
    public void Review_with_the_completion_is_the_last_event_and_is_kept_with_the_run()
    {
        var s = Playing();
        var runId = s.Player("Вася").ActiveRunId!.Value;
        var vasya = s.PlayerId("Вася");

        s.Complete("Вася", review: new RunReview(8, "Туман и радио — лучшее в серии"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new RunReviewed(runId, vasya, 8, "Туман и радио — лучшее в серии", s.Clock.UtcNow), s.Last.Events[^1]);
        Assert.Single(s.LastEvents<RunReviewed>());
        Assert.Equal(new RunReview(8, "Туман и радио — лучшее в серии"), s.State.Runs[runId].Review);
    }

    [Fact]
    public void Completion_without_a_review_leaves_none()
    {
        var (s, runId) = Completed();

        Assert.Empty(s.LastEvents<RunReviewed>());
        Assert.Null(s.State.Runs[runId].Review);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void Rating_bounds_are_accepted(int rating)
    {
        var s = Playing();

        s.Complete("Вася", review: new RunReview(rating, null));

        ScenarioAssert.Accepted(s);
        Assert.Equal(rating, Assert.Single(s.LastEvents<RunReviewed>()).Rating);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-1)]
    public void Rating_out_of_range_rejects_the_whole_completion(int rating)
    {
        var s = Playing();

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Complete("Вася", review: new RunReview(rating, "текст")), RejectionCodes.InvalidRating);
        Assert.Equal(TurnPhase.Playing, s.Player("Вася").Phase);
    }

    [Fact]
    public void Text_over_the_limit_rejects_the_whole_completion()
    {
        var s = Playing();

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Complete("Вася", review: new RunReview(7, new string('я', Limits.MaxReviewLength + 1))), RejectionCodes.ReviewTooLong);
    }

    [Fact]
    public void Text_of_the_maximum_length_is_accepted()
    {
        var s = Playing();
        var text = new string('я', Limits.MaxReviewLength);

        s.Complete("Вася", review: new RunReview(7, text));

        ScenarioAssert.Accepted(s);
        Assert.Equal(text, Assert.Single(s.LastEvents<RunReviewed>()).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void Blank_text_is_stored_as_no_text(string text)
    {
        var s = Playing();
        var runId = s.Player("Вася").ActiveRunId!.Value;

        s.Complete("Вася", review: new RunReview(6, text));

        ScenarioAssert.Accepted(s);
        Assert.Null(Assert.Single(s.LastEvents<RunReviewed>()).Text);
        Assert.Equal(new RunReview(6, null), s.State.Runs[runId].Review);
    }

    [Theory]
    [InlineData("  Отлично  ", "Отлично")]
    [InlineData("\n Отлично\t", "Отлично")]
    [InlineData("Туман  и радио", "Туман  и радио")] // only the edges, not the inside
    public void Text_is_trimmed_at_the_edges(string text, string stored)
    {
        // D-96 (4): the server trims the review text
        var s = Playing();
        var runId = s.Player("Вася").ActiveRunId!.Value;

        s.Complete("Вася", review: new RunReview(9, text));

        ScenarioAssert.Accepted(s);
        Assert.Equal(stored, Assert.Single(s.LastEvents<RunReviewed>()).Text);
        Assert.Equal(new RunReview(9, stored), s.State.Runs[runId].Review);
    }

    // ---- ReviewRun ----

    [Fact]
    public void Player_reviews_their_completed_run_later()
    {
        var (s, runId) = Completed();
        s.Advance(TimeSpan.FromHours(3));

        s.Review("Вася", runId, 9, "Перепрошёл бы");

        ScenarioAssert.Accepted(s);
        Assert.Equal([new RunReviewed(runId, s.PlayerId("Вася"), 9, "Перепрошёл бы", s.Clock.UtcNow)], s.Last.Events);
        Assert.Equal(new RunReview(9, "Перепрошёл бы"), s.State.Runs[runId].Review);
    }

    [Fact]
    public void Newer_review_replaces_the_earlier_one()
    {
        var s = Playing();
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.Complete("Вася", review: new RunReview(7, "Неплохо"));

        s.Review("Вася", runId, 4);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new RunReview(4, null), s.State.Runs[runId].Review);
        Assert.Equal(2, s.Log.OfType<RunReviewed>().Count());
    }

    [Fact]
    public void Review_changes_nothing_but_the_review()
    {
        var (s, runId) = Completed();
        var before = s.State;

        s.Review("Вася", runId, 5, "Средне");

        ScenarioAssert.Accepted(s);
        Assert.Equal(before.Players, s.State.Players);
        Assert.Equal(before.ManualEffects, s.State.ManualEffects);
        Assert.Equal(before.Runs[runId] with { Review = new RunReview(5, "Средне") }, s.State.Runs[runId]);
        Assert.Equal(before with { Runs = s.State.Runs }, s.State);
    }

    [Fact]
    public void Review_is_allowed_while_playing_the_next_game()
    {
        var (s, runId) = Completed();
        s.Roll("Вася").Start("Вася");
        Assert.Equal(TurnPhase.Playing, s.Player("Вася").Phase);

        s.Review("Вася", runId, 8);

        ScenarioAssert.Accepted(s);
        Assert.Equal(TurnPhase.Playing, s.Player("Вася").Phase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Later_review_with_rating_out_of_range_is_rejected(int rating)
    {
        var (s, runId) = Completed();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Review("Вася", runId, rating), RejectionCodes.InvalidRating);
    }

    [Fact]
    public void Later_review_with_text_over_the_limit_is_rejected()
    {
        var (s, runId) = Completed();

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Review("Вася", runId, 5, new string('я', Limits.MaxReviewLength + 1)), RejectionCodes.ReviewTooLong);
    }

    [Fact]
    public void Later_review_with_blank_text_has_no_text()
    {
        var (s, runId) = Completed();

        s.Review("Вася", runId, 5, "  ");

        ScenarioAssert.Accepted(s);
        Assert.Null(Assert.Single(s.LastEvents<RunReviewed>()).Text);
    }

    [Fact]
    public void Later_review_text_is_trimmed_at_the_edges()
    {
        var (s, runId) = Completed();

        s.Review("Вася", runId, 5, "  Отлично  ");

        ScenarioAssert.Accepted(s);
        Assert.Equal("Отлично", Assert.Single(s.LastEvents<RunReviewed>()).Text);
        Assert.Equal(new RunReview(5, "Отлично"), s.State.Runs[runId].Review);
    }

    [Fact]
    public void Another_players_run_cannot_be_reviewed()
    {
        var (s, runId) = Completed();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Review("Петя", runId, 1, "Плохая игра"), RejectionCodes.NotYourRun);
    }

    [Fact]
    public void Run_being_played_cannot_be_reviewed()
    {
        var s = Playing();
        var runId = s.Player("Вася").ActiveRunId!.Value;

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Review("Вася", runId, 7), RejectionCodes.RunNotCompleted);
    }

    [Fact]
    public void Dropped_run_cannot_be_reviewed()
    {
        var s = Playing();
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.Act(new DropRun(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Review("Вася", runId, 2), RejectionCodes.RunNotCompleted);
    }

    [Fact]
    public void Tech_rerolled_run_cannot_be_reviewed()
    {
        var s = Playing();
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.Act(new TechReroll(s.PlayerId("Вася"), TechRerollReason.DoesNotLaunch, null));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Review("Вася", runId, 2), RejectionCodes.RunNotCompleted);
    }

    [Fact]
    public void Unknown_run_cannot_be_reviewed()
    {
        var (s, _) = Completed();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Review("Вася", SequentialIds.Make(0x0BAD0000, 1), 7), RejectionCodes.RunUnknown);
    }

    [Fact]
    public void Unknown_player_cannot_review()
    {
        var (s, runId) = Completed();

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ReviewRun(SequentialIds.Make(0x0BAD0000, 2), runId, new RunReview(7, null))), RejectionCodes.PlayerUnknown);
    }

    [Theory]
    [InlineData(SeasonStatus.Closing)]
    [InlineData(SeasonStatus.Finished)]
    public void Review_is_allowed_until_the_season_is_archived(SeasonStatus status)
    {
        var (s, runId) = Completed();
        MoveTo(s, status);

        s.Review("Вася", runId, 10, "Итог сезона");

        ScenarioAssert.Accepted(s);
        Assert.Equal(new RunReview(10, "Итог сезона"), s.State.Runs[runId].Review);
    }

    [Fact]
    public void Archived_season_refuses_reviews()
    {
        var (s, runId) = Completed();
        MoveTo(s, SeasonStatus.Archived);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Review("Вася", runId, 10), RejectionCodes.SeasonClosed);
    }

    private static void MoveTo(Scenario s, SeasonStatus status)
    {
        while (s.State.Status < status)
        {
            s.Act(new ChangeSeasonStatus(s.State.Status + 1));
            ScenarioAssert.Accepted(s);
        }
    }
}

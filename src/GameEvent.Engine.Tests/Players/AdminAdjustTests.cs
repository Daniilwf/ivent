using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Players;

/// <summary>
/// D-21: the admin corrects a player with one command; every change is its own event with the reason
/// AdminAdjustment, plus one PlayerAdjusted with the comment. P1: moving the token and changing points are
/// independent. Dropping an offered game returns the player to Idle and frees the game; a playing player is
/// not reset here (that is a drop or a tech reroll, C6).
/// </summary>
public class AdminAdjustTests
{
    private const string Comment = "Потерял скрин, возвращаю очки";

    private static Scenario RunningSeason() =>
        Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
            .WithPlayers("Вася", "Петя");

    private static AdjustPlayer Adjust(
        Scenario s,
        string player,
        string comment = Comment,
        string? cellId = null,
        int points = 0,
        int coins = 0,
        EquatableArray<ResourceDelta> resources = default,
        bool discardOffer = false) =>
        new(s.PlayerId(player), comment, cellId, points, coins, resources, discardOffer);

    // ---- Points and coins ----

    [Fact]
    public void Points_adjustment_is_a_points_change_with_the_admin_reason_and_a_comment()
    {
        var s = RunningSeason();
        var vasya = s.PlayerId("Вася");

        s.Act(Adjust(s, "Вася", points: 5));

        ScenarioAssert.Accepted(s);
        Assert.Equal(2, s.Last.Events.Count);
        Assert.Contains(new PointsChanged(vasya, 5, PointsReason.AdminAdjustment, null), s.Last.Events);
        Assert.Contains(new PlayerAdjusted(vasya, Comment), s.Last.Events);
        Assert.Equal(5, s.Player("Вася").Points);
        Assert.Equal(0, s.Player("Петя").Points);
    }

    [Fact]
    public void Coins_adjustment_is_a_coins_change_with_the_admin_reason()
    {
        var s = RunningSeason();
        var vasya = s.PlayerId("Вася");

        s.Act(Adjust(s, "Вася", coins: 8));

        ScenarioAssert.Accepted(s);
        Assert.Equal(2, s.Last.Events.Count);
        Assert.Contains(new CoinsChanged(vasya, 8, CoinsReason.AdminAdjustment, null), s.Last.Events);
        Assert.Contains(new PlayerAdjusted(vasya, Comment), s.Last.Events);
        Assert.Equal(8, s.Player("Вася").Coins);
        Assert.Equal(0, s.Player("Вася").Points);
    }

    [Fact]
    public void Admin_can_take_points_and_coins_below_zero()
    {
        var s = RunningSeason();

        s.Act(Adjust(s, "Вася", points: -4, coins: -6));

        ScenarioAssert.Accepted(s);
        Assert.Equal(-4, s.Player("Вася").Points);
        Assert.Equal(-6, s.Player("Вася").Coins);
    }

    [Fact]
    public void Adjusted_points_and_coins_are_the_sum_of_logged_changes()
    {
        // Invariant 2 across completions and several corrections
        var s = RunningSeason().Roll("Вася").Start("Вася").Complete("Вася");
        s.Act(Adjust(s, "Вася", points: 3, coins: 2));
        s.Act(Adjust(s, "Вася", points: -10));
        s.Act(Adjust(s, "Вася", coins: 5));
        ScenarioAssert.Accepted(s);

        var vasya = s.PlayerId("Вася");
        Assert.Equal(s.Log.OfType<PointsChanged>().Where(e => e.PlayerId == vasya).Sum(e => e.Delta), s.Player("Вася").Points);
        Assert.Equal(s.Log.OfType<CoinsChanged>().Where(e => e.PlayerId == vasya).Sum(e => e.Delta), s.Player("Вася").Coins);
        Assert.Equal(3, s.Log.OfType<PointsChanged>().Count(e => e.PlayerId == vasya));
    }

    // ---- Other resources ----

    [Fact]
    public void Each_resource_change_is_its_own_event()
    {
        var s = RunningSeason();
        var vasya = s.PlayerId("Вася");

        s.Act(Adjust(s, "Вася", resources: [new ResourceDelta("tickets", 2), new ResourceDelta("keys", 1)]));

        ScenarioAssert.Accepted(s);
        Assert.Equal(3, s.Last.Events.Count);
        Assert.Contains(new ResourceChanged(vasya, "tickets", 2, ResourceReason.AdminAdjustment), s.Last.Events);
        Assert.Contains(new ResourceChanged(vasya, "keys", 1, ResourceReason.AdminAdjustment), s.Last.Events);
        Assert.Contains(new PlayerAdjusted(vasya, Comment), s.Last.Events);
        Assert.Equal(2, s.Player("Вася").Resources["tickets"]);
        Assert.Equal(1, s.Player("Вася").Resources["keys"]);
        Assert.Equal(2, s.Player("Вася").Resources.Count);
    }

    [Fact]
    public void Resource_brought_to_zero_disappears_from_the_dictionary()
    {
        var s = RunningSeason();
        s.Act(Adjust(s, "Вася", resources: [new ResourceDelta("tickets", 2)]));
        ScenarioAssert.Accepted(s);

        s.Act(Adjust(s, "Вася", resources: [new ResourceDelta("tickets", -2)]));

        ScenarioAssert.Accepted(s);
        Assert.Contains(new ResourceChanged(s.PlayerId("Вася"), "tickets", -2, ResourceReason.AdminAdjustment), s.Last.Events);
        Assert.Equal(ResourceBag.Empty, s.Player("Вася").Resources);
        Assert.DoesNotContain(s.Player("Вася").Resources, r => r.Key == "tickets");
    }

    [Fact]
    public void Zero_resource_delta_writes_no_event()
    {
        var s = RunningSeason();

        s.Act(Adjust(s, "Вася", resources: [new ResourceDelta("tickets", 0), new ResourceDelta("keys", 1)]));

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [new ResourceChanged(s.PlayerId("Вася"), "keys", 1, ResourceReason.AdminAdjustment)],
            s.LastEvents<ResourceChanged>());
    }

    // ---- Position (P1) ----

    [Fact]
    public void Position_change_is_a_transfer_with_the_admin_reason()
    {
        var s = RunningSeason();
        var vasya = s.PlayerId("Вася");

        s.Act(Adjust(s, "Вася", cellId: "c10"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(2, s.Last.Events.Count);
        Assert.Contains(new PlayerAdjusted(vasya, Comment), s.Last.Events);
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal(vasya, moved.PlayerId);
        Assert.Equal(LinearMap.StartId, moved.From);
        Assert.Equal("c10", moved.To);
        Assert.Equal(["c10"], moved.Path); // a transfer, not ten steps: no cells in between are entered
        Assert.Equal(MoveReason.AdminAdjustment, moved.Reason);
        Assert.Null(moved.RunId);
        Assert.Equal("c10", s.Player("Вася").CellId);
    }

    [Fact]
    public void Position_change_does_not_touch_points()
    {
        // Given Вася has points from a completion
        var s = RunningSeason().Roll("Вася").Start("Вася").Complete("Вася");
        var points = s.Player("Вася").Points;
        Assert.True(points > 0);

        // When the admin moves him back to c1
        s.Act(Adjust(s, "Вася", cellId: "c1"));

        // Then only the position changed
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PointsChanged>());
        Assert.Equal("c1", s.Player("Вася").CellId);
        Assert.Equal(points, s.Player("Вася").Points);
        Assert.Equal(["c1"], Assert.Single(s.LastEvents<PlayerMoved>()).Path);
    }

    [Fact]
    public void Points_change_does_not_touch_position()
    {
        var s = RunningSeason().Roll("Вася").Start("Вася").Complete("Вася");
        var cell = s.Player("Вася").CellId;

        s.Act(Adjust(s, "Вася", points: 20));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal(cell, s.Player("Вася").CellId);
    }

    [Fact]
    public void Moving_to_the_current_cell_changes_nothing()
    {
        var s = RunningSeason();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Adjust(x, "Вася", cellId: LinearMap.StartId)), RejectionCodes.NothingToChange);
    }

    [Fact]
    public void Moving_to_the_current_cell_along_with_points_writes_no_move()
    {
        var s = RunningSeason();

        s.Act(Adjust(s, "Вася", cellId: LinearMap.StartId, points: 2));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal(2, s.Player("Вася").Points);
    }

    [Fact]
    public void Unknown_cell_is_rejected()
    {
        var s = RunningSeason();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Adjust(x, "Вася", cellId: "c999", points: 3)), RejectionCodes.CellUnknown);
    }

    // ---- Everything at once ----

    [Fact]
    public void All_changes_in_one_command_each_get_an_event_and_one_comment()
    {
        var s = RunningSeason().Roll("Вася");
        var vasya = s.PlayerId("Вася");
        var offered = s.Player("Вася").Offer!.GameId;

        s.Act(Adjust(
            s, "Вася", cellId: "c4", points: 7, coins: -2,
            resources: [new ResourceDelta("tickets", 3)], discardOffer: true));

        ScenarioAssert.Accepted(s);
        Assert.Equal(6, s.Last.Events.Count);
        Assert.Equal([new PlayerAdjusted(vasya, Comment)], s.LastEvents<PlayerAdjusted>());
        Assert.Equal([new PointsChanged(vasya, 7, PointsReason.AdminAdjustment, null)], s.LastEvents<PointsChanged>());
        Assert.Equal([new CoinsChanged(vasya, -2, CoinsReason.AdminAdjustment, null)], s.LastEvents<CoinsChanged>());
        Assert.Equal([new ResourceChanged(vasya, "tickets", 3, ResourceReason.AdminAdjustment)], s.LastEvents<ResourceChanged>());
        Assert.Equal([new OfferDiscarded(vasya, offered)], s.LastEvents<OfferDiscarded>());
        Assert.Equal("c4", Assert.Single(s.LastEvents<PlayerMoved>()).To);

        var player = s.Player("Вася");
        Assert.Equal(("c4", 7, -2, 3, TurnPhase.Idle), (player.CellId, player.Points, player.Coins, player.Resources["tickets"], player.Phase));
        Assert.Null(player.Offer);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    // ---- Refusals ----

    [Fact]
    public void Adjustment_that_changes_nothing_is_rejected()
    {
        var s = RunningSeason();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Adjust(x, "Вася")), RejectionCodes.NothingToChange);
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(Adjust(x, "Вася", resources: [new ResourceDelta("tickets", 0)])), RejectionCodes.NothingToChange);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Adjustment_without_a_comment_is_rejected(string comment)
    {
        // The comment explains the change in the public log; the code is a stand-in until a dedicated one is agreed
        var s = RunningSeason();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Adjust(x, "Вася", comment: comment, points: 5)), RejectionCodes.CommentRequired);
    }

    [Fact]
    public void Unknown_player_is_rejected()
    {
        var s = RunningSeason();

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new AdjustPlayer(SequentialIds.Make(0x10000000, 0x99), Comment, PointsDelta: 5)),
            RejectionCodes.PlayerUnknown);
    }

    // ---- Dropping an offered game ----

    [Fact]
    public void Discarding_an_offer_returns_the_player_to_idle()
    {
        var s = RunningSeason().Roll("Вася");
        var vasya = s.PlayerId("Вася");
        var offered = s.Player("Вася").Offer!.GameId;

        s.Act(Adjust(s, "Вася", comment: "Колесо зависло", discardOffer: true));

        ScenarioAssert.Accepted(s);
        Assert.Equal(2, s.Last.Events.Count);
        Assert.Contains(new OfferDiscarded(vasya, offered), s.Last.Events);
        Assert.Contains(new PlayerAdjusted(vasya, "Колесо зависло"), s.Last.Events);
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
        Assert.Null(s.Player("Вася").Offer);
        Assert.Null(s.Player("Вася").ActiveRunId);
    }

    [Fact]
    public void Discarded_game_is_available_to_others_again()
    {
        // Given Вася was offered the only game, so Петя has nothing to roll
        var s = RunningSeason().Roll("Вася");
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Петя"), RejectionCodes.NoAvailableGames);

        // When the admin discards Вася's offer
        s.Act(Adjust(s, "Вася", discardOffer: true));
        ScenarioAssert.Accepted(s);

        // Then Петя rolls that game
        s.Roll("Петя");
        Assert.Equal(s.GameId("Silent Hill"), Assert.Single(s.LastEvents<GameRolled>()).GameId);
    }

    [Fact]
    public void Player_whose_offer_was_discarded_can_roll_again()
    {
        var s = RunningSeason().Roll("Вася");
        s.Act(Adjust(s, "Вася", discardOffer: true));
        ScenarioAssert.Accepted(s);

        s.Roll("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(TurnPhase.Rolling, s.Player("Вася").Phase);
    }

    [Fact]
    public void Discarding_an_offer_of_a_playing_player_is_rejected_as_busy()
    {
        // Resetting a run in progress is a drop or a tech reroll (C6), not an adjustment
        var s = RunningSeason().Roll("Вася").Start("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Adjust(x, "Вася", discardOffer: true)), RejectionCodes.PlayerBusy);
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(Adjust(x, "Вася", points: 5, cellId: "c3", discardOffer: true)), RejectionCodes.PlayerBusy);
    }

    [Fact]
    public void Discarding_an_offer_of_an_idle_player_alone_changes_nothing()
    {
        var s = RunningSeason();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Adjust(x, "Вася", discardOffer: true)), RejectionCodes.NothingToChange);
    }

    [Fact]
    public void Discard_flag_on_an_idle_player_is_ignored_when_something_else_changes()
    {
        var s = RunningSeason();

        s.Act(Adjust(s, "Вася", points: 4, discardOffer: true));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<OfferDiscarded>());
        Assert.Equal(4, s.Player("Вася").Points);
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
    }

    [Fact]
    public void Playing_player_can_get_points_and_keeps_the_run()
    {
        var s = RunningSeason().Roll("Вася").Start("Вася");
        var run = s.Player("Вася").ActiveRunId;

        s.Act(Adjust(s, "Вася", points: 5, cellId: "c2"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(TurnPhase.Playing, s.Player("Вася").Phase);
        Assert.Equal(run, s.Player("Вася").ActiveRunId);
        Assert.Equal(5, s.Player("Вася").Points);
        Assert.Equal("c2", s.Player("Вася").CellId);
    }

    // ---- Season status ----

    [Theory]
    [InlineData(SeasonStatus.Draft)]
    [InlineData(SeasonStatus.Closing)]
    public void Admin_can_adjust_while_the_season_is_a_draft_or_closing(SeasonStatus status)
    {
        // Closing: proofs are still reviewed and the admin may need to correct
        var s = SeasonIn(status);

        s.Act(Adjust(s, "Вася", points: 2));

        ScenarioAssert.Accepted(s);
        Assert.Equal(2, s.Player("Вася").Points);
    }

    [Theory]
    [InlineData(SeasonStatus.Finished)]
    [InlineData(SeasonStatus.Archived)]
    public void Admin_cannot_adjust_once_the_season_is_finished(SeasonStatus status)
    {
        // The results snapshot is taken at Finished (SE3); later edits would contradict it
        var s = SeasonIn(status);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Adjust(x, "Вася", points: 2)), RejectionCodes.SeasonClosed);
    }

    [Fact]
    public void Adjustment_before_the_season_exists_is_rejected()
    {
        var s = Scenario.New();

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new AdjustPlayer(SequentialIds.Make(0x10000000, 1), Comment, PointsDelta: 5)),
            RejectionCodes.SeasonNotCreated);
    }

    private static Scenario SeasonIn(SeasonStatus status)
    {
        var s = Scenario.New().AsDraft()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
            .WithPlayers("Вася");
        while (s.State.Status != status)
        {
            s.Act(new ChangeSeasonStatus(s.State.Status + 1));
            ScenarioAssert.Accepted(s);
        }

        return s;
    }
}

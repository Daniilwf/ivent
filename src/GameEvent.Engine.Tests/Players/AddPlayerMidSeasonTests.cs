using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Players;

/// <summary>
/// SE4: a player can join a draft or running season; by default on the start cell with zero points and coins,
/// or with the cell, points and coins the admin sets. Starting values are logged as changes of their own
/// (reasons StartingCell / StartingBalance), so points and coins stay the sum of logged changes (invariant 2).
/// </summary>
public class AddPlayerMidSeasonTests
{
    private static readonly Guid s_newPlayer = SequentialIds.Make(0x10000000, 0x50);
    private static readonly Guid s_newUser = SequentialIds.Make(0x40000000, 0x50);

    private static Scenario RunningSeason() =>
        Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithGame("Tetris", 3, "Horror")
            .WithPlayers("Вася");

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

    private static SeasonPlayer NewPlayer(Scenario s) => s.State.Players[s_newPlayer];

    [Theory]
    [InlineData(SeasonStatus.Draft)]
    [InlineData(SeasonStatus.Active)]
    public void Player_added_by_default_starts_on_start_with_zero_balance(SeasonStatus status)
    {
        // Given a draft or running season
        var s = SeasonIn(status);

        // When the admin adds a player without setting anything
        s.Act(new AddSeasonPlayer(s_newPlayer, s_newUser, "Лёша"));

        // Then one event: the player is on start, no zero changes are logged
        ScenarioAssert.Accepted(s);
        Assert.Equal([new SeasonPlayerAdded(s_newPlayer, s_newUser, "Лёша", LinearMap.StartId)], s.Last.Events);
        var player = NewPlayer(s);
        Assert.Equal(s_newUser, player.UserId);
        Assert.Equal("Лёша", player.Name);
        Assert.Equal(LinearMap.StartId, player.CellId);
        Assert.Equal(0, player.Points);
        Assert.Equal(0, player.Coins);
        Assert.Equal(ResourceBag.Empty, player.Resources);
        Assert.False(player.IsInactive);
        Assert.Equal(TurnPhase.Idle, player.Phase);
        Assert.Null(player.Offer);
        Assert.Null(player.ActiveRunId);
    }

    [Fact]
    public void Player_joining_mid_season_gets_the_cell_points_and_coins_the_admin_sets()
    {
        // Given a running season where Вася already played
        var s = RunningSeason().Roll("Вася").Start("Вася").Complete("Вася");

        // When the admin adds Лёша on c5 with 12 points and 7 coins
        s.Act(new AddSeasonPlayer(s_newPlayer, s_newUser, "Лёша", CellId: "c5", Points: 12, Coins: 7));

        // Then the player is added on start and each starting value is a logged change with its reason
        ScenarioAssert.Accepted(s);
        Assert.Equal(new SeasonPlayerAdded(s_newPlayer, s_newUser, "Лёша", LinearMap.StartId), s.Last.Events[0]);
        Assert.Equal(4, s.Last.Events.Count);

        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal(s_newPlayer, moved.PlayerId);
        Assert.Equal(LinearMap.StartId, moved.From);
        Assert.Equal("c5", moved.To);
        Assert.Equal(["c5"], moved.Path);
        Assert.Equal(MoveReason.StartingCell, moved.Reason);
        Assert.Null(moved.RunId);

        Assert.Equal([new PointsChanged(s_newPlayer, 12, PointsReason.StartingBalance, null)], s.LastEvents<PointsChanged>());
        Assert.Equal([new CoinsChanged(s_newPlayer, 7, CoinsReason.StartingBalance, null)], s.LastEvents<CoinsChanged>());

        var player = NewPlayer(s);
        Assert.Equal("c5", player.CellId);
        Assert.Equal(12, player.Points);
        Assert.Equal(7, player.Coins);
        Assert.Equal(TurnPhase.Idle, player.Phase);
    }

    [Fact]
    public void Starting_values_are_the_sum_of_logged_changes()
    {
        // Invariant 2 for a player who joins with a starting balance
        var s = RunningSeason();

        s.Act(new AddSeasonPlayer(s_newPlayer, s_newUser, "Лёша", CellId: "c3", Points: 9, Coins: 4));

        ScenarioAssert.Accepted(s);
        Assert.Equal(s.Log.OfType<PointsChanged>().Where(e => e.PlayerId == s_newPlayer).Sum(e => e.Delta), NewPlayer(s).Points);
        Assert.Equal(s.Log.OfType<CoinsChanged>().Where(e => e.PlayerId == s_newPlayer).Sum(e => e.Delta), NewPlayer(s).Coins);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void Explicit_start_cell_and_zero_balance_write_no_changes()
    {
        var s = RunningSeason();

        s.Act(new AddSeasonPlayer(s_newPlayer, s_newUser, "Лёша", CellId: LinearMap.StartId, Points: 0, Coins: 0));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new SeasonPlayerAdded(s_newPlayer, s_newUser, "Лёша", LinearMap.StartId)], s.Last.Events);
    }

    [Fact]
    public void Only_nonzero_starting_values_are_logged()
    {
        var s = RunningSeason();

        s.Act(new AddSeasonPlayer(s_newPlayer, s_newUser, "Лёша", Points: 5));

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [new SeasonPlayerAdded(s_newPlayer, s_newUser, "Лёша", LinearMap.StartId), new PointsChanged(s_newPlayer, 5, PointsReason.StartingBalance, null)],
            s.Last.Events);
        Assert.Equal(LinearMap.StartId, NewPlayer(s).CellId);
        Assert.Equal(0, NewPlayer(s).Coins);
    }

    [Fact]
    public void Starting_cell_gives_no_points_and_starting_points_give_no_cells()
    {
        // P1: position and points are independent measures
        var s = RunningSeason();

        s.Act(new AddSeasonPlayer(s_newPlayer, s_newUser, "Лёша", CellId: "c10"));
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PointsChanged>());
        Assert.Equal(0, NewPlayer(s).Points);
        Assert.Equal("c10", NewPlayer(s).CellId);

        var other = SequentialIds.Make(0x10000000, 0x51);
        s.Act(new AddSeasonPlayer(other, SequentialIds.Make(0x40000000, 0x51), "Оля", Points: 10));
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal(LinearMap.StartId, s.State.Players[other].CellId);
        Assert.Equal(10, s.State.Players[other].Points);
    }

    [Fact]
    public void Player_joining_mid_season_plays_on_from_the_given_cell_and_points()
    {
        // Given Лёша joined on c5 with 12 points; the only game is 3 hours long
        var s = Scenario.New().WithCategory("Puzzle").WithGame("Tetris", 3, "Puzzle").WithPlayers("Вася");
        s.Act(new AddSeasonPlayer(s_newPlayer, s_newUser, "Лёша", CellId: "c5", Points: 12));
        ScenarioAssert.Accepted(s);

        // When he rolls, starts and completes it on normal (one d4) with a 3
        s.Act(new RollGame(s_newPlayer));
        ScenarioAssert.Accepted(s);
        s.Act(new StartRun(s_newPlayer));
        ScenarioAssert.Accepted(s);
        s.NextRandom(3).Act(new CompleteRun(s_newPlayer, Difficulty.Normal));
        ScenarioAssert.Accepted(s);

        // Then both measures grow by the dice from where the admin put him
        Assert.Equal("c8", NewPlayer(s).CellId);
        Assert.Equal(15, NewPlayer(s).Points);
    }

    [Fact]
    public void Unknown_starting_cell_is_rejected()
    {
        var s = RunningSeason();

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new AddSeasonPlayer(s_newPlayer, s_newUser, "Лёша", CellId: "c999")), RejectionCodes.CellUnknown);
    }

    [Theory]
    [InlineData(SeasonStatus.Closing)]
    [InlineData(SeasonStatus.Finished)]
    [InlineData(SeasonStatus.Archived)]
    public void Player_cannot_join_once_the_season_is_closing(SeasonStatus status)
    {
        var s = SeasonIn(status);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new AddSeasonPlayer(s_newPlayer, s_newUser, "Лёша")), RejectionCodes.SeasonClosed);
    }

    [Fact]
    public void Same_user_cannot_join_twice_mid_season_even_with_a_starting_balance()
    {
        var s = RunningSeason();
        s.Act(new AddSeasonPlayer(s_newPlayer, s_newUser, "Лёша", CellId: "c5", Points: 3));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new AddSeasonPlayer(SequentialIds.Make(0x10000000, 0x52), s_newUser, "Лёша-2", CellId: "c7", Points: 1)),
            RejectionCodes.PlayerAlreadyAdded);
        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new AddSeasonPlayer(s_newPlayer, SequentialIds.Make(0x40000000, 0x52), "Лёша-3", Coins: 2)),
            RejectionCodes.PlayerAlreadyAdded);
    }
}

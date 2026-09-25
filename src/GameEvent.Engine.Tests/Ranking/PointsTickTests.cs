using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Ranking;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Finish.FinishSetup;

namespace GameEvent.Engine.Tests.Ranking;

/// <summary>
/// «Who reached the final score earlier» is counted by the log, not by the clock (P8; D-100): every non-zero
/// <see cref="PointsChanged"/> takes the next number of the season (<see cref="SeasonState.PointsChanges"/>), and its
/// player keeps the number of his last one (<see cref="SeasonPlayer.PointsTick"/>, 0 — never changed). A zero change
/// takes no number. The numbers come from folding the log: the event format does not change, a replay gives them back.
/// </summary>
public class PointsTickTests
{
    [Fact]
    public void Season_starts_with_no_points_changes()
    {
        var s = New();

        Assert.Equal(0, s.State.PointsChanges);
        Assert.All(s.State.Players.Values, p => Assert.Equal(0, p.PointsTick));
    }

    [Fact]
    public void Every_non_zero_points_change_takes_the_next_number()
    {
        var s = New();

        s.Act(new AdjustPlayer(s.PlayerId("Маша"), "Бонус", PointsDelta: 3));
        Assert.Equal((1, 1L), (s.State.PointsChanges, s.Player("Маша").PointsTick));

        Complete(s, "Вася", [1, 1]);
        Assert.Equal((2, 2L), (s.State.PointsChanges, s.Player("Вася").PointsTick));

        s.Act(new AdjustPlayer(s.PlayerId("Маша"), "Штраф", PointsDelta: -1));
        Assert.Equal((3, 3L), (s.State.PointsChanges, s.Player("Маша").PointsTick));

        // Петя never changed; Вася keeps the number of his last change
        Assert.Equal((0L, 2L), (s.Player("Петя").PointsTick, s.Player("Вася").PointsTick));
    }

    [Fact]
    public void Several_changes_in_one_command_take_one_number_each()
    {
        // Петя finishes second: the dice and the bonus are two changes — ticks 2 and 3, Петя keeps 3
        var s = New();
        FinishRun(s, "Вася");
        Assert.Equal(1, s.State.PointsChanges);

        FinishRun(s, "Петя");

        Assert.Equal(2, s.LastEvents<PointsChanged>().Count());
        Assert.Equal((3, 3L), (s.State.PointsChanges, s.Player("Петя").PointsTick));
    }

    [Fact]
    public void Changes_of_others_do_not_touch_a_players_tick()
    {
        var s = New();
        Complete(s, "Вася", [1, 1]);
        var vasya = s.Player("Вася").PointsTick;

        Complete(s, "Петя", [1, 1]);
        s.Act(new AdjustPlayer(s.PlayerId("Маша"), "Бонус", PointsDelta: 5));

        Assert.Equal(vasya, s.Player("Вася").PointsTick);
    }

    [Fact]
    public void Coins_and_other_changes_take_no_number()
    {
        var s = New();

        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Монетки", CoinsDelta: 5, CellId: "c1"));
        ScenarioAssert.Accepted(s);

        Assert.Equal((0, 0L), (s.State.PointsChanges, s.Player("Вася").PointsTick));
    }

    [Fact]
    public void Starting_points_of_a_player_added_mid_season_take_a_number()
    {
        var s = New();
        Complete(s, "Вася", [1, 1]);
        var late = SequentialIds.Make(0x10000000, 0x77);

        s.Act(new AddSeasonPlayer(late, SequentialIds.Make(0x40000000, 0x77), "Лёша", Points: 3));
        ScenarioAssert.Accepted(s);

        Assert.Equal((2, 2L), (s.State.PointsChanges, s.State.Players[late].PointsTick));
    }

    [Fact]
    public void Zero_points_change_takes_no_number()
    {
        // A zero change is never written by the engine, but the fold must not number it (a completion of 0 points)
        var s = New();
        Complete(s, "Вася", [1, 1]);
        var vasya = s.PlayerId("Вася");

        var replayed = SeasonEngine.Replay([.. s.Log, new PointsChanged(vasya, 0, PointsReason.CompletionRoll, null)]);

        Assert.Equal(1, replayed.PointsChanges);
        Assert.Equal(1L, replayed.Players[vasya].PointsTick);
    }

    [Fact]
    public void Replaying_the_log_gives_the_same_numbers()
    {
        var s = New();
        s.Act(new AdjustPlayer(s.PlayerId("Маша"), "Бонус", PointsDelta: 3));
        var vasyaRun = FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        Reject(s, vasyaRun);
        ScenarioAssert.Accepted(s);

        var replayed = SeasonEngine.Replay(s.Log);

        Assert.Equal(s.State, replayed);
        Assert.Equal(s.State.PointsChanges, replayed.PointsChanges);
        Assert.Equal(s.State.Players.Values.Select(p => p.PointsTick), replayed.Players.Values.Select(p => p.PointsTick));
        Assert.Equal(s.Log.OfType<PointsChanged>().Count(e => e.Delta != 0), s.State.PointsChanges);
    }

    [Fact]
    public void Entries_carry_the_tick()
    {
        var s = New();
        Complete(s, "Вася", [1, 1]);

        var entry = Leaderboard.Entries(s.State).Single(e => e.PlayerId == s.PlayerId("Вася"));

        Assert.Equal(s.Player("Вася").PointsTick, entry.PointsTick);
        Assert.Equal(1L, entry.PointsTick);
        Assert.Equal("c2", entry.CellId);
        Assert.NotEqual(LinearMap.FinishId, entry.CellId);
    }

    [Fact]
    public void Numbers_survive_the_json_round_trip_of_the_log()
    {
        // The numbers are not in the events: a log read back from JSON folds to the same numbers
        var s = New();
        Complete(s, "Вася", [1, 1]);
        s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Бонус", PointsDelta: 2));

        var roundTripped = s.Log.Select(e => EventCodec.Decode(EventCodec.Encode(e))).ToList();

        Assert.Equal(s.State, SeasonEngine.Replay(roundTripped));
    }
}

using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Ranking;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Finish.FinishSetup;

namespace GameEvent.Engine.Tests.Ranking;

/// <summary>
/// Tiebreakers (P8; SPEC «Тайбрейк: число пройденных игр, затем кто раньше набрал итоговые очки»; D-100). Among the
/// players who are not first, equal points are broken by <c>ranking.tiebreakers</c> in their order:
/// <c>completedRuns</c> — completed and not rejected runs, more is better; <c>earliestFinalScore</c> — the lower
/// <see cref="RankingEntry.PointsTick"/> (the number of the season's points change that set the current points) is
/// better. A full tie shares the place and the next place skips (1, 2, 2, 4); rows inside a tie go by player id.
/// </summary>
public class TiebreakTests
{
    private static readonly MapGraph s_map = LinearMap.Generate(4);

    private static readonly RankingRules s_default = Rules(Tiebreaker.CompletedRuns, Tiebreaker.EarliestFinalScore);

    private static RankingRules Rules(params Tiebreaker[] order) => new() { Tiebreakers = [.. order] };

    private static Guid Id(int n) => SequentialIds.Make(0x10000000, n);

    private static RankingEntry E(int n, int points, int runs = 0, long tick = 0, int? finishOrder = null, bool frozen = false) =>
        new(Id(n), points, LinearMap.StartId, finishOrder, frozen, runs, tick);

    private static List<(Guid Id, int Place)> Places(IEnumerable<RankingEntry> entries, RankingRules? rules = null) =>
        [.. Leaderboard.Rank(entries, rules ?? s_default, s_map).Select(r => (r.PlayerId, r.Place))];

    // ---- completedRuns ----

    [Fact]
    public void Equal_points_more_completed_runs_is_higher()
    {
        // Same points; 1 has one run, 2 has three
        Assert.Equal([(Id(2), 1), (Id(1), 2)], Places([E(1, 10, runs: 1, tick: 1), E(2, 10, runs: 3, tick: 2)]));
    }

    [Fact]
    public void Points_come_before_the_tiebreakers()
    {
        // 1 has more points but fewer runs and a later tick
        Assert.Equal([(Id(1), 1), (Id(2), 2)], Places([E(1, 11, runs: 0, tick: 9), E(2, 10, runs: 5, tick: 1)]));
    }

    [Fact]
    public void Rejected_runs_do_not_count_as_completed()
    {
        // Вася: runs A (1 + 1) and B (1 + 1), B rejected — 2 points, 1 run. Петя: one run 1 + 1 — 2 points, 1 run, and
        // his points were set before Вася's reject: Петя is above (were B counted, Вася would be)
        var s = New();
        Complete(s, "Вася", [1, 1]);
        Complete(s, "Петя", [1, 1]);
        var (runB, _) = Complete(s, "Вася", [1, 1]);
        Reject(s, runB);
        ScenarioAssert.Accepted(s);

        var entries = Leaderboard.Entries(s.State).ToDictionary(e => e.PlayerId);
        Assert.Equal((2, 1), (entries[s.PlayerId("Вася")].Points, entries[s.PlayerId("Вася")].CompletedRuns));
        Assert.Equal((2, 1), (entries[s.PlayerId("Петя")].Points, entries[s.PlayerId("Петя")].CompletedRuns));

        var rows = Leaderboard.Build(s.State);
        Assert.Equal([s.PlayerId("Петя"), s.PlayerId("Вася")], rows.Take(2).Select(r => r.PlayerId));
        Assert.Equal([1, 2], rows.Take(2).Select(r => r.Place));
    }

    /// <summary>
    /// A season where a drop costs no points and no cells (only the bad event), so the runs that do not count can be
    /// checked against a tie: Петя (the run in question) and Маша (nothing) both have 0 points and have never changed them.
    /// </summary>
    private static Scenario FreeDrops() =>
        New(ruleset: r => r with { Drop = r.Drop with { AffectsPoints = false, AffectsPosition = false } });

    private static void AssertPetyaAndMashaStillTied(Scenario s)
    {
        var entries = Leaderboard.Entries(s.State).ToDictionary(e => e.PlayerId);
        Assert.Equal(0, entries[s.PlayerId("Петя")].CompletedRuns);
        Assert.Equal((0, 0L), (entries[s.PlayerId("Петя")].Points, entries[s.PlayerId("Петя")].PointsTick));

        var places = Leaderboard.Build(s.State).ToDictionary(r => r.PlayerId, r => r.Place);
        Assert.Equal(places[s.PlayerId("Маша")], places[s.PlayerId("Петя")]);
    }

    [Fact]
    public void Run_being_played_does_not_count()
    {
        var s = FreeDrops();
        Complete(s, "Вася", [1, 1]);
        Playing(s, "Петя");

        Assert.Equal(1, Leaderboard.Entries(s.State).Single(e => e.PlayerId == s.PlayerId("Вася")).CompletedRuns);
        AssertPetyaAndMashaStillTied(s);
    }

    [Fact]
    public void Dropped_run_does_not_count()
    {
        var s = FreeDrops();
        Playing(s, "Петя");

        s.NextRandom(1, 1).Act(new DropRun(s.PlayerId("Петя")));

        ScenarioAssert.Accepted(s);
        AssertPetyaAndMashaStillTied(s);
    }

    [Fact]
    public void Tech_rerolled_run_does_not_count()
    {
        var s = FreeDrops();
        Playing(s, "Петя");

        s.Act(new TechReroll(s.PlayerId("Петя"), TechRerollReason.DoesNotLaunch, null));

        ScenarioAssert.Accepted(s);
        AssertPetyaAndMashaStillTied(s);
    }

    [Fact]
    public void Tech_reroll_turned_into_a_drop_does_not_count()
    {
        var s = FreeDrops();
        var runId = Playing(s, "Петя");
        s.Act(new TechReroll(s.PlayerId("Петя"), TechRerollReason.DoesNotLaunch, null));
        ScenarioAssert.Accepted(s);

        s.NextRandom(1, 1).Act(new ConvertTechRerollToDrop(runId, "это был дроп"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(RunStatus.Dropped, s.State.Runs[runId].Status);
        AssertPetyaAndMashaStillTied(s);
    }

    // ---- earliestFinalScore ----

    [Fact]
    public void Equal_points_and_runs_the_earlier_final_score_is_higher()
    {
        Assert.Equal([(Id(2), 1), (Id(1), 2)], Places([E(1, 10, runs: 2, tick: 7), E(2, 10, runs: 2, tick: 3)]));
    }

    [Fact]
    public void Never_changed_points_are_the_earliest()
    {
        // Tick 0 — the points never changed — is earlier than any change (both at 0 points, e.g. +3 then −3 for 2)
        Assert.Equal([(Id(1), 1), (Id(2), 2)], Places([E(1, 0, tick: 0), E(2, 0, tick: 4)]));
    }

    [Fact]
    public void Earliest_final_score_in_a_season()
    {
        // Вася and Петя reach 2 points each with one run; Петя's completion comes first, so his points were set earlier
        var s = New();
        Complete(s, "Петя", [1, 1]);
        Complete(s, "Вася", [1, 1]);

        var rows = Leaderboard.Build(s.State);

        Assert.Equal([s.PlayerId("Петя"), s.PlayerId("Вася")], rows.Take(2).Select(r => r.PlayerId));
        Assert.True(s.Player("Петя").PointsTick < s.Player("Вася").PointsTick);
    }

    // ---- The order of the tiebreakers comes from the ruleset ----

    [Fact]
    public void Tiebreakers_apply_in_the_order_of_the_ruleset()
    {
        // 1: more runs but a later tick; 2: fewer runs but an earlier tick
        RankingEntry[] entries = [E(1, 10, runs: 3, tick: 9), E(2, 10, runs: 1, tick: 2)];

        Assert.Equal([(Id(1), 1), (Id(2), 2)], Places(entries, Rules(Tiebreaker.CompletedRuns, Tiebreaker.EarliestFinalScore)));
        Assert.Equal([(Id(2), 1), (Id(1), 2)], Places(entries, Rules(Tiebreaker.EarliestFinalScore, Tiebreaker.CompletedRuns)));
    }

    [Fact]
    public void Only_the_listed_tiebreakers_apply()
    {
        // Only completedRuns: equal runs and different ticks are a tie
        RankingEntry[] entries = [E(1, 10, runs: 2, tick: 9), E(2, 10, runs: 2, tick: 2)];

        Assert.Equal([(Id(1), 1), (Id(2), 1)], Places(entries, Rules(Tiebreaker.CompletedRuns)));
    }

    [Fact]
    public void Without_tiebreakers_equal_points_share_the_place()
    {
        RankingEntry[] entries = [E(1, 10, runs: 5, tick: 9), E(2, 10, runs: 1, tick: 2), E(3, 4)];

        Assert.Equal([(Id(1), 1), (Id(2), 1), (Id(3), 3)], Places(entries, Rules()));
    }

    [Fact]
    public void Ruleset_tiebreakers_follow_a_change_of_the_rules()
    {
        // Петя gets 2 points from the admin first; Вася then reaches 2 by one run (c2, not a finish). By default (runs
        // first) Вася is above; with earliestFinalScore first Петя is; with no tiebreakers they share the place
        var s = New();
        s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Бонус", PointsDelta: 2));
        ScenarioAssert.Accepted(s);
        Complete(s, "Вася", [1, 1]);

        Assert.Equal([s.PlayerId("Вася"), s.PlayerId("Петя")], Leaderboard.Build(s.State).Take(2).Select(r => r.PlayerId));

        s.WithRuleset(r => r with { Ranking = new RankingRules { Tiebreakers = [Tiebreaker.EarliestFinalScore, Tiebreaker.CompletedRuns] } });
        Assert.Equal([Tiebreaker.EarliestFinalScore, Tiebreaker.CompletedRuns], s.State.Rules.Ranking.Tiebreakers);
        Assert.Equal([s.PlayerId("Петя"), s.PlayerId("Вася")], Leaderboard.Build(s.State).Take(2).Select(r => r.PlayerId));

        s.WithRuleset(r => r with { Ranking = new RankingRules { Tiebreakers = [] } });
        Assert.Equal([1, 1], Leaderboard.Build(s.State).Take(2).Select(r => r.Place));
    }

    // ---- Shared places ----

    [Fact]
    public void Full_tie_shares_the_place_and_the_next_place_skips()
    {
        // 10; 5 and 5 (same runs and tick); 1 → places 1, 2, 2, 4
        RankingEntry[] entries = [E(1, 10), E(2, 5, runs: 1, tick: 3), E(3, 5, runs: 1, tick: 3), E(4, 1)];

        Assert.Equal([1, 2, 2, 4], Places(entries).Select(x => x.Place));
    }

    [Fact]
    public void Rows_inside_a_tie_go_by_player_id()
    {
        // Given in reverse id order, the tied rows come out by id
        RankingEntry[] entries = [E(5, 3), E(3, 3), E(4, 3), E(1, 9)];

        Assert.Equal([(Id(1), 1), (Id(3), 2), (Id(4), 2), (Id(5), 2)], Places(entries));
    }

    [Fact]
    public void Three_way_tie_at_the_top_without_a_finisher()
    {
        RankingEntry[] entries = [E(3, 7), E(2, 7), E(1, 7), E(4, 6)];

        Assert.Equal([(Id(1), 1), (Id(2), 1), (Id(3), 1), (Id(4), 4)], Places(entries));
    }

    [Fact]
    public void Rank_does_not_depend_on_the_order_of_the_entries()
    {
        RankingEntry[] entries = [E(1, 3, runs: 1, tick: 5), E(2, 3, runs: 1, tick: 4), E(3, 8), E(4, 3, runs: 2, tick: 9), E(5, 1, finishOrder: 2)];

        Assert.Equal(
            Leaderboard.Rank(entries, s_default, s_map),
            Leaderboard.Rank(Enumerable.Reverse(entries), s_default, s_map));
    }

    // ---- The first and the tiebreakers ----

    [Fact]
    public void First_finisher_is_alone_at_place_one_and_the_others_start_at_two()
    {
        // 2 is first with fewer points; 1 and 3 tie for place 2
        RankingEntry[] entries = [E(1, 10), E(2, 1, finishOrder: 1), E(3, 10)];

        var rows = Leaderboard.Rank(entries, s_default, s_map);

        Assert.Equal([(Id(2), 1), (Id(1), 2), (Id(3), 2)], rows.Select(r => (r.PlayerId, r.Place)));
        Assert.Equal([true, false, false], rows.Select(r => r.IsFirst));
    }

    [Fact]
    public void Lowest_finish_order_is_first_even_when_it_is_not_one()
    {
        // Orders 2 and 5 stand (1 was revoked): order 2 is first; order 5 is ranked by points below a non-finisher
        RankingEntry[] entries = [E(1, 3, finishOrder: 5), E(2, 0, finishOrder: 2, frozen: true), E(3, 4)];

        var rows = Leaderboard.Rank(entries, s_default, s_map);

        Assert.Equal([Id(2), Id(3), Id(1)], rows.Select(r => r.PlayerId));
        Assert.Equal(new LeaderboardRow(Id(2), 1, 0, 4, true, false), rows[0]);
    }

    [Fact]
    public void Provisional_is_the_unfrozen_first_only()
    {
        RankingEntry[] entries = [E(1, 3, finishOrder: 1), E(2, 20, finishOrder: 2)];

        var rows = Leaderboard.Rank(entries, s_default, s_map);

        Assert.Equal([(true, true), (false, false)], rows.Select(r => (r.IsFirst, r.Provisional)));
    }

    [Fact]
    public void Default_tiebreakers_are_completed_runs_then_earliest_final_score()
    {
        Assert.Equal(
            (EquatableArray<Tiebreaker>)[Tiebreaker.CompletedRuns, Tiebreaker.EarliestFinalScore],
            TestRuleset.Create().Ranking.Tiebreakers);
    }
}

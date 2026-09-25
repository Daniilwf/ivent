using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Ranking;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Finish.FinishSetup;

namespace GameEvent.Engine.Tests.Ranking;

/// <summary>
/// Places and the leaderboard (P5, P7, P11; SPEC «Первое место», «Остальные места», «Если до финиша никто не дошёл»,
/// «Лидерборд»; D-100). Place 1 is the standing finisher with the lowest order — on top whatever his points, marked
/// provisional until frozen. Everybody else, other finishers and inactive players included, goes by points. Each row
/// shows the points and the cells to the finish (0 on it). The map of the scenarios is 4 steps (start, c1, c2, c3,
/// finish); 3 + 1 from the start reaches the finish.
/// </summary>
public class LeaderboardTests
{
    private static LeaderboardRow Row(Scenario s, string name) =>
        Leaderboard.Build(s.State).Single(r => r.PlayerId == s.PlayerId(name));

    private static List<Guid> Order(Scenario s) => [.. Leaderboard.Build(s.State).Select(r => r.PlayerId)];

    [Fact]
    public void Others_ranked_by_points()
    {
        // Вася 1 + 1 → c2 (2 points), Петя 1 + 2 → c3 (3 points), Маша nothing
        var s = New();
        Complete(s, "Вася", [1, 1]);
        Complete(s, "Петя", [1, 2]);

        var rows = Leaderboard.Build(s.State);

        Assert.Equal(
            [
                new LeaderboardRow(s.PlayerId("Петя"), 1, 3, 1, false, false),
                new LeaderboardRow(s.PlayerId("Вася"), 2, 2, 2, false, false),
                new LeaderboardRow(s.PlayerId("Маша"), 3, 0, 4, false, false),
            ],
            rows);
    }

    [Fact]
    public void Points_not_position_decide_the_place()
    {
        // The admin moves Маша to c3 (closest to the finish) with no points; Вася has 2 points on c2
        var s = New();
        Complete(s, "Вася", [1, 1]);
        s.Act(new AdjustPlayer(s.PlayerId("Маша"), "Перенос", CellId: "c3"));
        ScenarioAssert.Accepted(s);

        // Маша ties with Петя at 0 points (no runs, never changed points): they share place 2
        Assert.Equal(s.PlayerId("Вася"), Order(s)[0]);
        Assert.Equal((1, 2), (Row(s, "Маша").CellsToFinish, Row(s, "Маша").Place));
        Assert.Equal(2, Row(s, "Петя").Place);
    }

    [Fact]
    public void No_finisher_all_by_points()
    {
        // Nobody has a standing finish: nobody is first, the places go by points only
        var s = New();
        s.Act(new AdjustPlayer(s.PlayerId("Маша"), "Бонус", PointsDelta: 7));
        Complete(s, "Вася", [2, 1]);

        var rows = Leaderboard.Build(s.State);

        Assert.Equal([s.PlayerId("Маша"), s.PlayerId("Вася"), s.PlayerId("Петя")], rows.Select(r => r.PlayerId));
        Assert.Equal([1, 2, 3], rows.Select(r => r.Place));
        Assert.All(rows, r => Assert.False(r.IsFirst));
        Assert.All(rows, r => Assert.False(r.Provisional));
    }

    [Fact]
    public void First_finisher_is_on_top_regardless_of_points()
    {
        // Вася finishes with 4 points; Петя has 100 from the admin
        var s = New();
        s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Бонус", PointsDelta: 100));
        FinishRun(s, "Вася");

        var rows = Leaderboard.Build(s.State);

        Assert.Equal(new LeaderboardRow(s.PlayerId("Вася"), 1, 4, 0, true, true), rows[0]);
        Assert.Equal(new LeaderboardRow(s.PlayerId("Петя"), 2, 100, 4, false, false), rows[1]);
        Assert.Equal(3, rows[2].Place);
    }

    [Fact]
    public void First_finisher_is_provisional_until_frozen()
    {
        var s = New();
        var runId = FinishRun(s, "Вася");
        Assert.True(Row(s, "Вася").Provisional);

        Approve(s, runId);
        ScenarioAssert.Accepted(s);

        Assert.Equal((1, true, false), (Row(s, "Вася").Place, Row(s, "Вася").IsFirst, Row(s, "Вася").Provisional));
    }

    [Fact]
    public void Without_required_approval_the_first_is_never_provisional()
    {
        var s = New(ruleset: r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } });

        FinishRun(s, "Вася");

        Assert.Equal((1, true, false), (Row(s, "Вася").Place, Row(s, "Вася").IsFirst, Row(s, "Вася").Provisional));
    }

    [Fact]
    public void Later_finishers_are_ranked_by_points_not_by_finish_order()
    {
        // Вася 1st; Петя finishes 2nd (4 + 10 = 14), Маша 3rd (4 + 8 = 12) and plays on: +8 → 20
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        FinishRun(s, "Маша");
        Complete(s, "Маша", [4, 4]);

        var rows = Leaderboard.Build(s.State);

        Assert.Equal([s.PlayerId("Вася"), s.PlayerId("Маша"), s.PlayerId("Петя")], rows.Select(r => r.PlayerId));
        Assert.Equal([1, 2, 3], rows.Select(r => r.Place));
        Assert.Equal([true, false, false], rows.Select(r => r.IsFirst));
        Assert.All(rows, r => Assert.Equal(0, r.CellsToFinish));
    }

    [Fact]
    public void Later_finisher_is_not_above_a_player_with_more_points_who_has_not_finished()
    {
        // Петя finishes 2nd with 14 points; Маша has not finished but has 30
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        s.Act(new AdjustPlayer(s.PlayerId("Маша"), "Бонус", PointsDelta: 30));

        Assert.Equal([s.PlayerId("Вася"), s.PlayerId("Маша"), s.PlayerId("Петя")], Order(s));
    }

    [Fact]
    public void Revoked_finish_loses_place_one_and_the_next_finisher_becomes_first()
    {
        var s = New();
        var vasyaRun = FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Бонус", PointsDelta: 50));

        Reject(s, vasyaRun);
        ScenarioAssert.Accepted(s);

        // Петя is first now (his bonus went: 4 points); Вася (50, back on the start) is ranked by points
        var rows = Leaderboard.Build(s.State);
        Assert.Equal(new LeaderboardRow(s.PlayerId("Петя"), 1, 4, 0, true, true), rows[0]);
        Assert.Equal(new LeaderboardRow(s.PlayerId("Вася"), 2, 50, 4, false, false), rows[1]);
    }

    [Fact]
    public void Inactive_players_are_ranked_too()
    {
        var s = New();
        s.Act(new AdjustPlayer(s.PlayerId("Маша"), "Бонус", PointsDelta: 5));
        s.Act(new SetPlayerInactive(s.PlayerId("Маша"), true));
        ScenarioAssert.Accepted(s);

        Assert.Equal(s.State.Players.Count, Leaderboard.Build(s.State).Count);
        Assert.Equal(1, Row(s, "Маша").Place);
    }

    [Fact]
    public void Everybody_has_one_row()
    {
        var s = New(players: 5);
        FinishRun(s, "Коля");

        var rows = Leaderboard.Build(s.State);

        Assert.Equal(s.State.Players.Keys.Order(), rows.Select(r => r.PlayerId).Order());
    }

    [Fact]
    public void Negative_points_rank_below_zero()
    {
        var s = New();
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Штраф", PointsDelta: -3));

        Assert.Equal(s.PlayerId("Вася"), Order(s)[^1]);
        Assert.Equal(-3, Row(s, "Вася").Points);
    }

    [Fact]
    public void Empty_season_has_an_empty_leaderboard()
    {
        var s = Scenario.New();
        s.WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers();

        Assert.Empty(Leaderboard.Build(s.State));
    }

    // ---- Build is Rank over Entries ----

    [Fact]
    public void Build_is_rank_over_the_entries_of_the_state()
    {
        var s = New();
        FinishRun(s, "Вася");
        Complete(s, "Петя", [1, 2]);
        Complete(s, "Маша", [2, 1]);

        Assert.Equal(Leaderboard.Rank(Leaderboard.Entries(s.State), s.State.Rules.Ranking, s.State.Map), Leaderboard.Build(s.State));
    }

    [Fact]
    public void Entries_carry_points_cell_finish_frozen_runs_and_tick()
    {
        var s = New();
        var runId = FinishRun(s, "Вася");
        Approve(s, runId);
        Complete(s, "Петя", [1, 1]);

        var entries = Leaderboard.Entries(s.State).ToDictionary(e => e.PlayerId);

        var vasya = s.Player("Вася");
        Assert.Equal(new RankingEntry(vasya.PlayerId, 4, LinearMap.FinishId, 1, true, 1, vasya.PointsTick), entries[vasya.PlayerId]);
        var petya = s.Player("Петя");
        Assert.Equal(new RankingEntry(petya.PlayerId, 2, "c2", null, false, 1, petya.PointsTick), entries[petya.PlayerId]);
        var masha = s.Player("Маша");
        Assert.Equal(new RankingEntry(masha.PlayerId, 0, LinearMap.StartId, null, false, 0, 0), entries[masha.PlayerId]);
    }

    // ---- Cells to the finish on the linear map ----

    [Fact]
    public void Cells_to_finish_on_the_linear_map()
    {
        var map = LinearMap.Generate(4);

        var cells = Leaderboard.CellsToFinish(map);

        Assert.Equal(
            new Dictionary<string, int> { [LinearMap.StartId] = 4, ["c1"] = 3, ["c2"] = 2, ["c3"] = 1, [LinearMap.FinishId] = 0 },
            cells.OrderBy(x => x.Key).ToDictionary());
    }

    [Fact]
    public void Rank_takes_the_cells_to_finish_from_the_map()
    {
        var map = LinearMap.Generate(4);
        var rules = new RankingRules { Tiebreakers = [Tiebreaker.CompletedRuns, Tiebreaker.EarliestFinalScore] };
        var id = Guid.Parse("10000000-0000-0000-0000-000000000001");

        var row = Assert.Single(Leaderboard.Rank([new RankingEntry(id, 0, "c1", null, false, 0, 0)], rules, map));

        Assert.Equal(new LeaderboardRow(id, 1, 0, 3, false, false), row);
    }
}

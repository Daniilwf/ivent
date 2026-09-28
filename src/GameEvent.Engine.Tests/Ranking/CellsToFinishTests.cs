using GameEvent.Engine.Map;
using GameEvent.Engine.Ranking;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Tests.Ranking;

/// <summary>
/// «Клеток до финиша» on a graph (P11; D-100; the map is a graph from day one): the fewest
/// forward steps along the edges from a cell to any finish cell; 0 on a finish; cells that cannot reach one are absent,
/// and a player on such a cell has no value in the leaderboard.
/// </summary>
public class CellsToFinishTests
{
    //   start → a → finish
    //     ↓           ↑
    //     b → c ——————┘         d → e (a dead end: no way to the finish)
    //     ↓
    //     g → h → i → j → finish2
    private static MapGraph Branching() =>
        new(
            [
                new Cell("start", CellType.Start),
                new Cell("a", CellType.Empty),
                new Cell("b", CellType.Empty),
                new Cell("c", CellType.Empty),
                new Cell("d", CellType.Empty),
                new Cell("e", CellType.Empty),
                new Cell("g", CellType.Empty),
                new Cell("h", CellType.Empty),
                new Cell("i", CellType.Empty),
                new Cell("j", CellType.Empty),
                new Cell("finish", CellType.Finish),
                new Cell("finish2", CellType.Finish),
            ],
            [
                new Edge("start", "a", true, false),
                new Edge("start", "b", false, false),
                new Edge("a", "finish", true, true),
                new Edge("b", "c", true, true),
                new Edge("c", "finish", true, false),
                new Edge("b", "g", false, false),
                new Edge("g", "h", true, true),
                new Edge("h", "i", true, true),
                new Edge("i", "j", true, true),
                new Edge("j", "finish2", true, true),
                new Edge("d", "e", true, true),
            ]);

    [Fact]
    public void Fewest_forward_steps_to_any_finish()
    {
        var cells = Leaderboard.CellsToFinish(Branching());

        Assert.Equal(
            new Dictionary<string, int>
            {
                ["finish"] = 0,
                ["finish2"] = 0,
                ["a"] = 1,
                ["c"] = 1,
                ["j"] = 1,
                ["i"] = 2,
                ["b"] = 2,
                ["h"] = 3,
                ["g"] = 4,
                ["start"] = 2,
            }.OrderBy(x => x.Key),
            cells.OrderBy(x => x.Key));
    }

    [Fact]
    public void Cells_that_cannot_reach_a_finish_are_absent()
    {
        var cells = Leaderboard.CellsToFinish(Branching());

        Assert.False(cells.ContainsKey("d"));
        Assert.False(cells.ContainsKey("e"));
    }

    [Fact]
    public void Steps_go_forward_only()
    {
        // finish → x is an edge out of the finish; x cannot go on, so x is absent, and the finish is still 0
        var map = new MapGraph(
            [new Cell("start", CellType.Start), new Cell("finish", CellType.Finish), new Cell("x", CellType.Empty)],
            [new Edge("start", "finish", true, true), new Edge("finish", "x", true, true)]);

        var cells = Leaderboard.CellsToFinish(map);

        Assert.Equal((0, 1), (cells["finish"], cells["start"]));
        Assert.False(cells.ContainsKey("x"));
    }

    [Fact]
    public void Cycles_do_not_loop()
    {
        // start ⇄ a, a → finish
        var map = new MapGraph(
            [new Cell("start", CellType.Start), new Cell("a", CellType.Empty), new Cell("finish", CellType.Finish)],
            [new Edge("start", "a", true, true), new Edge("a", "start", false, false), new Edge("a", "finish", true, true)]);

        var cells = Leaderboard.CellsToFinish(map);

        Assert.Equal((2, 1, 0), (cells["start"], cells["a"], cells["finish"]));
    }

    [Fact]
    public void Player_on_an_unreachable_cell_has_no_cells_to_finish()
    {
        var rules = new RankingRules { Tiebreakers = [] };
        var id = Guid.Parse("10000000-0000-0000-0000-000000000001");

        var row = Assert.Single(Leaderboard.Rank([new RankingEntry(id, 5, "d", null, false, 0, 0)], rules, Branching()));

        Assert.Null(row.CellsToFinish);
    }

    [Fact]
    public void Player_on_a_branch_gets_the_shortest_way()
    {
        var rules = new RankingRules { Tiebreakers = [] };
        var id = Guid.Parse("10000000-0000-0000-0000-000000000001");

        var row = Assert.Single(Leaderboard.Rank([new RankingEntry(id, 5, "b", null, false, 0, 0)], rules, Branching()));

        Assert.Equal(2, row.CellsToFinish);
    }
}

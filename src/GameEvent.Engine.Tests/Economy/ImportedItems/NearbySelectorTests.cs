using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy.ImportedItems;

/// <summary>
/// Step 8 of the import queue (EC47, D-480): the selectors <c>withinCells</c> (everyone at most N cells away along the
/// map, those on the user's cell with <c>includeSameCell</c>) and <c>nearestBehind</c> (the closest player behind, all of
/// them on a tie); an action aimed at the user runs once per use, however many targets.
/// </summary>
public class NearbySelectorTests
{
    private static Scenario Season(params string[] players)
    {
        var s = EconomyScenario.New()
            .WithRuleset(r => r with { Effects = r.Effects with { Reflect = new CoinReflect { CoinsAtMost = -30, Multiplier = 2 } } })
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror")
            .WithPlayers(players.Length == 0 ? ["Вася", "Петя", "Коля", "Женя"] : players);
        s.WithContent(EconomyScenario.RepositoryPack());
        foreach (var name in players.Length == 0 ? ["Вася", "Петя", "Коля", "Женя"] : players)
        {
            s.WithCoins(name, 20);
        }

        return s;
    }

    [Theory]
    [InlineData("spear", 5)]
    [InlineData("better-spear", 15)]
    [InlineData("rare-spear", 10)]
    [InlineData("epic-spear", 20)]
    public void A_spear_pushes_back_everyone_within_its_reach_and_takes_a_coin_each(string spear, int reach)
    {
        var s = Season().At("Вася", "c30").At("Петя", "c30").At("Коля", $"c{30 + reach}").At("Женя", $"c{30 - reach - 1}");
        s.Give("Вася", spear);

        s.Used("Вася", spear);

        // Петя on the same cell and Коля at the edge of the reach; Женя one cell too far
        Assert.Equal(("c29", $"c{29 + reach}", $"c{29 - reach}"), (s.Player("Петя").CellId, s.Player("Коля").CellId, s.Player("Женя").CellId));
        Assert.Equal((19, 19, 20, 20), (s.Player("Петя").Coins, s.Player("Коля").Coins, s.Player("Женя").Coins, s.Player("Вася").Coins));
        Assert.Equal("c30", s.Player("Вася").CellId);
    }

    [Fact]
    public void A_spear_with_nobody_in_reach_is_refused()
    {
        var s = Season().At("Вася", "c30").At("Петя", "c40").At("Коля", "c50").At("Женя", "c10");
        var spear = s.Give("Вася", "spear");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", spear), RejectionCodes.ItemNoTarget);
    }

    [Fact]
    public void A_spear_counts_cells_along_the_map_not_the_distance_to_the_finish()
    {
        // Two branches of six cells: m3 and s3 are as far from the finish, but six cells apart along the map
        var map = MapBuilder.New()
            .Path("start", "fk", "m1", "m2", "m3", "m4", "m5", "m6", "j", "finish")
            .Path("fk", "s1", "s2", "s3", "s4", "s5", "s6", "j")
            .Build();
        var s = EconomyScenario.New().WithMap(map).WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя");
        s.WithContent(EconomyScenario.RepositoryPack());
        var spear = s.At("Вася", "m3").At("Петя", "s3").Give("Вася", "spear");
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", spear), RejectionCodes.ItemNoTarget);

        // On s2 he is five cells away (m3, m2, m1, fk, s1, s2): within the reach of 5
        s.At("Петя", "s2");
        s.Use("Вася", spear);
        ScenarioAssert.Accepted(s);
        Assert.Equal("s1", s.Player("Петя").CellId);
    }

    [Fact]
    public void Each_target_of_a_spear_has_his_own_shield_and_his_own_reflection()
    {
        var s = Season().At("Вася", "c30").At("Петя", "c31").At("Коля", "c32").At("Женя", "c33").WithCoins("Женя", -30);
        s.Give("Петя", "shield");
        s.Used("Петя", "shield");
        s.Give("Вася", "spear");

        s.Used("Вася", "spear");

        Assert.Single(s.LastEvents<HostileIntercepted>());
        Assert.Equal(("c31", 20), (s.Player("Петя").CellId, s.Player("Петя").Coins));
        Assert.Equal(("c31", 19), (s.Player("Коля").CellId, s.Player("Коля").Coins));
        Assert.Equal(("c32", -30), (s.Player("Женя").CellId, s.Player("Женя").Coins));
        Assert.Equal(18, s.Player("Вася").Coins);
    }

    [Fact]
    public void A_spear_reaches_no_finisher()
    {
        var s = ImportedItemsSeason.WithFinishers();
        var spear = s.Give("Вася", "spear");

        // Петя (first) and Коля (later) are on the finish, a cell away: neither is reached, nobody is left
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", spear), RejectionCodes.ItemNoTarget);
    }

    [Fact]
    public void A_banana_steps_forward_and_pushes_the_nearest_player_behind_one_back()
    {
        var s = Season().At("Вася", "c20").At("Петя", "c17").At("Коля", "c15").At("Женя", "c25");
        s.Give("Вася", "slippery-banana");

        s.Used("Вася", "slippery-banana");

        Assert.Equal(("c21", "c16", "c15", "c25"), (s.Player("Вася").CellId, s.Player("Петя").CellId, s.Player("Коля").CellId, s.Player("Женя").CellId));
    }

    [Fact]
    public void A_banana_pushes_every_nearest_player_behind_on_a_tie_and_steps_forward_once()
    {
        var s = Season().At("Вася", "c20").At("Петя", "c17").At("Коля", "c17").At("Женя", "c20");
        s.Give("Вася", "slippery-banana");

        s.Used("Вася", "slippery-banana");

        // Женя on Вася's cell is not behind him; the user's own step is one step however many were pushed
        Assert.Equal(("c21", "c16", "c16", "c20"), (s.Player("Вася").CellId, s.Player("Петя").CellId, s.Player("Коля").CellId, s.Player("Женя").CellId));
    }

    [Fact]
    public void A_banana_with_nobody_behind_is_refused()
    {
        var s = Season().At("Вася", "c5").At("Петя", "c6").At("Коля", "c7").At("Женя", "c5");
        var banana = s.Give("Вася", "slippery-banana");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", banana), RejectionCodes.ItemNoTarget);
    }

    [Theory]
    [InlineData("noCells")]
    [InlineData("cellsElsewhere")]
    [InlineData("sameCellElsewhere")]
    public void Reach_is_checked(string variant)
    {
        var target = variant switch
        {
            "noCells" => new TargetSpec { Selector = TargetSelector.WithinCells },
            "cellsElsewhere" => new TargetSpec { Selector = TargetSelector.All, Cells = 3 },
            _ => new TargetSpec { Selector = TargetSelector.NearestBehind, IncludeSameCell = true },
        };
        var spear = EconomyScenario.RepositoryPack().Objects.Single(o => o.Id == "spear");

        Assert.NotEmpty(ContentValidator.Check(spear with { Effect = spear.Effect! with { Target = target } }));
    }
}

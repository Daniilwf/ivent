using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rolls;

/// <summary>Which games belong to a category on the wheel (D-45) and how weights count.</summary>
public class RollCategoryTests
{
    private static readonly int[] s_seeds = [.. Enumerable.Range(1, 40)];

    [Fact]
    public void Tag_matches_category_regardless_of_case()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "hORROR")
            .WithPlayers("Вася");

        s.Roll("Вася");

        var rolled = Assert.Single(s.LastEvents<GameRolled>());
        Assert.Equal("Horror", rolled.Category);
        Assert.Equal(s.GameId("Silent Hill"), rolled.GameId);
    }

    [Fact]
    public void Game_with_several_tags_is_in_each_of_those_categories()
    {
        var categoriesSeen = new HashSet<string>();
        foreach (var seed in s_seeds)
        {
            var s = Scenario.New(seed: seed)
                .WithCategory("Horror").WithCategory("Action")
                .WithGame("Resident Evil 4", 16, "Horror", "Action")
                .WithPlayers("Вася");

            s.Roll("Вася");

            var rolled = Assert.Single(s.LastEvents<GameRolled>());
            Assert.Equal(s.GameId("Resident Evil 4"), rolled.GameId);
            categoriesSeen.Add(rolled.Category);
        }

        Assert.Equal(["Action", "Horror"], categoriesSeen.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Category_with_zero_weight_is_never_spun()
    {
        foreach (var seed in s_seeds)
        {
            var s = Scenario.New(seed: seed)
                .WithCategory("Zero", weight: 0).WithGame("Tetris", 2, "Zero")
                .WithCategory("Horror", weight: 1).WithGame("Silent Hill", 12, "Horror")
                .WithPlayers("Вася");

            s.Roll("Вася");

            Assert.Equal("Horror", Assert.Single(s.LastEvents<GameRolled>()).Category);
        }
    }

    [Fact]
    public void Only_zero_weight_categories_reject_with_no_available_games()
    {
        var s = Scenario.New()
            .WithCategory("Zero", weight: 0).WithGame("Tetris", 2, "Zero")
            .WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.NoAvailableGames);
    }

    [Fact]
    public void Same_seed_gives_the_same_roll_whatever_order_the_pool_lists_categories_in()
    {
        foreach (var seed in s_seeds)
        {
            var forward = Scenario.New(seed: seed)
                .WithCategory("Action", 2).WithCategory("Horror", 3).WithCategory("Puzzle", 1)
                .WithGame("Doom", 4, "Action").WithGame("Silent Hill", 12, "Horror").WithGame("Tetris", 2, "Puzzle")
                .WithPlayers("Вася");
            var backward = Scenario.New(seed: seed)
                .WithCategory("Puzzle", 1).WithCategory("Horror", 3).WithCategory("Action", 2)
                .WithGame("Doom", 4, "Action").WithGame("Silent Hill", 12, "Horror").WithGame("Tetris", 2, "Puzzle")
                .WithPlayers("Вася");

            forward.Roll("Вася");
            backward.Roll("Вася");

            Assert.Equal(forward.Last.Events, backward.Last.Events);
        }
    }
}

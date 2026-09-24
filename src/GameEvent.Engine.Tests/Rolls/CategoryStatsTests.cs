using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rolls;

/// <summary>
/// G11: the admin sees, for every category, how many of its games can be rolled right now (SPEC «Уточнения»: small
/// categories burn out, games with many tags come up more often; D-92). Available means not deleted, not completed in
/// the season, not played, offered or among pending options. Personal exclusions are not subtracted.
/// </summary>
public class CategoryStatsTests
{
    private static IReadOnlyList<CategoryStat> Stats(Scenario s) => PoolStats.Categories(s.State, s.Context().Pool);

    private static Scenario Pool() =>
        Scenario.New()
            .WithCategory("Puzzle", weight: 2)
            .WithCategory("Action", weight: 1)
            .WithCategory("Horror", weight: 3)
            .WithCategory("Empty", weight: 5)
            .WithGame("Silent Hill", 12, "Horror")
            .WithGame("Alan Wake", 15, "Horror", "Action")
            .WithGame("Tetris", 2, "Puzzle")
            .WithGame("Portal", 4, "Puzzle")
            .WithGame("Baba Is You", 7, "Puzzle")
            .WithPlayers("Вася", "Петя");

    [Fact]
    public void Every_category_is_listed_by_name_with_its_weight_and_available_games()
    {
        var s = Pool();

        // A game with two tags counts in both categories; a category without games is listed with zero
        Assert.Equal(
            [new CategoryStat("Action", 1, 1), new CategoryStat("Empty", 5, 0), new CategoryStat("Horror", 3, 2), new CategoryStat("Puzzle", 2, 3)],
            Stats(s));
    }

    [Fact]
    public void Deleted_games_are_not_counted()
    {
        var s = Pool().WithDeletedGame("Gone", 5, "Horror").DeleteGame("Tetris");

        Assert.Equal(
            [new CategoryStat("Action", 1, 1), new CategoryStat("Empty", 5, 0), new CategoryStat("Horror", 3, 2), new CategoryStat("Puzzle", 2, 2)],
            Stats(s));
    }

    [Fact]
    public void Offered_played_and_completed_games_are_not_counted()
    {
        // Given the pool is one category of three games
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithGame("Alan Wake", 15, "Horror").WithGame("Dead Space", 9, "Horror")
            .WithPlayers("Вася", "Петя", "Маша");
        Assert.Equal([new CategoryStat("Horror", 1, 3)], Stats(s));

        // Offered (reserved) to Вася
        s.Roll("Вася");
        Assert.Equal([new CategoryStat("Horror", 1, 2)], Stats(s));

        // Played by Вася, offered to Петя
        s.Start("Вася").Roll("Петя");
        Assert.Equal([new CategoryStat("Horror", 1, 1)], Stats(s));

        // Completed by Вася: gone for the season; Маша takes the last one
        s.Complete("Вася").Roll("Маша");
        Assert.Equal([new CategoryStat("Horror", 1, 0)], Stats(s));

        // Петя's offer is discarded: that game is available again, the completed one is not
        s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Сброс", DiscardOffer: true));
        Assert.Equal([new CategoryStat("Horror", 1, 1)], Stats(s));
    }

    [Fact]
    public void Options_of_a_pending_choice_are_not_counted()
    {
        var s = Scenario.New()
            .WithRuleset(r => r with { Roll = r.Roll with { ChoiceCount = 2 } })
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithGame("Alan Wake", 15, "Horror").WithGame("Dead Space", 9, "Horror")
            .WithPlayers("Вася");

        s.Roll("Вася");

        Assert.Equal(2, s.Player("Вася").Choice!.Options.Count);
        Assert.Equal([new CategoryStat("Horror", 1, 1)], Stats(s));
    }

    [Fact]
    public void Personal_exclusions_are_not_subtracted()
    {
        // Given Вася excluded the only horror game: it is still available for everyone else
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
            .WithPlayers("Вася", "Петя")
            .Roll("Вася");
        s.Act(new DeclareAlreadyPlayed(s.PlayerId("Вася"), s.GameId("Silent Hill")));
        ScenarioAssert.Accepted(s);
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);

        Assert.Equal([new CategoryStat("Horror", 1, 1)], Stats(s));
    }

    [Fact]
    public void Game_with_two_tags_leaves_both_categories_when_it_is_taken()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithCategory("Action")
            .WithGame("Alan Wake", 15, "Horror", "Action")
            .WithPlayers("Вася");

        Assert.Equal([new CategoryStat("Action", 1, 1), new CategoryStat("Horror", 1, 1)], Stats(s));

        s.Roll("Вася");

        Assert.Equal([new CategoryStat("Action", 1, 0), new CategoryStat("Horror", 1, 0)], Stats(s));
    }

    [Fact]
    public void Stats_do_not_change_the_state()
    {
        var s = Pool().Roll("Вася");
        var before = s.State;

        Stats(s);

        Assert.Equal(before, s.State);
    }

    [Fact]
    public void Weights_follow_the_pool_categories()
    {
        var s = Scenario.New(TestRuleset.Create()).WithCategory("Horror", weight: 7).WithGame("Silent Hill", 12, "Horror").WithPlayers("Вася");

        Assert.Equal([new CategoryStat("Horror", 7, 1)], Stats(s));
    }
}

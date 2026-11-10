using GameEvent.Engine.Runs;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// A genre an item imposed against the drops in a row (D-324, D-423): the run it imposed is a run like any other — its drop
/// adds to the streak and pays the grown penalty.
/// </summary>
public class ImposedGenreDropTests
{
    private static Scenario Season()
    {
        var s = EconomyScenario.New().WithRuleset(r => r with { Drop = r.Drop with { ConsecutiveExtraDice = 1 } })
            .WithCategory("Horror").WithCategory("RPG")
            .WithGame("Persona", 12, "RPG").WithGame("Disco Elysium", 12, "RPG").WithGame("Silent Hill", 12, "Horror").WithGame("Soma", 12, "Horror")
            .WithPlayers("Вася", "Петя").WithPoints("Петя", 10);
        s.WithContent(EconomyScenario.RepositoryPack());
        return s;
    }

    private static int DropActive(Scenario s)
    {
        s.Advance(TimeSpan.FromHours(2)).Act(new DropRun(s.PlayerId("Петя")));
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(1));
        return Assert.Single(s.LastEvents<RunDropped>()).PenaltyDice.Count;
    }

    [Fact]
    public void A_drop_of_a_run_with_an_imposed_genre_adds_to_the_streak()
    {
        // Given Петя dropped once
        var s = Season();
        s.Roll("Петя").Start("Петя");
        var first = DropActive(s);

        // When Вася imposes RPG and Петя drops the RPG game he rolled
        s.Give("Вася", "dirty-trick");
        s.Used("Вася", "dirty-trick", "Петя", "RPG");
        s.Roll("Петя").Start("Петя");
        var run = s.State.Runs[s.Player("Петя").ActiveRunId!.Value];
        Assert.Equal(["RPG"], (IReadOnlyList<string>)run.Snapshot.ImposedTags);
        var second = DropActive(s);

        // Then it is the second drop in a row, and the next one is the third
        Assert.Equal(first + 1, second);
        s.Roll("Петя").Start("Петя");
        Assert.Equal(first + 2, DropActive(s));
    }
}

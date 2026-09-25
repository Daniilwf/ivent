using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;

namespace GameEvent.Engine.Tests.Support;

/// <summary>
/// C13: a failing random game prints as the builder calls that replay it — setup, clock moves, commands with ids by
/// name, ruleset changes as <c>with</c> expressions.
/// </summary>
public class ScenarioCodeTests
{
    private static Scenario Played()
    {
        var s = Scenario.New(seed: 7)
            .WithRuleset(r => r with { Roll = r.Roll with { ChoiceCount = 1, FreeRerollsPerRoll = 2 } })
            .WithMapLength(10)
            .WithCategory("Horror", weight: 2)
            .WithGame("Silent Hill", 12, "Horror")
            .WithGame("Tetris", 2.5m, "Horror")
            .WithPlayers("Вася", "Петя");
        s.Advance(TimeSpan.FromHours(3));
        s.Act(new RollGame(s.PlayerId("Вася")));
        s.Act(new AdjustPlayer(s.PlayerId("Петя"), "правка \"по чату\"", PointsDelta: 2));
        s.Clock.Advance(TimeSpan.FromMinutes(90));
        s.Act(new ChangeRuleset(s.Ruleset with { Reward = s.Ruleset.Reward with { DiceCount = s.Ruleset.Reward.DiceCount with { HoursPerDie = 2.5m } } }));
        s.NextRandom(3, 1).Act(new StartRun(s.PlayerId("Вася"))).Act(new CompleteRun(s.PlayerId("Вася"), Difficulty.Hard, ChallengeDone: true));
        return s;
    }

    [Fact]
    public void A_scenario_prints_as_its_builder_calls()
    {
        var s = Played();

        Assert.Equal(
            """
            var s = Scenario.New(seed: 7);
            s.WithRuleset(r => r with { Roll = r.Roll with { FreeRerollsPerRoll = 2 } });
            s.WithMapLength(10);
            s.WithCategory("Horror", weight: 2);
            s.WithGame("Silent Hill", 12m, "Horror");
            s.WithGame("Tetris", 2.5m, "Horror");
            s.WithPlayers("Вася", "Петя");
            s.Advance(TimeSpan.FromHours(3));
            s.Act(new RollGame(s.PlayerId("Вася")));
            s.Act(new AdjustPlayer(s.PlayerId("Петя"), "правка \"по чату\"", PointsDelta: 2));
            s.Advance(TimeSpan.FromMinutes(90));
            s.Act(new ChangeRuleset(s.Ruleset with { Reward = s.Ruleset.Reward with { DiceCount = s.Ruleset.Reward.DiceCount with { HoursPerDie = 2.5m } } }));
            s.NextRandom(3, 1);
            s.Act(new StartRun(s.PlayerId("Вася")));
            s.Act(new CompleteRun(s.PlayerId("Вася"), Difficulty.Hard, ChallengeDone: true));
            """.ReplaceLineEndings(),
            s.ToCode());
    }

    [Fact]
    public void Ids_the_scenario_made_print_as_parsed_ids_with_what_they_are()
    {
        var s = Played();
        var run = s.State.Runs.Values.Single();

        s.Act(new CorrectRunHours(run.RunId, 3, "часы"));

        Assert.EndsWith(
            $"s.Act(new CorrectRunHours(Guid.Parse(\"{run.RunId}\") /* run: Вася, {s.GameTitle(run.GameId)} */, 3m, \"часы\"));",
            s.ToCode());
    }

    [Fact]
    public void A_failure_inside_a_played_scenario_carries_its_code()
    {
        var s = Played();

        var failure = Assert.Throws<ScenarioFailedException>(() => s.Explained(x => Assert.Fail("an invariant broke")));

        Assert.Equal(s.ToCode(), failure.Code);
        Assert.StartsWith("The failing scenario as builder code:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("an invariant broke", failure.Message, StringComparison.Ordinal);
    }
}

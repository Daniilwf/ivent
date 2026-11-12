using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Runs;

/// <summary>
/// The difficulty declared at the start (W8, AU12-2, D-633; SPEC «Награда»: «Тип кубика зависит от сложности, указанной
/// при старте и подтверждённой пруфом»; «Уточнения / Часы и кубы»: «если игрок понизил сложность посреди игры, считается
/// более низкая»). The start may name a difficulty; the completion then counts the lower of it and the one claimed. A start
/// without one (and every run started before the field existed) caps nothing. Test ruleset: easy d2, normal d4, hard d6,
/// extreme d6 with a good event; a 6-hour game gives two dice.
/// </summary>
public class DeclaredDifficultyTests
{
    private static Scenario Started(Difficulty? declared)
    {
        var s = Scenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася").Roll("Вася");
        s.Act(new StartRun(s.PlayerId("Вася"), declared));
        ScenarioAssert.Accepted(s);
        return s;
    }

    [Fact]
    public void Game_picked_from_a_choice_starts_on_the_declared_difficulty()
    {
        // D-91: a game choice starts the run at once, so the pick carries the difficulty
        var s = Scenario.New()
            .WithRuleset(r => r with { Roll = r.Roll with { ChoiceCount = 2 } })
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Alan Wake", 6, "Horror")
            .WithPlayers("Вася").Roll("Вася");
        var choice = s.Player("Вася").Choice!;

        s.Act(new MakeChoice(s.PlayerId("Вася"), choice.ChoiceId, choice.Options[0].Id, Difficulty.Easy));

        ScenarioAssert.Accepted(s);
        Assert.Equal(Difficulty.Easy, Assert.Single(s.LastEvents<RunStarted>()).DeclaredDifficulty);
        s.NextRandom(1, 1).Complete("Вася", Difficulty.Extreme);
        Assert.Equal(Difficulty.Easy, Assert.Single(s.LastEvents<RunCompleted>()).Difficulty);
    }

    [Fact]
    public void Undefined_difficulty_of_a_game_choice_is_rejected_as_an_invalid_command()
    {
        var s = Scenario.New()
            .WithRuleset(r => r with { Roll = r.Roll with { ChoiceCount = 2 } })
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Alan Wake", 6, "Horror")
            .WithPlayers("Вася").Roll("Вася");
        var choice = s.Player("Вася").Choice!;

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new MakeChoice(x.PlayerId("Вася"), choice.ChoiceId, choice.Options[0].Id, (Difficulty)9)), RejectionCodes.CommandInvalid);
    }

    [Fact]
    public void Start_keeps_the_declared_difficulty_in_the_event_and_the_run()
    {
        var s = Started(Difficulty.Easy);

        var started = Assert.Single(s.LastEvents<RunStarted>());
        Assert.Equal(Difficulty.Easy, started.DeclaredDifficulty);
        Assert.Equal(Difficulty.Easy, s.State.Runs[started.RunId].DeclaredDifficulty);
    }

    [Fact]
    public void Started_on_easy_and_completed_as_hard_throws_the_easy_die()
    {
        var s = Started(Difficulty.Easy);

        s.NextRandom(2, 1).Complete("Вася", Difficulty.Hard);

        ScenarioAssert.Accepted(s);
        Assert.Equal(Difficulty.Easy, Assert.Single(s.LastEvents<RunCompleted>()).Difficulty);
        Assert.All(Assert.Single(s.LastEvents<CompletionRolled>()).Dice, d => Assert.Equal(2, d.Sides));
        Assert.Equal(3, s.Player("Вася").Points);
    }

    [Fact]
    public void Started_on_hard_and_completed_lower_counts_the_lower()
    {
        var s = Started(Difficulty.Hard);

        s.NextRandom(4, 4).Complete("Вася", Difficulty.Normal);

        ScenarioAssert.Accepted(s);
        Assert.Equal(Difficulty.Normal, Assert.Single(s.LastEvents<RunCompleted>()).Difficulty);
        Assert.All(Assert.Single(s.LastEvents<CompletionRolled>()).Dice, d => Assert.Equal(4, d.Sides));
    }

    [Fact]
    public void Started_on_hard_and_completed_as_extreme_grants_no_good_event()
    {
        var s = Started(Difficulty.Hard);

        s.NextRandom(6, 6).Complete("Вася", Difficulty.Extreme);

        ScenarioAssert.Accepted(s);
        Assert.Equal(Difficulty.Hard, Assert.Single(s.LastEvents<RunCompleted>()).Difficulty);
        Assert.Empty(s.LastEvents<ManualEffectCreated>());
    }

    [Theory]
    [InlineData(Difficulty.Easy, 2)]
    [InlineData(Difficulty.Hard, 6)]
    [InlineData(Difficulty.Extreme, 6)]
    public void Start_without_a_declared_difficulty_counts_the_claimed_one_as_before(Difficulty claimed, int sides)
    {
        var s = Started(null);
        Assert.Null(Assert.Single(s.LastEvents<RunStarted>()).DeclaredDifficulty);

        s.NextRandom(1, 1).Complete("Вася", claimed);

        ScenarioAssert.Accepted(s);
        Assert.Equal(claimed, Assert.Single(s.LastEvents<RunCompleted>()).Difficulty);
        Assert.All(Assert.Single(s.LastEvents<CompletionRolled>()).Dice, d => Assert.Equal(sides, d.Sides));
    }

    [Fact]
    public void Undefined_declared_difficulty_is_rejected_as_an_invalid_command()
    {
        var s = Scenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася").Roll("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new StartRun(x.PlayerId("Вася"), (Difficulty)42)), RejectionCodes.CommandInvalid);
    }

    [Fact]
    public void Replaying_the_log_keeps_the_declared_difficulty()
    {
        var s = Started(Difficulty.Normal);
        s.NextRandom(1, 1).Complete("Вася", Difficulty.Hard);

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }
}

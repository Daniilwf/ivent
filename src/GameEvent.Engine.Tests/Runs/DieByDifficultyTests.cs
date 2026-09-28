using GameEvent.Engine.Effects;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Runs;

/// <summary>
/// The event a difficulty grants (W2; SPEC «Награда за прохождение»: «Выше сложной — d6 и хороший ивент»; D-10, D-96):
/// a difficulty with <c>grantEvent</c> creates a manual effect «Разыграй хороший/плохой ивент» with source
/// <see cref="ManualEffectSource.Difficulty"/> and the run. The die rule is part of the roll-time snapshot.
/// Test ruleset: only extreme has <c>grantEvent: good</c>.
/// </summary>
public class DieByDifficultyTests
{
    private static Scenario Playing(Func<Ruleset, Ruleset>? ruleset = null)
    {
        var s = Scenario.New();
        if (ruleset is not null)
        {
            s.WithRuleset(ruleset);
        }

        s.WithCategory("Horror").WithGame("Silent Hill", 6, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася");
        ScenarioAssert.Accepted(s);
        return s;
    }

    private static Func<Ruleset, Ruleset> Dies(Func<DieByDifficulty, DieByDifficulty> change) =>
        r => r with { Reward = r.Reward with { DieByDifficulty = change(r.Reward.DieByDifficulty) } };

    [Fact]
    public void Extreme_difficulty_grants_a_good_event_to_play_out()
    {
        var s = Playing();
        var runId = s.Player("Вася").ActiveRunId!.Value;
        var vasya = s.PlayerId("Вася");

        s.Complete("Вася", Difficulty.Extreme);

        ScenarioAssert.Accepted(s);
        var created = Assert.Single(s.LastEvents<ManualEffectCreated>());
        Assert.Equal(new ManualEffectCreated(created.EffectId, vasya, EventKind.Good, ManualEffectSource.Difficulty, runId), created);
        Assert.NotEqual(Guid.Empty, created.EffectId);
        Assert.Equal(
            new PendingManualEffect(created.EffectId, vasya, EventKind.Good, ManualEffectSource.Difficulty, runId),
            Assert.Single(s.State.ManualEffects.Values));
    }

    [Theory]
    [InlineData(Difficulty.Easy)]
    [InlineData(Difficulty.Normal)]
    [InlineData(Difficulty.Hard)]
    public void Other_difficulties_grant_no_event(Difficulty difficulty)
    {
        var s = Playing();

        s.Complete("Вася", difficulty);

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<ManualEffectCreated>());
        Assert.Empty(s.State.ManualEffects);
    }

    [Fact]
    public void Configured_bad_event_on_hard_is_created_as_bad()
    {
        var s = Playing(Dies(d => d with { Hard = new DieRule { Sides = 6, GrantEvent = EventKind.Bad } }));
        var runId = s.Player("Вася").ActiveRunId!.Value;

        s.Complete("Вася", Difficulty.Hard);

        ScenarioAssert.Accepted(s);
        var created = Assert.Single(s.LastEvents<ManualEffectCreated>());
        Assert.Equal(
            new ManualEffectCreated(created.EffectId, s.PlayerId("Вася"), EventKind.Bad, ManualEffectSource.Difficulty, runId), created);
    }

    [Fact]
    public void Extreme_without_grant_event_in_the_rules_grants_nothing()
    {
        var s = Playing(Dies(d => d with { Extreme = new DieRule { Sides = 6 } }));

        s.Complete("Вася", Difficulty.Extreme);

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<ManualEffectCreated>());
    }

    [Fact]
    public void Grant_event_removed_after_the_roll_still_applies_to_the_run()
    {
        // S1: the die rule, grantEvent included, is fixed at the roll
        var s = Playing();
        s.WithRuleset(Dies(d => d with { Extreme = new DieRule { Sides = 6 } }));

        s.Complete("Вася", Difficulty.Extreme);

        ScenarioAssert.Accepted(s);
        Assert.Equal(EventKind.Good, Assert.Single(s.LastEvents<ManualEffectCreated>()).DrawEvent);
    }

    [Fact]
    public void Each_extreme_completion_adds_its_own_effect()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("A", 3, "Horror").WithGame("B", 3, "Horror")
            .WithPlayers("Вася");

        s.Roll("Вася").Start("Вася").Complete("Вася", Difficulty.Extreme);
        s.Roll("Вася").Start("Вася").Complete("Вася", Difficulty.Extreme);

        var created = s.Log.OfType<ManualEffectCreated>().ToList();
        Assert.Equal(2, created.Count);
        Assert.NotEqual(created[0].EffectId, created[1].EffectId);
        Assert.NotEqual(created[0].RunId, created[1].RunId);
        Assert.Equal(2, s.State.ManualEffects.Count);
    }
}

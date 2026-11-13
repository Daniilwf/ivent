using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.GlobalEvents;

/// <summary>
/// The thin slice of global events (task 5.9): the admin launches an event of the content on everyone with a goal of hours;
/// its effect is settled for everyone once, a completion in the window brings the HowLongToBeat hours of its snapshot, the
/// goal reached queues its reward for everyone (SPEC «Общие ивенты», D-517, D-518, D-537).
/// </summary>
public class GlobalEventSliceTests
{
    private static ChangeResourceAction Coins(int amount) => new() { Resource = "coins", Amount = ContentValue.Of(amount) };

    private static readonly ObjectDefinition s_coinRain = new()
    {
        Id = "coin-rain",
        Kind = ObjectKind.Event,
        Name = "Монетный дождь",
        Description = "Всем по две монетки.",
        Effect = new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.All }, Actions = [Coins(2)] },
    };

    [Fact]
    public void A_launched_event_gives_everyone_its_effect_and_its_goal_counts_hours_until_the_reward()
    {
        var s = Scenario.New(TestRuleset.Create().WithGlobalEvents())
            .WithCategory("Horror")
            .WithGame("Silent Hill", 6, "Horror")
            .WithPlayers("Вася", "Петя", "Маша")
            .WithContent(new ContentPack { Objects = [s_coinRain] });

        s.Act(new LaunchGlobalEvent("coin-rain", 72, new GlobalEventGoal { Metric = GoalMetric.Hours, Target = 5, Reward = new EffectSpec { Actions = [Coins(5)] } }));
        ScenarioAssert.Accepted(s);
        var launched = Assert.Single(s.LastEvents<GlobalEventLaunched>());
        Assert.Equal(s.Clock.UtcNow.AddHours(72), launched.EndsAt);
        Assert.Single(s.LastEvents<ForAllQueued>());

        s.Act(new SettleForAll(ForAllSettlements.Next(s.State)!.Value));
        Assert.All(new[] { "Вася", "Петя", "Маша" }, p => Assert.Equal(2, s.Player(p).Coins));

        s.Roll("Вася").Start("Вася").NextRandom(1, 1).Complete("Вася", estimatedHours: 40);
        var progress = Assert.Single(s.LastEvents<GlobalProgressChanged>());
        Assert.Equal(6m, progress.Delta);
        Assert.Single(s.LastEvents<GlobalGoalReached>());
        var reward = Assert.Single(s.LastEvents<ForAllQueued>());
        Assert.Equal(ForAllSource.GoalReward, reward.Source);
    }
}

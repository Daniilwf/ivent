using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.GlobalEvents.GlobalSetup;

namespace GameEvent.Engine.Tests.GlobalEvents;

/// <summary>
/// Effects on everyone stay within the chain limit (task 5.9, EV31, EV53; SPEC «Лимит цепочки»: a command writes at most 50
/// events; D-519, D-537, D-700): with 17 players (16 and one added mid-season) the launch effect and the goal reward are
/// settled for all of them in batches, each batch within <c>effects.maxEventsPerCommand</c>, nothing cut, nobody moved.
/// Written by <c>test-author</c> from the SPEC and the public contract, before the implementation is finished.
/// </summary>
public class GlobalEventLimitTests
{
    /// <summary>Three events a player: 17 of them do not fit one command.</summary>
    private static readonly ObjectDefinition s_triple = Event("triple-gift", OnAll(Coins(2), Points(1), Give("orange")));

    private static string[] Players17() => [.. Names.Concat(Enumerable.Range(5, 12).Select(i => $"Игрок {i}"))];

    private static Scenario Season17()
    {
        var s = Season(pack: Pack([s_triple]), players: Players17()[..16]);
        s.WithPlayers("Лёша");
        Assert.Equal(17, s.State.Players.Count);
        return s;
    }

    private sealed record Batch(ForAllSettled Settled, IReadOnlyList<IGameEvent> Events);

    private static List<Batch> Settle(Scenario s)
    {
        var batches = new List<Batch>();
        while (ForAllSettlements.Next(s.State) is { } id)
        {
            s.Act(new SettleForAll(id));
            ScenarioAssert.Accepted(s);
            batches.Add(new Batch(Assert.IsType<ForAllSettled>(s.Last.Events[0]), s.Last.Events));
            Assert.True(batches.Count <= 20, "The settlement never ends.");
        }

        return batches;
    }

    private static void AssertWithinTheLimit(Scenario s, List<Batch> batches)
    {
        Assert.NotEmpty(batches);
        Assert.All(batches, b => Assert.InRange(b.Events.Count, 1, s.Ruleset.Effects.MaxEventsPerCommand));
        Assert.All(batches, b => Assert.DoesNotContain(b.Events, e => e is EffectChainCut or PlayerMoved));
        Assert.True(batches[^1].Settled.Done);
        Assert.All(batches, b => Assert.Empty(b.Settled.Skipped));

        // Everyone exactly once
        Assert.Equal(s.State.Players.Keys.Order(), batches.SelectMany(b => b.Settled.Players).Order());
    }

    [Theory]
    [InlineData("coin-rain")]
    [InlineData("shield-day")]
    [InlineData("triple-gift")]
    public void The_launch_effect_reaches_all_17_within_the_limit(string objectId)
    {
        var s = Season17();
        var before = s.State.Players;

        s.Launch(objectId, 72);
        Assert.Equal(17, s.Queued().Players.Count);
        var batches = Settle(s);

        AssertWithinTheLimit(s, batches);
        if (objectId == "triple-gift")
        {
            Assert.True(batches.Count > 1, "17 players of 3 events each do not fit one command.");
        }

        foreach (var (id, was) in before)
        {
            var now = s.State.Players[id];
            switch (objectId)
            {
                case "coin-rain":
                    Assert.Equal(was.Coins + 2, now.Coins);
                    break;
                case "shield-day":
                    Assert.Single(now.Wallet.Inventory, o => o.ObjectId == "day-shield");
                    break;
                default:
                    Assert.Equal((was.Coins + 2, was.Points + 1), (now.Coins, now.Points));
                    Assert.Single(now.Wallet.Inventory, o => o.ObjectId == "orange");
                    break;
            }

            Assert.Equal(was.CellId, now.CellId);
        }

        Assert.DoesNotContain(s.Log, e => e is EffectChainCut);
    }

    [Fact]
    public void A_goal_reached_by_a_completion_rewards_all_17_within_the_limit()
    {
        var s = Season17();
        s.Launch("banner", 72, HoursGoal(10, OnAll(Coins(2), Points(1), Give("orange"))));

        s.Completed("Вася", "Silent Hill");

        // The completion only queues the reward: its own events stay a completion's
        Assert.Single(s.LastEvents<GlobalGoalReached>());
        Assert.Empty(s.LastEvents<Inventory.ObjectGiven>());
        Assert.InRange(s.Last.Events.Count, 1, s.Ruleset.Effects.MaxEventsPerCommand);
        var before = s.State.Players;
        var batches = Settle(s);

        AssertWithinTheLimit(s, batches);
        Assert.True(batches.Count > 1);
        Assert.All(before, p => Assert.Equal(p.Value.Coins + 2, s.State.Players[p.Key].Coins));
        Assert.All(before, p => Assert.Equal(p.Value.CellId, s.State.Players[p.Key].CellId));
    }

    [Fact]
    public void No_settlement_of_a_global_event_ever_moves_a_player()
    {
        // D-519, D-754: effects on everyone from the system do not move
        var s = Season17();
        s.Launch("triple-gift", 72, RunsGoal(1, OnAll(Coins(1))));
        var launch = Settle(s);
        s.Completed("Петя", "Alan Wake");
        var reward = Settle(s);

        Assert.All(launch.Concat(reward), b => Assert.DoesNotContain(b.Events, e => e is PlayerMoved));
    }
}

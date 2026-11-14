using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Map;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.GlobalEvents;

/// <summary>
/// Moving everyone in batches (D-757, the owner's decision of 2026-11-14, lifting D-519 and D-549, the proposal of D-703):
/// an effect on everyone may move and teleport; a drawn event on everyone goes to a settlement of its own, its actions on
/// «self» played on the drawer in his command; each batch holds whole players with their cells' reactions within the 50
/// events; a player choosing a branch is not moved and does not hold up the others.
/// </summary>
public class ForAllMovementTests
{
    private static readonly TargetSpec s_all = new() { Selector = TargetSelector.All };

    private static MoveAction Move(int steps) => new() { Steps = ContentValue.Of(steps) };

    private static ChangeResourceAction Coins(int amount, bool self = false) =>
        new() { Resource = "coins", Amount = ContentValue.Of(amount), Target = self ? ActionTarget.Self : null };

    private static ObjectDefinition Event(string id, EffectSpec effect) => new()
    {
        Id = id,
        Kind = ObjectKind.Event,
        Name = $"Ивент {id}",
        Description = "Ивент для всех.",
        Rarity = Rarity.Common,
        Effect = effect,
    };

    /// <summary>«Буря»: everyone two cells forward; the drawer gets 3 coins himself.</summary>
    private static readonly ObjectDefinition s_storm = Event("storm", new EffectSpec { Target = s_all, Actions = [Move(2), Coins(3, self: true)] });

    /// <summary>«Сдвиг»: everyone one cell forward (a global event).</summary>
    private static readonly ObjectDefinition s_shift = Event("shift", new EffectSpec { Target = s_all, Actions = [Move(1)] });

    /// <summary>An item drawing from the good deck, which holds only «Буря».</summary>
    private static readonly ObjectDefinition s_fortune = EconomyScenario.Examples()["orange"] with
    {
        Id = "fortune",
        Name = "Печенье с предсказанием",
        Effect = new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.Self }, Actions = [new DrawEventAction { Deck = "good" }] },
    };

    private static string[] Names(int count) =>
        [.. new[] { "Вася", "Петя", "Маша", "Коля" }.Concat(Enumerable.Range(5, Math.Max(0, count - 4)).Select(i => $"Игрок {i}")).Take(count)];

    private static Scenario Season(int players = 4, MapGraph? map = null)
    {
        var s = Scenario.New(TestRuleset.Create().WithGlobalEvents().WithPolls());
        if (map is null)
        {
            s.WithMapLength(40);
        }
        else
        {
            s.WithMap(map);
        }

        s.WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithGame("Fatal Frame", 3, "Horror").WithPlayers(Names(players));
        return s.WithContent(new ContentPack
        {
            Objects = [s_fortune, s_shift, s_storm],
            Wheels = [EventScenario.Deck(global::GameEvent.Engine.Rulesets.EventKind.Good, null, "storm")],
        });
    }

    private static Scenario SettleAll(Scenario s)
    {
        for (var i = 0; ForAllSettlements.Next(s.State) is { } id; i++)
        {
            Assert.True(i < 30, "The settlement never ends.");
            s.Act(new SettleForAll(id));
            ScenarioAssert.Accepted(s);
        }

        return s;
    }

    private static Dictionary<string, string> Cells(Scenario s, string[] names) => names.ToDictionary(n => n, n => s.Player(n).CellId);

    [Fact]
    public void A_drawn_event_on_everyone_moves_them_in_a_settlement_and_its_self_part_plays_on_the_drawer_at_once()
    {
        var s = Season();
        s.Give("Вася", "fortune");
        var coins = s.Player("Вася").Coins;

        s.Used("Вася", "fortune");

        Assert.Single(s.LastEvents<global::GameEvent.Engine.Decks.EventDrawn>());
        var queued = Assert.Single(s.LastEvents<ForAllQueued>());
        Assert.Equal(ForAllSource.DrawnEvent, queued.Source);
        Assert.Equal(s.PlayerId("Вася"), queued.Reference);
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal(coins + 3, s.Player("Вася").Coins);

        SettleAll(s);

        var moved = s.Log.OfType<PlayerMoved>().ToList();
        Assert.All(Names(4), n => Assert.Contains(moved, m => m.PlayerId == s.PlayerId(n) && m.Steps == 2));
        Assert.Equal(coins + 3, s.Player("Вася").Coins);
        Assert.All(new[] { "Петя", "Маша", "Коля" }, n => Assert.Equal(0, s.Player(n).Coins));
    }

    [Fact]
    public void A_global_event_moves_17_players_in_batches_within_the_limit()
    {
        var s = Season(players: 17);
        s.Act(new LaunchGlobalEvent("shift", 72));
        ScenarioAssert.Accepted(s);
        var limit = s.State.Rules.Effects.MaxEventsPerCommand;

        var before = s.Log.Count;
        SettleAll(s);

        var settledLog = s.Log.Skip(before).ToList();
        Assert.Equal(17, settledLog.OfType<ForAllSettled>().Sum(b => b.Players.Count));
        Assert.Equal(17, settledLog.OfType<PlayerMoved>().Count());
        Assert.Empty(settledLog.OfType<EffectChainCut>());
        Assert.All(s.History.Skip(s.History.Count - settledLog.OfType<ForAllSettled>().Count()), c => Assert.True(c.Events.Count <= limit));
    }

    [Fact]
    public void A_drawn_event_on_everyone_moves_17_players_with_their_cells_reactions_within_the_limit()
    {
        // Every second cell is an event cell of the good deck: the push of each stops on one and draws «Буря» again — its own
        // settlement, so the chain never cuts it (D-500 levels hold within each batch)
        var builder = MapBuilder.New();
        var path = new[] { "start" }.Concat(Enumerable.Range(1, 60).Select(i => $"c{i}")).Append("finish").ToArray();
        builder.Path(path);
        foreach (var cell in path.Where((_, i) => i > 0 && i < path.Length - 1 && i % 2 == 0))
        {
            builder.EventCell(cell, "good");
        }

        var s = Season(players: 17, map: builder.Build());
        s.Give("Вася", "fortune");
        s.Used("Вася", "fortune");
        var limit = s.State.Rules.Effects.MaxEventsPerCommand;
        var batches = 0;

        for (var i = 0; ForAllSettlements.Next(s.State) is { } id && i < 3; i++)
        {
            s.Act(new SettleForAll(id));
            ScenarioAssert.Accepted(s);
            batches++;
            Assert.True(s.Last.Events.Count <= limit, $"A batch wrote {s.Last.Events.Count} events.");
            Assert.DoesNotContain(s.Last.Events, e => e is EffectChainCut { Limit: EffectChainLimit.Events });
        }

        Assert.True(batches > 1, "17 pushes with their cells need more than one batch.");
        Assert.All(Names(17), n => Assert.NotEqual("start", s.Player(n).CellId));
    }

    [Fact]
    public void A_player_choosing_a_branch_is_not_moved_and_the_others_are()
    {
        var map = MapBuilder.New().Path("start", "f", "x", "x2", "x3", "finish").Path("f", "y", "y2", "y3", "finish").Build();
        var s = Season(map: map);
        s.RollTitle("Петя", "Fatal Frame").Start("Петя").NextRandom(2).Complete("Петя");
        Assert.Equal(("f", ChoiceKind.Branch), (s.Player("Петя").CellId, s.Player("Петя").Choice!.Kind));
        s.Act(new LaunchGlobalEvent("shift", 72));
        ScenarioAssert.Accepted(s);
        var others = new[] { "Вася", "Маша", "Коля" };
        var before = Cells(s, others);

        SettleAll(s);

        Assert.Equal(("f", ChoiceKind.Branch), (s.Player("Петя").CellId, s.Player("Петя").Choice!.Kind));
        Assert.All(others, n => Assert.NotEqual(before[n], s.Player(n).CellId));
        s.ChooseBranch("Петя", "y");
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void A_poll_result_and_a_goal_reward_on_everyone_may_move_now()
    {
        var s = Season();
        var move = new EffectSpec { Target = s_all, Actions = [Move(1)] };

        s.Act(new StartPoll(new PollDefinition
        {
            Question = "Двигаем всех?",
            Options = new ChoiceOptionsSpec { List = ["да", "нет"] },
            Voters = PollVoters.Players,
            Anonymous = false,
            ClosesInHours = 24,
            OnResult = move,
        }));
        ScenarioAssert.Accepted(s);
        s.Act(new LaunchGlobalEvent("shift", 72, new GlobalEventGoal { Metric = GoalMetric.Runs, Target = 1, Reward = move }));
        ScenarioAssert.Accepted(s);
    }
}

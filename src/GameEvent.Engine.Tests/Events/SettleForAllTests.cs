using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// Settlements «на всех» as a system command of their own (D-537, EV53; SPEC «Лимит цепочки», invariants 2, 3, 8, 12):
/// a reward of a goal, the result of a poll on everyone or the effect of a global event launch is not run inside the
/// command that caused it — the cause only queues it (<see cref="ForAllQueued"/> with the targets fixed then), and
/// <see cref="SettleForAll"/> commands settle it in batches of players, each within the 50 events of a command, the
/// batch recorded in <see cref="ForAllSettled"/>. The targets: everyone but the first finisher (provisional or not); a
/// later finisher only for points and coins (D-518, SPEC «Цели»). Undoing the cause waits for its batches, undone in
/// reverse; an undone batch halts the settlement for good.
/// Goals of 5.9 do not exist yet: a test trigger handler stands in for «цель достигнута» (D-24).
/// </summary>
public class SettleForAllTests
{
    private const string GoalComment = "Цель достигнута";

    private static readonly Guid s_goal = SequentialIds.Make(0x6F000000, 1);

    private static readonly string[] s_titles =
    [
        "Silent Hill", "Alan Wake", "Dead Space", "Outlast", "Amnesia", "Soma", "Prey", "Doom", "Quake", "Portal", "Limbo", "Inside",
    ];

    private static readonly TargetSpec s_all = new() { Selector = TargetSelector.All };

    private static ChangeResourceAction Resource(string resource, int amount) => new() { Resource = resource, Amount = ContentValue.Of(amount) };

    /// <summary>Points and coins only: a later finisher gets it too. Two events per player.</summary>
    private static readonly EffectSpec s_pointsAndCoins = new() { Target = s_all, Actions = [Resource("coins", 2), Resource("points", 1)] };

    /// <summary>With an object: not for a later finisher. Three events per player, so 17 players need more than one batch.</summary>
    private static readonly EffectSpec s_withObject = new()
    {
        Target = s_all,
        Actions = [Resource("coins", 2), Resource("points", 1), new GiveObjectAction { ObjectId = "orange" }],
    };

    /// <summary>Stands in for a goal of 5.9: when <paramref name="when"/> holds for an event, the reward is queued for everyone.</summary>
    private sealed class GoalReached(EffectSpec effect, Func<IGameEvent, bool> when) : ITriggerHandler
    {
        public IEnumerable<IGameEvent> React(SeasonState state, IGameEvent trigger, EngineContext context) =>
            when(trigger) ? [ForAllSettlements.Queue(state, context, ForAllSource.GoalReward, s_goal, "goal-reward", effect)] : [];
    }

    // «Цель достигнута завершением»: any completion reaches the goal
    private static bool ByCompletion(IGameEvent e) => e is RunCompleted;

    // The admin's adjustment with the goal's comment reaches it: a cause that touches nobody else
    private static bool ByAdmin(IGameEvent e) => e is PlayerAdjusted { Comment: GoalComment };

    private static string[] Names(int count) =>
        [.. new[] { "Вася", "Петя", "Маша", "Даша" }.Concat(Enumerable.Range(5, Math.Max(0, count - 4)).Select(i => $"Игрок {i}")).Take(count)];

    private static Scenario Season(EffectSpec effect, Func<IGameEvent, bool> when, int players = 17, int mapLength = 60, bool economy = true)
    {
        var ruleset = economy ? TestRuleset.Create().WithEconomy() : TestRuleset.Create();
        var s = Scenario.New(ruleset).WithMapLength(mapLength).WithCategory("Horror");
        foreach (var title in s_titles)
        {
            s.WithGame(title, 6, "Horror");
        }

        s.WithPlayers(Names(players));
        if (economy)
        {
            s.WithContent(EconomyScenario.ExamplePack("orange"));
        }

        return s.WithTriggers(new GoalReached(effect, when));
    }

    /// <summary>The admin's cause; returns what it queued.</summary>
    private static ForAllQueued Cause(Scenario s)
    {
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), GoalComment, PointsDelta: 1));
        ScenarioAssert.Accepted(s);
        return Assert.Single(s.LastEvents<ForAllQueued>());
    }

    private sealed record Batch(Guid CommandId, ForAllSettled Settled, IReadOnlyList<IGameEvent> Events);

    /// <summary>One <see cref="SettleForAll"/> of the oldest pending settlement; it must be accepted.</summary>
    private static Batch SettleNext(Scenario s)
    {
        var id = ForAllSettlements.Next(s.State) ?? throw new InvalidOperationException("Nothing to settle.");
        s.Act(new SettleForAll(id));
        ScenarioAssert.Accepted(s);

        // The batch is recorded first among the command's events
        var settled = Assert.IsType<ForAllSettled>(s.Last.Events[0]);
        Assert.Equal(id, settled.SettlementId);
        return new Batch(s.LastCommandId, settled, s.Last.Events);
    }

    /// <summary>Settles until nothing is pending, as the queue does after a cause.</summary>
    private static List<Batch> SettleAll(Scenario s)
    {
        var batches = new List<Batch>();
        while (ForAllSettlements.Next(s.State) is not null)
        {
            batches.Add(SettleNext(s));
            Assert.True(batches.Count <= 20, "The settlement never ends.");
        }

        return batches;
    }

    private static Guid Finish(Scenario s, string player)
    {
        s.Roll(player).Start(player);
        var runId = s.Player(player).ActiveRunId!.Value;
        s.NextRandom(3, 1).Complete(player);
        ScenarioAssert.Accepted(s);
        Assert.NotNull(s.Player(player).Finish);
        s.Advance(TimeSpan.FromHours(1));
        return runId;
    }

    private static UndoCommand Undo(Guid commandId) => new(commandId, "Ошибка админа");

    // The season as it matters to the game: the counters of finishes and points changes only grow (as UndoTests)
    private static SeasonState Comparable(SeasonState state) =>
        state with
        {
            FinishesSoFar = 0,
            PointsChanges = 0,
            Players = state.Players.SetItems(state.Players.Select(p => KeyValuePair.Create(p.Key, p.Value with { PointsTick = 0 }))),
        };

    private static int Oranges(SeasonPlayer player) => player.Wallet.Inventory.Count(o => o.ObjectId == "orange");

    // ---- EV53: a goal reached by a completion rewards all 17 ----

    [Fact]
    public void A_goal_reached_by_a_completion_rewards_all_17_players()
    {
        var s = Season(s_withObject, ByCompletion, players: 16);
        s.Roll("Вася").Start("Вася");

        // The 17th joins mid-season
        s.WithPlayers("Лёша");
        Assert.Equal(17, s.State.Players.Count);

        s.NextRandom(1, 1).Complete("Вася");
        ScenarioAssert.Accepted(s);

        // The completion only queues the reward: none of its events are in the cause command
        var cause = s.Last.Events;
        var queued = Assert.Single(cause.OfType<ForAllQueued>());
        Assert.Equal((ForAllSource.GoalReward, s_goal, "goal-reward", s_withObject), (queued.Source, queued.Reference, queued.ObjectId, queued.Effect));
        Assert.Equal(s.State.Players.Keys, queued.Players);
        Assert.Empty(cause.OfType<ObjectGiven>());
        Assert.DoesNotContain(cause, e => e is CoinsChanged c && c.PlayerId != s.PlayerId("Вася"));
        Assert.DoesNotContain(cause, e => e is PointsChanged p && p.PlayerId != s.PlayerId("Вася"));
        Assert.Equal(queued.SettlementId, ForAllSettlements.Next(s.State));
        var before = s.State;

        var batches = SettleAll(s);

        // Each batch is its own command within the 50 events, nothing cut, more than one batch for 17 × 3 events
        Assert.True(batches.Count > 1, $"17 players of 3 events each fit one command: {batches.Count} batch(es).");
        Assert.All(batches, b => Assert.InRange(b.Events.Count, 1, s.Ruleset.Effects.MaxEventsPerCommand));
        Assert.All(batches, b => Assert.DoesNotContain(b.Events, e => e is EffectChainCut));
        Assert.Equal(Enumerable.Range(1, batches.Count), batches.Select(b => b.Settled.Batch));
        Assert.Equal(batches.Count, batches.Select(b => b.CommandId).Distinct().Count());
        Assert.All(batches.SkipLast(1), b => Assert.False(b.Settled.Done));
        Assert.True(batches[^1].Settled.Done);
        Assert.Null(ForAllSettlements.Next(s.State));
        Assert.Empty(s.State.ForAll);

        // Every one of the 17 got the reward exactly once
        Assert.All(batches, b => Assert.Empty(b.Settled.Skipped));
        foreach (var (id, was) in before.Players)
        {
            var now = s.State.Players[id];
            var name = s.PlayerName(id);
            Assert.Equal((name, was.Coins + 2, was.Points + 1, Oranges(was) + 1), (name, now.Coins, now.Points, Oranges(now)));
            Assert.Single(batches, b => b.Settled.Players.Contains(id));
        }
    }

    // ---- Batches ----

    [Fact]
    public void Batches_record_their_players_in_order_and_partition_the_queued_list()
    {
        var s = Season(s_withObject, ByAdmin);
        var queued = Cause(s);

        var first = SettleNext(s);

        // After a batch the settlement waits with the rest, in order
        var pending = s.State.ForAll[queued.SettlementId];
        Assert.Equal(queued.SettlementId, pending.SettlementId);
        Assert.Equal(1, pending.Batches);
        Assert.False(pending.Halted);
        Assert.Equal(queued.Players.Except(first.Settled.Players).Except(first.Settled.Skipped), pending.Remaining);

        var batches = new List<Batch> { first };
        batches.AddRange(SettleAll(s));

        // Players and skipped of all batches together are the queued list, each once
        var reached = batches.SelectMany(b => b.Settled.Players.Concat(b.Settled.Skipped)).ToList();
        Assert.Equal(reached.Count, reached.Distinct().Count());
        Assert.Equal(queued.Players.Order(), reached.Order());

        // Batches go through the list in order, and inside a batch each player's events follow the recorded order
        var settled = batches.SelectMany(b => b.Settled.Players).ToList();
        Assert.Equal(queued.Players.Where(settled.Contains), settled);
        Assert.All(batches, b => Assert.Equal(b.Settled.Players, b.Events.OfType<ObjectGiven>().Select(o => o.PlayerId)));
        Assert.All(batches, b => Assert.Equal(b.Settled.Players, b.Events.OfType<CoinsChanged>().Select(c => c.PlayerId)));
    }

    [Fact]
    public void A_batch_never_passes_the_seasons_own_limit()
    {
        // effects.maxEventsPerCommand of the season, not only the ceiling of 50: 3 events a player, at most 10 a command
        var s = Season(s_withObject, ByAdmin, players: 5);
        s.WithRuleset(r => r with { Effects = r.Effects with { MaxEventsPerCommand = 10 } });
        var queued = Cause(s);

        var batches = SettleAll(s);

        Assert.True(batches.Count >= 2);
        Assert.All(batches, b => Assert.InRange(b.Events.Count, 1, 10));
        Assert.All(batches, b => Assert.DoesNotContain(b.Events, e => e is EffectChainCut));
        Assert.Equal(queued.Players.Order(), batches.SelectMany(b => b.Settled.Players).Order());
    }

    [Fact]
    public void A_player_alone_over_the_limit_is_settled_alone()
    {
        // 3 events a player and the ForAllSettled do not fit 3: each player goes alone (a cut, if any, only as usual)
        var s = Season(s_withObject, ByAdmin, players: 3);
        s.WithRuleset(r => r with { Effects = r.Effects with { MaxEventsPerCommand = 3 } });
        var queued = Cause(s);

        var batches = SettleAll(s);

        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.Single(b.Settled.Players));
        Assert.All(batches, b => Assert.True(b.Events.OfType<EffectChainCut>().Count() <= 1));
        Assert.Equal(queued.Players, batches.SelectMany(b => b.Settled.Players));
    }

    /// <summary>
    /// A reaction of three events in one firing (written whole or cut whole) to each player's +2 coins: a player's settlement
    /// takes 5 events.
    /// </summary>
    private sealed class CoinsEcho : ITriggerHandler, IFiringTriggerHandler
    {
        public IEnumerable<IGameEvent> React(SeasonState state, IGameEvent trigger, EngineContext context) => [];

        public IEnumerable<IReadOnlyList<IGameEvent>> ReactInFirings(SeasonState state, IGameEvent trigger, EngineContext context, int depth, int eventsBefore) =>
            trigger is CoinsChanged { Delta: 2 } coins
                ? [[.. Enumerable.Repeat<IGameEvent>(new PointsChanged(coins.PlayerId, 1, PointsReason.AdminAdjustment, null), 3)]]
                : [];
    }

    [Fact]
    public void A_player_whose_reactions_do_not_fit_waits_whole_for_the_next_batch()
    {
        // Limit 14: the batch event and two players take 11; the third's own 2 events fit, his firing of 3 does not — he
        // goes whole into the next batch instead of being settled with his chain cut (D-537, D-700)
        var s = Season(s_pointsAndCoins, ByAdmin, players: 4);
        s.WithRuleset(r => r with { Effects = r.Effects with { MaxEventsPerCommand = 14 } });
        s.WithTriggers(new GoalReached(s_pointsAndCoins, ByAdmin), new CoinsEcho());
        var queued = Cause(s);
        var before = s.State;

        var batches = SettleAll(s);

        Assert.Equal([2, 2], batches.Select(b => b.Settled.Players.Count));
        Assert.All(batches, b => Assert.DoesNotContain(b.Events, e => e is EffectChainCut));
        Assert.All(batches, b => Assert.InRange(b.Events.Count, 1, 14));
        Assert.All(queued.Players, id => Assert.Equal(before.Players[id].Points + 1 + 3, s.State.Players[id].Points));
    }

    // ---- Targets ----

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void The_first_finisher_is_never_a_target_and_a_later_one_only_for_points_and_coins(bool givesObject, bool firstApproved)
    {
        var s = Season(givesObject ? s_withObject : s_pointsAndCoins, ByAdmin, players: 4, mapLength: 4);
        var first = Finish(s, "Вася");
        if (firstApproved)
        {
            s.Approve(first);
        }

        Finish(s, "Петя");
        var vasya = s.Player("Вася");
        var petya = s.Player("Петя");

        var queued = Cause(s);

        string[] expected = givesObject ? ["Маша", "Даша"] : ["Петя", "Маша", "Даша"];
        Assert.Equal(expected.Select(s.PlayerId).Order(), queued.Players);

        SettleAll(s);

        // The first (frozen or provisional) gets nothing; the later finisher his coins, his position stays on the finish
        Assert.Equal(vasya.Coins, s.Player("Вася").Coins);
        Assert.Equal(petya.Coins + (givesObject ? 0 : 2), s.Player("Петя").Coins);
        Assert.Equal(petya.CellId, s.Player("Петя").CellId);
    }

    [Fact]
    public void In_a_settlement_self_is_the_target_so_a_later_finisher_is_not_reached_by_an_object_on_self()
    {
        // Points on the target and an object on «self»: in a settlement «self» is the player himself (D-700), so the effect is
        // not points and coins only and a later finisher is no target (D-518)
        var effect = new EffectSpec
        {
            Target = s_all,
            Actions = [Resource("points", 1), new GiveObjectAction { ObjectId = "orange", Target = ActionTarget.Self }],
        };
        var s = Season(effect, ByAdmin, players: 4, mapLength: 4);
        Finish(s, "Вася");
        Finish(s, "Петя");

        var queued = Cause(s);

        Assert.DoesNotContain(s.PlayerId("Петя"), queued.Players);
        Assert.Contains(s.PlayerId("Маша"), queued.Players);
    }

    [Fact]
    public void A_player_whose_run_of_the_effect_is_refused_is_skipped()
    {
        // A question in an effect on everyone has nobody to ask: the run is refused, the player gets nothing (D-703 (3))
        var effect = new EffectSpec
        {
            Target = s_all,
            Actions =
            [
                new RequestChoiceAction { Prompt = "Что?", Options = new ChoiceOptionsSpec { List = ["a", "b"] } },
                Resource("coins", 2),
            ],
        };
        var s = Season(effect, ByAdmin, players: 3);
        var queued = Cause(s);
        var before = s.State;

        var batches = SettleAll(s);

        Assert.Equal(queued.Players, batches.SelectMany(b => b.Settled.Skipped));
        Assert.Empty(batches.SelectMany(b => b.Settled.Players));
        Assert.True(batches[^1].Settled.Done);
        Assert.All(queued.Players, id => Assert.Equal(before.Players[id].Coins, s.State.Players[id].Coins));
        Assert.Empty(s.State.ForAll);
    }

    [Fact]
    public void An_inactive_player_is_a_target_too()
    {
        var s = Season(s_pointsAndCoins, ByAdmin, players: 4);
        s.Act(new SetPlayerInactive(s.PlayerId("Маша"), true));
        ScenarioAssert.Accepted(s);
        var coins = s.Player("Маша").Coins;

        var queued = Cause(s);
        SettleAll(s);

        Assert.Contains(s.PlayerId("Маша"), queued.Players);
        Assert.Equal(coins + 2, s.Player("Маша").Coins);
    }

    [Fact]
    public void A_player_who_finishes_first_before_his_batch_is_skipped()
    {
        var s = Season(s_withObject, ByAdmin, mapLength: 4);
        var queued = Cause(s);
        var first = SettleNext(s);
        var last = queued.Players[^1];
        var name = s.PlayerName(last);
        Assert.DoesNotContain(last, first.Settled.Players);

        // Between the batches the last player of the list finishes first: frozen targets get nothing
        Finish(s, name);
        var coins = s.Player(name).Coins;
        var oranges = Oranges(s.Player(name));

        var rest = SettleAll(s);

        Assert.Contains(last, rest.SelectMany(b => b.Settled.Skipped));
        Assert.DoesNotContain(last, rest.SelectMany(b => b.Settled.Players));
        Assert.True(rest[^1].Settled.Done);
        Assert.Equal(coins, s.Player(name).Coins);
        Assert.Equal(oranges, Oranges(s.Player(name)));
        Assert.Empty(s.State.ForAll);
    }

    // ---- Refusals ----

    [Fact]
    public void Settling_an_unknown_settlement_is_refused()
    {
        var s = Season(s_pointsAndCoins, ByAdmin, players: 3);
        Assert.Equal("forAll.unknown", RejectionCodes.ForAllUnknown);
        Assert.Equal("forAll.halted", RejectionCodes.ForAllHalted);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new SettleForAll(SequentialIds.Make(0x6E000000, 1))), RejectionCodes.ForAllUnknown);
        Assert.Null(ForAllSettlements.Next(s.State));
    }

    [Fact]
    public void Settling_a_settlement_already_done_is_refused()
    {
        var s = Season(s_pointsAndCoins, ByAdmin, players: 3);
        var queued = Cause(s);
        SettleAll(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new SettleForAll(queued.SettlementId)), RejectionCodes.ForAllUnknown);
    }

    [Fact]
    public void A_finished_season_settles_nothing()
    {
        var s = Season(s_pointsAndCoins, ByAdmin, players: 3);
        var queued = Cause(s);
        s.MoveStatusTo(SeasonStatus.Closing);
        s.AppendCraftedEvents(new SeasonStatusChanged(SeasonStatus.Closing, SeasonStatus.Finished));

        Assert.Null(ForAllSettlements.Next(s.State));
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new SettleForAll(queued.SettlementId)), RejectionCodes.SeasonClosed);
    }

    // ---- After the deadline, no flag ----

    [Fact]
    public void A_settlement_pending_at_the_deadline_is_still_settled()
    {
        var s = Season(s_withObject, ByAdmin);
        var queued = Cause(s);
        s.MoveStatusTo(SeasonStatus.Closing);
        var before = s.State;

        Assert.Equal(queued.SettlementId, ForAllSettlements.Next(s.State));
        SettleAll(s);

        Assert.Empty(s.State.ForAll);
        Assert.All(queued.Players, id => Assert.Equal(before.Players[id].Coins + 2, s.State.Players[id].Coins));
    }

    [Fact]
    public void A_settlement_needs_no_flag_of_its_own()
    {
        // Items, shop and the rest off: the sources carry their own flags
        var s = Season(s_pointsAndCoins, ByAdmin, players: 3, economy: false);
        var queued = Cause(s);
        var before = s.State;

        SettleAll(s);

        Assert.All(queued.Players, id => Assert.Equal(before.Players[id].Coins + 2, s.State.Players[id].Coins));
    }

    // ---- Undo (invariant 12) ----

    [Fact]
    public void Undoing_the_cause_is_refused_while_its_batches_stand()
    {
        var s = Season(s_withObject, ByAdmin);
        Cause(s);
        var cause = s.LastCommandId;
        var first = SettleNext(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Undo(cause)), RejectionCodes.UndoDependents);
        Assert.Equal([first.CommandId], s.Last.Rejection!.Related);

        var rest = SettleAll(s);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Undo(cause)), RejectionCodes.UndoDependents);
        Assert.Equal(new[] { first.CommandId }.Concat(rest.Select(b => b.CommandId)).Order(), s.Last.Rejection!.Related.Order());
    }

    [Fact]
    public void Undoing_an_earlier_batch_while_a_later_stands_is_refused()
    {
        var s = Season(s_withObject, ByAdmin);
        Cause(s);
        var batches = SettleAll(s);
        Assert.True(batches.Count >= 2);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Undo(batches[0].CommandId)), RejectionCodes.UndoDependents);
        Assert.Contains(batches[1].CommandId, s.Last.Rejection!.Related);
    }

    [Fact]
    public void Undoing_the_last_batch_puts_its_players_back_and_halts_the_settlement()
    {
        var s = Season(s_withObject, ByAdmin);
        var queued = Cause(s);
        SeasonState before;
        Batch last;
        do
        {
            before = s.State;
            last = SettleNext(s);
        }
        while (!last.Settled.Done);

        Assert.True(last.Settled.Batch >= 2);
        var pending = before.ForAll[queued.SettlementId];

        s.Act(Undo(last.CommandId));

        ScenarioAssert.Accepted(s);
        foreach (var id in last.Settled.Players)
        {
            Assert.Equal(Comparable(before).Players[id], Comparable(s.State).Players[id]);
        }

        // Back as pending, but halted: never settled again by itself
        var halted = s.State.ForAll[queued.SettlementId];
        Assert.Equal(pending.Remaining, halted.Remaining);
        Assert.Equal(pending.Batches, halted.Batches);
        Assert.True(halted.Halted);
        Assert.Null(ForAllSettlements.Next(s.State));
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new SettleForAll(queued.SettlementId)), RejectionCodes.ForAllHalted);
    }

    [Fact]
    public void A_halted_settlement_does_not_hold_up_a_later_one()
    {
        var s = Season(s_withObject, ByAdmin);
        Cause(s);
        var last = SettleAll(s)[^1];
        s.Act(Undo(last.CommandId));
        ScenarioAssert.Accepted(s);

        var later = Cause(s);

        Assert.Equal(later.SettlementId, ForAllSettlements.Next(s.State));
        SettleAll(s);
        Assert.False(s.State.ForAll.ContainsKey(later.SettlementId));
    }

    [Fact]
    public void Undoing_the_batches_in_reverse_and_then_the_cause_gives_the_season_back()
    {
        var s = Season(s_withObject, ByAdmin);
        var before = s.State;
        var queued = Cause(s);
        var cause = s.LastCommandId;
        var batches = SettleAll(s);

        foreach (var batch in Enumerable.Reverse(batches))
        {
            s.Act(Undo(batch.CommandId));
            ScenarioAssert.Accepted(s);
        }

        Assert.True(s.State.ForAll[queued.SettlementId].Halted);
        s.Act(Undo(cause));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.State.ForAll);
        Assert.Null(ForAllSettlements.Next(s.State));
        Assert.Equal(Comparable(before), Comparable(s.State));
    }

    [Fact]
    public void Undoing_a_cause_not_settled_yet_removes_its_settlement()
    {
        var s = Season(s_withObject, ByAdmin);
        var before = s.State;
        Cause(s);
        var cause = s.LastCommandId;

        s.Act(Undo(cause));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.State.ForAll);
        Assert.Null(ForAllSettlements.Next(s.State));
        Assert.Equal(Comparable(before), Comparable(s.State));
    }

    // ---- Replay ----

    [Fact]
    public void The_log_replays_to_the_same_settlements()
    {
        var s = Season(s_withObject, ByAdmin);
        var queued = Cause(s);

        void Replays()
        {
            var replayed = SeasonEngine.Replay(s.StoredLog);
            Assert.Equal(s.State, replayed);
            Assert.Equal(s.State.ForAll, replayed.ForAll);
        }

        Replays();
        SettleNext(s);
        Replays();
        var batches = SettleAll(s);
        Replays();

        s.Act(Undo(batches[^1].CommandId));
        ScenarioAssert.Accepted(s);
        Replays();
        Assert.True(SeasonEngine.Replay(s.StoredLog).ForAll[queued.SettlementId].Halted);
    }

    [Fact]
    public void The_queued_event_keeps_its_effect_and_writes_parameters_only_when_set()
    {
        var s = Season(s_withObject, ByAdmin);
        var queued = Cause(s);

        Assert.Equal(queued, EventCodec.Decode(EventCodec.Encode(queued)));
        Assert.Null(queued.Parameters);
        Assert.DoesNotContain("parameters", EventCodec.Encode(queued).Data, StringComparison.OrdinalIgnoreCase);

        var withParameters = queued with { Parameters = new ContentParamDictionary([new("tag", "Horror")]) };
        Assert.Equal(withParameters, EventCodec.Decode(EventCodec.Encode(withParameters)));
    }
}

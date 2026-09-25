using FsCheck.Xunit;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Effects;

/// <summary>
/// The effect chain limits (SPEC «Лимит цепочки», E2, invariant 12, D-24, D-103): reactions to reactions go at most
/// 3 levels deep and a command writes at most 50 events; beyond that the chain stops with <see cref="EffectChainCut"/>.
/// The handlers here are test effects: a point for every point (a loop), a fan-out, a quiet one.
/// </summary>
public class ChainLimitTests
{
    // Every points change of a player gives them `fanOut` more points changes of +1: the loop «толкнул на клетку,
    // которая толкает» in points
    private sealed class PointsEcho(int fanOut) : ITriggerHandler
    {
        public IEnumerable<IGameEvent> React(SeasonState state, IGameEvent trigger, EngineContext context) =>
            trigger is PointsChanged changed
                ? Enumerable.Repeat<IGameEvent>(new PointsChanged(changed.PlayerId, 1, PointsReason.AdminAdjustment, null), fanOut)
                : [];
    }

    // Reacts to nothing
    private sealed class Quiet : ITriggerHandler
    {
        public IEnumerable<IGameEvent> React(SeasonState state, IGameEvent trigger, EngineContext context) => [];
    }

    // Records what it saw: the state it was given must already hold the trigger
    private sealed class Witness : ITriggerHandler
    {
        public List<(IGameEvent Trigger, int Points)> Seen { get; } = [];

        public IEnumerable<IGameEvent> React(SeasonState state, IGameEvent trigger, EngineContext context)
        {
            if (trigger is PointsChanged changed)
            {
                Seen.Add((trigger, state.Players[changed.PlayerId].Points));
            }

            return [];
        }
    }

    private static Scenario Season(params ITriggerHandler[] handlers) =>
        Scenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя").WithTriggers(handlers);

    private static AdjustPlayer Bonus(Scenario s, int points = 5) => new(s.PlayerId("Вася"), "Бонус", PointsDelta: points);

    [Fact]
    public void Without_handlers_a_command_writes_only_its_own_events()
    {
        var s = Season();

        s.Act(Bonus(s));

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [new PlayerAdjusted(s.PlayerId("Вася"), "Бонус"), new PointsChanged(s.PlayerId("Вася"), 5, PointsReason.AdminAdjustment, null)],
            s.Last.Events);
    }

    [Fact]
    public void A_quiet_handler_changes_nothing()
    {
        var s = Season(new Quiet());

        s.Act(Bonus(s));

        Assert.Equal(2, s.Last.Events.Count);
    }

    [Fact]
    public void A_loop_stops_after_three_levels()
    {
        // The command writes 2 (the adjustment and +5) → +1 (level 1) → +1 (level 2) → +1 (level 3) → the 4th level is cut
        var s = Season(new PointsEcho(1));

        s.Act(Bonus(s));

        ScenarioAssert.Accepted(s);
        var events = s.Last.Events;
        Assert.Equal(6, events.Count);
        Assert.Equal(new EffectChainCut(EffectChainLimit.Depth, Limits.MaxEffectDepth + 1, 5), events[^1]);
        Assert.Equal(5 + 3, s.Player("Вася").Points);
    }

    [Fact]
    public void A_fan_out_stops_at_fifty_events()
    {
        // 2 + 7 + 49 would pass 50: the chain stops at level 2 when the command has written 50
        var s = Season(new PointsEcho(7));

        s.Act(Bonus(s));

        var events = s.Last.Events;
        Assert.Equal(Limits.MaxEventsPerCommand + 1, events.Count);
        Assert.Equal(new EffectChainCut(EffectChainLimit.Events, 2, Limits.MaxEventsPerCommand), events[^1]);
        Assert.Equal(5 + (Limits.MaxEventsPerCommand - 2), s.Player("Вася").Points);
    }

    [Fact]
    public void Reactions_follow_the_command_in_order_and_see_the_state_so_far()
    {
        var witness = new Witness();
        var s = Season(witness, new PointsEcho(2));

        s.Act(Bonus(s));

        // Handlers run in order on each trigger and see every reaction written before: the bonus at 5; its two echoes
        // (+1, +1) come after the witness, so the first echo is seen at 7, its own two echoes make the second one 9
        Assert.Equal([5, 7, 9], witness.Seen.Take(3).Select(x => x.Points));
        Assert.All(witness.Seen, x => Assert.Contains(x.Trigger, s.Last.Events));
    }

    [Fact]
    public void A_rejected_command_runs_no_handlers()
    {
        var witness = new Witness();
        var s = Season(new PointsEcho(1), witness);

        s.Act(new AdjustPlayer(Guid.NewGuid(), "Бонус", PointsDelta: 5));

        Assert.False(s.Last.IsAccepted);
        Assert.Empty(s.Last.Events);
        Assert.Empty(witness.Seen);
    }

    [Fact]
    public void The_log_with_reactions_and_the_cut_replays_to_the_same_state()
    {
        var s = Season(new PointsEcho(3));

        s.Act(Bonus(s)).Act(Bonus(s, -2));

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log.Select(e => EventCodec.Decode(EventCodec.Encode(e)))));
    }

    [Fact]
    public void A_cut_changes_no_state()
    {
        var s = Season();
        var before = s.State;

        Assert.Equal(before, SeasonEngine.Apply(before, new EffectChainCut(EffectChainLimit.Depth, 4, 4)));
    }

    [Fact]
    public void A_fan_out_is_cut_by_events_on_the_last_allowed_level()
    {
        // 2 + 4 + 16 = 22 by level 2; level 3 would add 64: cut by events at 50, still on level 3
        var s = Season(new PointsEcho(4));

        s.Act(Bonus(s));

        var cut = Assert.IsType<EffectChainCut>(s.Last.Events[^1]);
        Assert.Equal((EffectChainLimit.Events, 3, Limits.MaxEventsPerCommand), (cut.Limit, cut.Depth, cut.Events));
    }

    [Fact]
    public void A_chain_of_exactly_fifty_is_not_cut_when_nothing_follows()
    {
        // A handler that answers only the command's own points change with 48 reactions: 2 + 48 = 50, then silence
        var s = Season(new FirstOnly(48));

        s.Act(Bonus(s));

        Assert.Equal(Limits.MaxEventsPerCommand, s.Last.Events.Count);
        Assert.Empty(s.LastEvents<EffectChainCut>());
    }

    // Answers only the command's own points change (reason AdminAdjustment with a delta other than 1)
    private sealed class FirstOnly(int count) : ITriggerHandler
    {
        public IEnumerable<IGameEvent> React(SeasonState state, IGameEvent trigger, EngineContext context) =>
            trigger is PointsChanged { Delta: not 1 } changed
                ? Enumerable.Repeat<IGameEvent>(new PointsChanged(changed.PlayerId, 1, PointsReason.AdminAdjustment, null), count)
                : [];
    }

    // Invariant 12: whatever the handlers and the commands, a command writes exactly what an independent count of the
    // levels says — reactions level by level, cut at the 4th level or when 50 events are written, the cut last
    [Property(MaxTest = 200)]
    public void Chains_stay_within_the_limits(byte fanOut, byte[] script)
    {
        var handlers = new List<ITriggerHandler> { new PointsEcho(fanOut % 5) };
        if (fanOut % 3 == 0)
        {
            handlers.Add(new PointsEcho(1));
        }

        var perTrigger = (fanOut % 5) + (fanOut % 3 == 0 ? 1 : 0);
        var s = Season([.. handlers]);
        foreach (var b in script.Take(20))
        {
            var delta = (b % 7) - 3;
            s.Act(new AdjustPlayer(s.PlayerId(b % 2 == 0 ? "Вася" : "Петя"), "Бонус", PointsDelta: delta));
            if (!s.Last.IsAccepted)
            {
                Assert.Empty(s.Last.Events);
                continue;
            }

            var events = s.Last.Events;
            // The command's own events: the adjustment, and its points change when the delta is not 0
            var (expectedCount, expectedCut) = Expected(own: delta == 0 ? 1 : 2, pointsChanges: delta == 0 ? 0 : 1, perTrigger);

            Assert.Equal(expectedCount, events.Count(e => e is not EffectChainCut));
            Assert.Equal(expectedCut, events.OfType<EffectChainCut>().SingleOrDefault());
            Assert.True(expectedCut is null || events[^1] is EffectChainCut, "A cut that is not last.");
        }

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    // The reference: level by level, each points change answered by `perTrigger` more; cut past level 3 or at 50
    private static (int Count, EffectChainCut? Cut) Expected(int own, int pointsChanges, int perTrigger)
    {
        var total = own;
        var level = pointsChanges;
        for (var depth = 1; ; depth++)
        {
            var produced = level * perTrigger;
            if (produced == 0)
            {
                return (total, null);
            }

            if (depth > Limits.MaxEffectDepth)
            {
                return (total, new EffectChainCut(EffectChainLimit.Depth, depth, total));
            }

            if (total + produced > Limits.MaxEventsPerCommand)
            {
                return (Limits.MaxEventsPerCommand, new EffectChainCut(EffectChainLimit.Events, depth, Limits.MaxEventsPerCommand));
            }

            total += produced;
            level = produced;
        }
    }
}

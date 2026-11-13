using GameEvent.Engine.Content;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// The sources of task 5.3 and the questions of task 5.7 together (D-559, EV13): an event that asks, drawn from any source —
/// a drop's penalty, a difficulty, another player's item — is suspended and goes on after the answer exactly like the event
/// of a cell (D-511). The provisional first plays the question of his own run's event after his answer (D-553 (3)), but is
/// never asked about another player (D-557 (4)); a Бабл stops a bad event that would ask before it asks (D-484).
/// «Монетки или шаг» is the example 6 of CONTENT.md: three coins or a step.
/// </summary>
public class EventSourceSuspensionTests
{
    private static readonly string[] s_titles = ["Silent Hill", "Alan Wake", "Dead Space", "Outlast", "Amnesia", "Soma"];

    /// <summary>A bad event on a player the drawer picks: its target <c>chosen</c> is a question (D-511).</summary>
    private static readonly ObjectDefinition s_hex = ContentJson.Parse<ObjectDefinition>("""
        {
          "id": "hex", "kind": "event", "name": "Сглаз", "description": "Выбери, кто теряет 2 монетки.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "chosen", "excludeSelf": true }, "actions": [{ "type": "changeResource", "resource": "coins", "amount": -2 }] }
        }
        """);

    /// <summary>A good event without a question: one step forward, so a line of event cells loops it (D-500).</summary>
    private static readonly ObjectDefinition s_step = ContentJson.Parse<ObjectDefinition>("""
        {
          "id": "step", "kind": "event", "name": "Шаг", "description": "Шаг вперёд.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "self" }, "actions": [{ "type": "move", "steps": 1 }] }
        }
        """);

    private static ContentPack Pack(EventKind kind, string eventId, params string[] extra)
    {
        var pack = EventSetup.Pack(eventId == "hex" ? extra : [eventId, .. extra], EventSetup.Deck(kind, eventId));
        return eventId == "hex" ? pack with { Objects = [.. pack.Objects.Append(s_hex).OrderBy(o => o.Id, StringComparer.Ordinal)] } : pack;
    }

    /// <summary>A line of four steps, 6-hour games (two d4 of the pinned ruleset), events on.</summary>
    private static Scenario Season(ContentPack pack, MapGraph? map = null, Func<Ruleset, Ruleset>? rules = null)
    {
        var ruleset = TestRuleset.Create().WithEvents();
        var s = Scenario.New(rules is null ? ruleset : rules(ruleset), 42);
        s = (map is null ? s.WithMapLength(4) : s.WithMap(map)).WithCategory("Horror");
        foreach (var title in s_titles)
        {
            s.WithGame(title, 6, "Horror");
        }

        return s.WithPlayers(["Вася", "Петя", "Маша"]).WithContent(pack);
    }

    private static Guid Complete(Scenario s, string player, int[] dice, Difficulty difficulty = Difficulty.Normal)
    {
        s.Roll(player).Start(player);
        var runId = s.Player(player).ActiveRunId!.Value;
        s.NextRandom(dice).Complete(player, difficulty);
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(1));
        return runId;
    }

    private static EffectSuspended Suspended(Scenario s, string player, EventSource source)
    {
        var drawn = Assert.Single(s.LastEvents<EventDrawn>());
        Assert.Equal(source, drawn.Source);
        var suspended = Assert.Single(s.LastEvents<EffectSuspended>());
        Assert.Equal((s.PlayerId(player), drawn.ObjectId, source), (suspended.PlayerId, suspended.ObjectId, suspended.Source));
        Assert.Equal((ChoiceKind.Effect, suspended.ChoiceId), (s.Player(player).Choice!.Kind, s.Player(player).Choice!.ChoiceId));
        return suspended;
    }

    private static void AnswersCoins(Scenario s, string player, EffectSuspended suspended)
    {
        var coins = s.Player(player).Coins;

        s.Act(new MakeChoice(s.PlayerId(player), suspended.ChoiceId, "монетки"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new EffectResumed(s.PlayerId(player), suspended.ChoiceId, "монетки"), s.Last.Events[0]);
        Assert.Equal(coins + 3, s.Player(player).Coins);
        Assert.Null(s.Player(player).Choice);
        Assert.Empty(s.Player(player).Suspended);
    }

    [Fact]
    public void A_question_of_the_bad_event_of_a_drop_waits_and_the_answer_plays_its_case()
    {
        var s = Season(Pack(EventKind.Bad, "coin-or-step"));
        s.Roll("Вася").Start("Вася");
        s.Advance(TimeSpan.FromMinutes(61));

        s.NextRandom(1, 1, 0).Act(new DropRun(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        var suspended = Suspended(s, "Вася", EventSource.DropPenalty);
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
        AnswersCoins(s, "Вася", suspended);
    }

    [Fact]
    public void A_question_of_the_event_of_a_difficulty_waits_and_the_answer_plays_its_case()
    {
        var s = Season(Pack(EventKind.Good, "coin-or-step"));

        Complete(s, "Вася", [1, 1], Difficulty.Extreme);

        AnswersCoins(s, "Вася", Suspended(s, "Вася", EventSource.Difficulty));
    }

    [Fact]
    public void A_question_of_an_event_drawn_onto_another_player_waits_for_his_answer_not_the_users()
    {
        var s = Season(Pack(EventKind.Bad, "coin-or-step", "jinx"));
        var jinx = s.Give("Петя", "jinx");

        s.Use("Петя", jinx, "Вася");

        ScenarioAssert.Accepted(s);
        var suspended = Suspended(s, "Вася", EventSource.Incoming);
        Assert.Null(s.Player("Петя").Choice);
        AnswersCoins(s, "Вася", suspended);
    }

    [Fact]
    public void The_provisional_first_answers_the_question_of_his_own_runs_event_and_it_plays()
    {
        // D-553 (3): he still draws by his own run until the approval, so the answer goes on (not D-567 (8), a question
        // asked before he became first)
        var s = Season(Pack(EventKind.Good, "coin-or-step"));
        Complete(s, "Вася", [3, 1]);
        Assert.True(Targets.IsFirst(s.State, s.PlayerId("Вася")));
        Assert.False(s.Player("Вася").Finish!.Frozen);

        Complete(s, "Вася", [1, 1], Difficulty.Extreme);

        AnswersCoins(s, "Вася", Suspended(s, "Вася", EventSource.Difficulty));
    }

    [Fact]
    public void The_provisional_first_is_not_asked_about_another_player()
    {
        // D-557 (4): his event reaches him alone; a target chosen among the others has nobody to ask about
        var s = Season(Pack(EventKind.Good, "hex"));
        Complete(s, "Вася", [3, 1]);
        s.WithCoins("Петя", 10).WithCoins("Маша", 10);

        Complete(s, "Вася", [1, 1], Difficulty.Extreme);

        Assert.Equal(("hex", EventSource.Difficulty), (Assert.Single(s.LastEvents<EventDrawn>()).ObjectId, s.LastEvents<EventDrawn>().Single().Source));
        Assert.Empty(s.LastEvents<EffectSuspended>());
        Assert.Null(s.Player("Вася").Choice);
        Assert.Equal((10, 10), (s.Player("Петя").Coins, s.Player("Маша").Coins));
    }

    [Fact]
    public void A_bubble_stops_a_bad_event_that_would_ask_and_nothing_waits()
    {
        var s = Season(Pack(EventKind.Bad, "coin-or-step", "babl", "bubble-effect", "sack-bad"));
        s.Bubbled("Вася");
        s.Roll("Вася").Start("Вася");
        s.Advance(TimeSpan.FromMinutes(61));

        s.NextRandom(1, 1, 0).Act(new DropRun(s.PlayerId("Вася")));

        // The penalty of a drop is never stopped (D-484 (1)): it asks; the bad event of his item is stopped before it asks
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<BadEventIntercepted>());
        AnswersCoins(s, "Вася", Suspended(s, "Вася", EventSource.DropPenalty));

        s.Give("Вася", "sack-bad");
        s.Used("Вася", "sack-bad");

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<BadEventIntercepted>());
        Assert.Empty(s.LastEvents<EffectSuspended>());
        Assert.Null(s.Player("Вася").Choice);
    }

    [Fact]
    public void A_question_on_the_stop_of_an_own_move_waits_at_the_first_level_and_its_continuation_loops_as_without_it()
    {
        // D-567 (4), D-559 (1): the stop of a run's own move draws inside its command but reacts to the move, level 1, as in
        // the chain — the loop «step → event cell → step» gets as many levels before the cut whether e1 asks or not
        int Loop(string first, bool asks)
        {
            var map = MapBuilder.New().EventCell("e1", "first")
                .EventCell("e2", "loop").EventCell("e3", "loop").EventCell("e4", "loop").EventCell("e5", "loop").EventCell("e6", "loop")
                .Path("start", "a", "e1", "e2", "e3", "e4", "e5", "e6", "b", "c", "finish")
                .Build();
            var pack = new ContentPack
            {
                Objects = [.. Pack(EventKind.Good, "coin-or-step").Objects.Append(s_step).OrderBy(o => o.Id, StringComparer.Ordinal)],
                Wheels = [EventScenario.Deck(EventKind.Good, "first", first), EventScenario.Deck(EventKind.Good, "loop", "step")],
            };
            var s = Season(pack, map);

            Complete(s, "Вася", [1, 1]);
            if (!asks)
            {
                Assert.Empty(s.LastEvents<EffectSuspended>());
                Assert.Single(s.LastEvents<EffectChainCut>());
                return s.LastEvents<EventDrawn>().Count();
            }

            Assert.Equal("e1", s.Player("Вася").CellId);
            var suspended = Assert.Single(s.LastEvents<EffectSuspended>());
            Assert.Equal((EventSource.Cell, 1), (suspended.Source, suspended.Depth));

            s.Act(new MakeChoice(s.PlayerId("Вася"), suspended.ChoiceId, "шаг"));

            ScenarioAssert.Accepted(s);
            Assert.Single(s.LastEvents<EffectChainCut>());
            return 1 + s.LastEvents<EventDrawn>().Count();
        }

        var without = Loop("step", asks: false);

        Assert.Equal(Limits.MaxEffectDepth, without);
        Assert.Equal(without, Loop("coin-or-step", asks: true));
    }

    [Fact]
    public void A_question_of_the_price_of_a_paid_reroll_waits_and_the_new_roll_is_made_at_once()
    {
        // D-556: the price is drawn before the new roll; its question waits, the new roll does not (the answer acts on the next one)
        var s = Season(
            Pack(EventKind.Bad, "coin-or-step"),
            rules: r => r with { Roll = r.Roll with { FreeRerollsPerRoll = 0, RerollCost = new RerollCost { Kind = RerollCostKind.BadEvent } } });
        s.Roll("Вася");

        s.NextRandom(0).Act(new Reroll(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        var suspended = Suspended(s, "Вася", EventSource.RerollCost);
        Assert.Equal(0, suspended.Depth);
        Assert.Single(s.LastEvents<GameRolled>());
        Assert.NotNull(s.Player("Вася").Offer);
        AnswersCoins(s, "Вася", suspended);
        Assert.NotNull(s.Player("Вася").Offer);
    }

    [Fact]
    public void A_question_of_an_event_drawn_by_the_players_own_item_waits_for_his_answer()
    {
        var s = Season(Pack(EventKind.Bad, "coin-or-step", "sack-bad"));
        s.Give("Вася", "sack-bad");

        s.Used("Вася", "sack-bad");

        ScenarioAssert.Accepted(s);
        var suspended = Suspended(s, "Вася", EventSource.Item);
        Assert.Equal(0, suspended.Depth);
        AnswersCoins(s, "Вася", suspended);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_drop_or_a_completion_whose_event_asked_is_undone_before_the_answer_and_not_after_it(bool drop)
    {
        // Invariant 13, EV38: the whole command goes, its question with it; once answered, the answer holds the undo
        Scenario Asked()
        {
            var s = Season(Pack(drop ? EventKind.Bad : EventKind.Good, "coin-or-step"));
            s.Roll("Вася").Start("Вася");
            s.Advance(TimeSpan.FromMinutes(61));
            if (drop)
            {
                s.NextRandom(1, 1, 0).Act(new DropRun(s.PlayerId("Вася")));
            }
            else
            {
                s.NextRandom(1, 1, 0).Complete("Вася", Difficulty.Extreme);
            }

            ScenarioAssert.Accepted(s);
            Suspended(s, "Вася", drop ? EventSource.DropPenalty : EventSource.Difficulty);
            return s;
        }

        var before = Asked();
        var command = before.LastCommandId;
        var run = before.State.Runs.Values.Single(r => r.PlayerId == before.PlayerId("Вася")).RunId;

        before.Act(new UndoCommand(command, "Ошибка"));

        ScenarioAssert.Accepted(before);
        Assert.Empty(before.Player("Вася").Suspended);
        Assert.Null(before.Player("Вася").Choice);
        Assert.Equal((TurnPhase.Playing, (Guid?)run), (before.Player("Вася").Phase, before.Player("Вася").ActiveRunId));

        var after = Asked();
        command = after.LastCommandId;
        AnswersCoins(after, "Вася", after.LastEvents<EffectSuspended>().Single());
        var answer = after.LastCommandId;

        after.Act(new UndoCommand(command, "Ошибка"));

        Assert.Equal(RejectionCodes.UndoDependents, after.Last.Rejection!.Code);
        Assert.Contains(answer, after.Last.Rejection.Related);
    }

    [Fact]
    public void A_replay_of_a_question_of_a_source_and_its_answer_gives_the_same_state()
    {
        var s = Season(Pack(EventKind.Good, "coin-or-step"));
        Complete(s, "Вася", [1, 1], Difficulty.Extreme);
        var suspended = Suspended(s, "Вася", EventSource.Difficulty);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log.Select(e => EventCodec.Decode(EventCodec.Encode(e)))));

        AnswersCoins(s, "Вася", suspended);

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log.Select(e => EventCodec.Decode(EventCodec.Encode(e)))));
    }
}

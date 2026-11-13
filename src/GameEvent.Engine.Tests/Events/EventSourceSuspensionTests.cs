using GameEvent.Engine.Content;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

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

    private static ContentPack Pack(EventKind kind, string eventId, params string[] extra)
    {
        var pack = EventSetup.Pack(eventId == "hex" ? extra : [eventId, .. extra], EventSetup.Deck(kind, eventId));
        return eventId == "hex" ? pack with { Objects = [.. pack.Objects.Append(s_hex).OrderBy(o => o.Id, StringComparer.Ordinal)] } : pack;
    }

    /// <summary>A line of four steps, 6-hour games (two d4 of the pinned ruleset), events on.</summary>
    private static Scenario Season(ContentPack pack)
    {
        var s = Scenario.New(TestRuleset.Create().WithEvents(), 42).WithMapLength(4).WithCategory("Horror");
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

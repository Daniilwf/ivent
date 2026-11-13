using GameEvent.Engine.Content;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Decks;

/// <summary>
/// The chain limit on events (SPEC «Лимит цепочки», invariant 8, EV31, EV47): a draw and the actions of its event are one
/// level (D-500), so a loop «event cell → push → event cell» is cut with <see cref="EffectChainCut"/> on the fourth; a push
/// of an event never reaches the finish (EV50, D-322).
/// </summary>
public class ChainTests
{
    /// <summary>Asks forward or not: «вперёд» pushes three cells on, onto the next event cell of <see cref="Loop"/>.</summary>
    private static readonly ObjectDefinition s_leap = ContentJson.Parse<ObjectDefinition>("""
        {
          "id": "leap", "kind": "event", "name": "Прыжок", "description": "Прыгнешь на 3 клетки вперёд?",
          "rarity": "common", "price": null, "hostile": false,
          "effect": {
            "target": { "selector": "self" },
            "outcomes": {
              "choice": { "prompt": "Прыгнешь?", "options": { "list": ["вперёд", "стоять"] } },
              "cases": [
                { "option": "вперёд", "actions": [{ "type": "move", "steps": 3 }] },
                { "option": "стоять", "actions": [] }
              ]
            }
          }
        }
        """);

    /// <summary>An event cell every third cell: each gamble on 4–6 pushes three cells on, onto the next event cell.</summary>
    private static MapGraph Loop(string first = "risky") => MapBuilder.New()
        .EventCell("e1", first).EventCell("e2", "risky").EventCell("e3", "risky").EventCell("e4", "risky").EventCell("e5", "risky")
        .Path("start", "e1", "a1", "b1", "e2", "a2", "b2", "e3", "a3", "b3", "e4", "a4", "b4", "e5", "a5", "finish")
        .Build();

    private static Scenario Season(MapGraph map)
    {
        var s = EventScenario.New().WithMap(map).WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithPlayers("Вася");
        var pack = EventScenario.Pack(["gamble", "orange"], EventScenario.Deck(EventKind.Risky, null, "gamble"), EventScenario.Deck(EventKind.Good, "ask", "leap"));
        return s.WithContent(pack with { Objects = [.. pack.Objects.Append(s_leap).OrderBy(o => o.Id, StringComparer.Ordinal)] });
    }

    private static void CutOnTheFourthLevel(Scenario s, int draws = 3)
    {
        ScenarioAssert.Accepted(s);
        Assert.Equal(draws, s.LastEvents<EventDrawn>().Count());
        Assert.Equal(EffectChainLimit.Depth, Assert.IsType<EffectChainCut>(s.Last.Events[^1]).Limit);
        Assert.Equal("e4", s.Player("Вася").CellId);
    }

    [Fact]
    public void A_loop_of_event_cells_pushing_onto_event_cells_is_cut_on_the_fourth_level()
    {
        var s = Season(Loop());
        s.Give("Вася", "orange");

        // Each level: the ticket of the draw, then the d6 of the gamble
        s.NextRandom(0, 6, 0, 6, 0, 6, 0, 6).Used("Вася", "orange");

        ScenarioAssert.Accepted(s);
        Assert.Equal(3, s.LastEvents<EventDrawn>().Count());
        Assert.Equal(EffectChainLimit.Depth, Assert.IsType<EffectChainCut>(s.Last.Events[^1]).Limit);
        Assert.Equal("e4", s.Player("Вася").CellId);

        // The fourth draw is not written: the log keeps results, so a replay never needs it
        Assert.DoesNotContain(s.Last.Events.SkipWhile(e => e is not EffectChainCut), e => e is EventDrawn);
    }

    [Fact]
    public void The_loop_from_the_stop_of_a_runs_own_move_is_cut_on_the_fourth_level_too()
    {
        // D-559 (1): the stop is drawn inside the completion but reacts to its move — three draws, as from an item
        var s = Season(Loop());
        s.Roll("Вася").Start("Вася");

        // The d4 of the run (one step onto e1), then each level: the ticket of the draw and the d6 of the gamble
        s.NextRandom(1, 0, 6, 0, 6, 0, 6, 0, 6).Complete("Вася");

        CutOnTheFourthLevel(s);
    }

    [Fact]
    public void The_loop_from_the_stop_of_a_drops_penalty_move_is_cut_on_the_fourth_level_too()
    {
        // D-559 (1): the penalty's 2 back from b1 stop on e1 inside the drop; its own bad event (no bad deck: a miss) follows
        var s = Season(Loop());
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Позиция для теста", CellId: "b1"));
        s.Roll("Вася").Start("Вася");
        s.Advance(TimeSpan.FromMinutes(61));

        s.NextRandom(1, 1, 0, 6, 0, 6, 0, 6, 0, 6).Act(new DropRun(s.PlayerId("Вася")));

        Assert.Single(s.LastEvents<EventDrawMissed>());
        CutOnTheFourthLevel(s);
    }

    [Fact]
    public void A_question_on_the_stop_of_a_runs_own_move_leaves_its_loop_the_same_levels_after_the_answer()
    {
        // D-559 (1), D-567 (4): the question waits at level 1, the continuation reacts from 2 — as without the question
        var s = Season(Loop(first: "ask"));
        s.Roll("Вася").Start("Вася");
        s.NextRandom(1).Complete("Вася");
        var suspended = Assert.Single(s.LastEvents<EffectSuspended>());
        Assert.Equal((EventSource.Cell, 1), (suspended.Source, suspended.Depth));
        Assert.Equal("e1", s.Player("Вася").CellId);

        s.NextRandom(0, 6, 0, 6, 0, 6).Act(new MakeChoice(s.PlayerId("Вася"), suspended.ChoiceId, "вперёд"));

        // One draw before the answer, two after it: three in all, as in the loop without a question
        CutOnTheFourthLevel(s, draws: 2);
    }

    [Fact]
    public void An_event_push_stops_before_the_finish()
    {
        var s = Season(MapBuilder.New().EventCell("e", "risky").Path("start", "a", "e", "b", "finish").Build());
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Позиция для теста", CellId: "a"));
        s.Give("Вася", "orange");

        s.NextRandom(0, 6).Used("Вася", "orange");

        Assert.Single(s.LastEvents<EventDrawn>());
        Assert.Equal("b", s.Player("Вася").CellId);
        Assert.Null(s.Player("Вася").Finish);
        Assert.DoesNotContain(s.LastEvents<IGameEvent>(), e => e is Engine.Finish.PlayerFinished);
    }
}

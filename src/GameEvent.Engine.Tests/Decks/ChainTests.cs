using GameEvent.Engine.Decks;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Decks;

/// <summary>
/// The chain limit on events (SPEC «Лимит цепочки», invariant 8, EV31, EV47): a draw and the actions of its event are one
/// level (D-500), so a loop «event cell → push → event cell» is cut with <see cref="EffectChainCut"/> on the fourth; a push
/// of an event never reaches the finish (EV50, D-322).
/// </summary>
public class ChainTests
{
    private static Scenario Season(MapGraph map)
    {
        var s = EventScenario.New().WithMap(map).WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithPlayers("Вася");
        return s.WithContent(EventScenario.Pack(["gamble", "orange"], EventScenario.Deck(EventKind.Risky, null, "gamble")));
    }

    [Fact]
    public void A_loop_of_event_cells_pushing_onto_event_cells_is_cut_on_the_fourth_level()
    {
        // An event cell every third cell: each gamble on 4–6 pushes three cells on, onto the next event cell
        var map = MapBuilder.New()
            .EventCell("e1", "risky").EventCell("e2", "risky").EventCell("e3", "risky").EventCell("e4", "risky").EventCell("e5", "risky")
            .Path("start", "e1", "a1", "b1", "e2", "a2", "b2", "e3", "a3", "b3", "e4", "a4", "b4", "e5", "a5", "finish")
            .Build();
        var s = Season(map);
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

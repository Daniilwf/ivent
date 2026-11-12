using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// Acceptance of docs/CONTENT.md «Примеры ивентов» 6 «Монетки или шаг» (EV13, D-511): published from the doc as written,
/// drawn on a stop on an event cell, the question waits on the server and the common choice plays the case it names.
/// </summary>
public class SuspendedEffectContentTests
{
    private static Scenario Season()
    {
        var s = EventScenario.New()
            .WithMap(MapBuilder.New().EventCell("e", "good").Path("start", "a", "e", "b", "c", "finish").Build())
            .WithCategory("Horror").WithGame("Silent Hill", 3, "Horror")
            .WithPlayers("Вася", "Петя");
        s.WithContent(EventScenario.Pack(["coin-or-step", "orange"], EventScenario.Deck(EventKind.Good, null, "coin-or-step")));
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Позиция для теста", CellId: "a"));
        s.Give("Вася", "orange");
        return s;
    }

    [Theory]
    [InlineData("монетки", 3, "e")]
    [InlineData("шаг", 0, "b")]
    public void The_example_asks_on_the_cell_and_the_answer_plays_its_case(string option, int coinsGained, string cell)
    {
        var s = Season();
        var coins = s.Player("Вася").Coins;

        s.NextRandom(0).Used("Вася", "orange");

        ScenarioAssert.Accepted(s);
        var suspended = Assert.Single(s.LastEvents<EffectSuspended>());
        Assert.Equal(("coin-or-step", SuspendedStep.Outcome, EventKind.Good), (suspended.ObjectId, suspended.Step, suspended.Kind));
        Assert.Equal(new[] { "монетки", "шаг" }, suspended.Options.Select(o => o.Id));
        Assert.Equal(ChoiceKind.Effect, s.Player("Вася").Choice!.Kind);
        Assert.Equal(coins, s.Player("Вася").Coins);

        s.Act(new MakeChoice(s.PlayerId("Вася"), suspended.ChoiceId, option));

        ScenarioAssert.Accepted(s);
        Assert.Equal(coins + coinsGained, s.Player("Вася").Coins);
        Assert.Equal(cell, s.Player("Вася").CellId);
        Assert.Null(s.Player("Вася").Choice);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log.Select(e => EventCodec.Decode(EventCodec.Encode(e)))));
    }
}

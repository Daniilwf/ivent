using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Kernel;

/// <summary>The frozen formats of an event that waits for its answer (D-511, task 5.7).</summary>
public partial class EventFormatTests
{
    private static TheoryData<string, IGameEvent, int, string> WithSuspensions(TheoryData<string, IGameEvent, int, string> data)
    {
        foreach (var (type, sample, json) in SuspensionSamples())
        {
            data.Add(type, sample, 1, json);
        }

        return data;
    }

    private static IEnumerable<(string Type, IGameEvent Sample, string Json)> SuspensionSamples() =>
    [
        (
            "effect-suspended",
            new EffectSuspended(
                s_player, s_run, "genre-gift", EventKind.Good, SuspendedStep.Action, s_player, 3, 2,
                [new ChoiceOption("Horror", Game: null), new ChoiceOption("RPG", Game: null)], 3, null, s_at),
            """{"playerId":"10000000-0000-0000-0000-000000000001","choiceId":"00000000-0000-0000-0000-000000000001","objectId":"genre-gift","kind":"good","step":"action","targetId":"10000000-0000-0000-0000-000000000001","nextAction":3,"depth":2,"options":[{"id":"Horror","game":null},{"id":"RPG","game":null}],"roll":3,"choice":null,"dueAt":"2026-10-01T12:30:00+00:00"}"""
        ),
        ("effect-resumed", new EffectResumed(s_player, s_run, "RPG"), """{"playerId":"10000000-0000-0000-0000-000000000001","choiceId":"00000000-0000-0000-0000-000000000001","optionId":"RPG"}"""),
        ("effect-move-skipped", new EffectMoveSkipped(s_player, "crossroads"), """{"playerId":"10000000-0000-0000-0000-000000000001","objectId":"crossroads"}"""),
    ];

    [Fact]
    public void A_question_of_an_event_writes_its_source_only_when_set()
    {
        // D-559: the source of the event (D-548) goes with its question, so the suspensions of 5.7 keep their frozen format (the sample above)
        var suspended = new EffectSuspended(
            s_player, s_run, "coin-or-step", EventKind.Good, SuspendedStep.Outcome, s_player, 0, 0,
            [new ChoiceOption("coins", Game: null), new ChoiceOption("step", Game: null)], null, null, null)
        {
            Source = Engine.Decks.EventSource.Difficulty,
        };
        var stored = new StoredEvent(
            "effect-suspended",
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","choiceId":"00000000-0000-0000-0000-000000000001","objectId":"coin-or-step","kind":"good","step":"outcome","targetId":"10000000-0000-0000-0000-000000000001","nextAction":0,"depth":0,"options":[{"id":"coins","game":null},{"id":"step","game":null}],"roll":null,"choice":null,"dueAt":null,"source":"difficulty"}""");

        Assert.Equal(stored, EventCodec.Encode(suspended));
        Assert.Equal(suspended, EventCodec.Decode(stored));
    }
}

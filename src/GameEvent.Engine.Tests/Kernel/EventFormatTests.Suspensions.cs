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
}

using GameEvent.Engine.Events;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Tests.Kernel;

/// <summary>The frozen formats of the events of stage 5 (D-500…).</summary>
public partial class EventFormatTests
{
    private static TheoryData<string, IGameEvent, int, string> WithEvents(TheoryData<string, IGameEvent, int, string> data)
    {
        foreach (var (type, sample, json) in EventSamples())
        {
            data.Add(type, sample, 1, json);
        }

        return data;
    }

    private static IEnumerable<(string Type, IGameEvent Sample, string Json)> EventSamples() =>
    [
        ("event-drawn", new EventDrawn(s_player, "risky", "gamble", EventKind.Risky, EventSource.Cell), """{"playerId":"10000000-0000-0000-0000-000000000001","deck":"risky","objectId":"gamble","kind":"risky","source":"cell"}"""),
        ("event-draw-missed", new EventDrawMissed(s_player, "horror-bad", EventKind.Bad, EventSource.Item), """{"playerId":"10000000-0000-0000-0000-000000000001","deck":"horror-bad","kind":"bad","source":"item"}"""),
    ];
}

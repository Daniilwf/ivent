using GameEvent.Engine.Decks;
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

    [Fact]
    public void A_manual_effect_of_an_empty_deck_is_stored_in_the_frozen_format_with_the_new_kinds_and_source()
    {
        var missed = new Engine.Effects.ManualEffectCreated(s_run, s_player, EventKind.Risky, Engine.Effects.ManualEffectSource.Event, null);
        var stored = new StoredEvent(
            "manual-effect-created",
            2,
            """{"effectId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","drawEvent":"risky","source":"event","runId":null}""");

        Assert.Equal(stored, EventCodec.Encode(missed));
        Assert.Equal(missed, EventCodec.Decode(stored));
        Assert.Equal(EventKind.Special, ((Engine.Effects.ManualEffectCreated)EventCodec.Decode(stored with { Data = stored.Data.Replace("risky", "special", StringComparison.Ordinal) })).DrawEvent);
    }
}

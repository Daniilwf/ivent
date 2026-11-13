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
        ("run-condition-checked", new Engine.Runs.RunConditionChecked(s_player, s_player, Engine.Runs.Difficulty.Hard, true, [new Engine.Runs.Die(6, 5)], Waived: true), """{"runId":"10000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","difficulty":"hard","met":true,"dice":[{"sides":6,"value":5}],"waived":true}"""),
        ("bad-event-intercepted", new BadEventIntercepted(s_player, s_object, "bubble-effect", EventSource.Incoming, "horror-week"), """{"playerId":"10000000-0000-0000-0000-000000000001","instanceId":"60000000-0000-0000-0000-000000000001","objectId":"bubble-effect","source":"incoming","eventId":"horror-week"}"""),
    ];

    [Theory]
    [InlineData(EventSource.Difficulty, "difficulty")]
    [InlineData(EventSource.Incoming, "incoming")]
    [InlineData(EventSource.RerollCost, "rerollCost")]
    [InlineData(EventSource.DropPenalty, "dropPenalty")]
    public void The_sources_of_task_5_3_are_stored_by_name(EventSource source, string name)
    {
        var stored = EventCodec.Encode(new EventDrawMissed(s_player, "bad", EventKind.Bad, source));

        Assert.Contains($"\"source\":\"{name}\"", stored.Data, StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_drawn_onto_a_player_by_another_writes_who_and_whether_hostile()
    {
        // D-548: written only when set, so the draws of 5.1 keep their frozen format (the sample above)
        var drawn = new EventDrawn(s_player, "bad", "horror-week", EventKind.Bad, EventSource.Incoming) { FromPlayerId = s_other, Hostile = true };
        var stored = new StoredEvent(
            "event-drawn",
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","deck":"bad","objectId":"horror-week","kind":"bad","source":"incoming","fromPlayerId":"10000000-0000-0000-0000-000000000002","hostile":true}""");

        Assert.Equal(stored, EventCodec.Encode(drawn));
        Assert.Equal(drawn, EventCodec.Decode(stored));
    }

    [Fact]
    public void Roll_changes_left_waiting_by_a_paid_reroll_are_written_only_when_there_are_some()
    {
        // D-556: the event of a paid reroll gives its changes to the roll in progress and leaves the older ones waiting
        var older = new Engine.Inventory.RollModifier("pick-of-three", null, 3, Hostile: false);
        var applied = new Engine.Inventory.RollModifiersApplied(s_player, [new Engine.Inventory.RollModifier("forced-genre", null, null, Hostile: false)]) { Waiting = [older] };
        var stored = EventCodec.Encode(applied);

        Assert.Contains("\"waiting\":[{\"objectId\":\"pick-of-three\"", stored.Data, StringComparison.Ordinal);
        Assert.Equal(applied, EventCodec.Decode(stored));
        Assert.DoesNotContain("waiting", EventCodec.Encode(applied with { Waiting = [] }).Data, StringComparison.Ordinal);
    }

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

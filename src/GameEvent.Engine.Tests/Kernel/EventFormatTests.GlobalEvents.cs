using GameEvent.Engine.Content;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Tests.Kernel;

/// <summary>The frozen formats of global events (D-517, D-518, task 5.9).</summary>
public partial class EventFormatTests
{
    private static TheoryData<string, IGameEvent, int, string> WithGlobalEvents(TheoryData<string, IGameEvent, int, string> data)
    {
        foreach (var (type, sample, json) in GlobalEventSamples())
        {
            data.Add(type, sample, 1, json);
        }

        return data;
    }

    private static readonly ObjectDefinition s_globalDefinition = new()
    {
        Id = "coin-rain",
        Kind = ObjectKind.Event,
        Name = "Монетный дождь",
        Description = "Всем по монетке.",
        Effect = new EffectSpec { Actions = [new ChangeResourceAction { Resource = "coins", Amount = ContentValue.Of(1) }] },
    };

    private static IEnumerable<(string Type, IGameEvent Sample, string Json)> GlobalEventSamples() =>
    [
        (
            "global-event-launched",
            new GlobalEventLaunched(s_run, "coin-rain", s_globalDefinition, s_at, s_at.AddHours(72))
            {
                Goal = new GlobalEventGoal { Metric = GoalMetric.Hours, Target = 50, Reward = new EffectSpec { Actions = [new ChangeResourceAction { Resource = "coins", Amount = ContentValue.Of(5) }] } },
                Kind = EventKind.Good,
            },
            """{"globalEventId":"00000000-0000-0000-0000-000000000001","objectId":"coin-rain","definition":{"id":"coin-rain","kind":"event","name":"Монетный дождь","description":"Всем по монетке.","rarity":null,"price":null,"window":null,"hostile":false,"stackable":false,"manual":false,"proof":null,"scope":null,"effect":{"target":null,"trigger":null,"duration":null,"condition":null,"intercept":null,"actions":[{"type":"changeResource","resource":"coins","amount":1,"target":null}],"outcomes":null}},"startedAt":"2026-10-01T12:30:00+00:00","endsAt":"2026-10-04T12:30:00+00:00","goal":{"metric":"hours","target":50,"reward":{"target":null,"trigger":null,"duration":null,"condition":null,"intercept":null,"actions":[{"type":"changeResource","resource":"coins","amount":5,"target":null}],"outcomes":null}},"kind":"good"}"""
        ),
        ("global-event-ended", new GlobalEventEnded(s_run, GlobalEventEndReason.Timer), """{"globalEventId":"00000000-0000-0000-0000-000000000001","reason":"timer"}"""),
        ("global-progress-changed", new GlobalProgressChanged(s_run, s_game, GlobalProgressReason.HoursCorrected, -2.5m, 47.5m), """{"globalEventId":"00000000-0000-0000-0000-000000000001","runId":"20000000-0000-0000-0000-000000000001","reason":"hoursCorrected","delta":-2.5,"total":47.5}"""),
        ("global-goal-reached", new GlobalGoalReached(s_run, s_game), """{"globalEventId":"00000000-0000-0000-0000-000000000001","runId":"20000000-0000-0000-0000-000000000001"}"""),
    ];
}

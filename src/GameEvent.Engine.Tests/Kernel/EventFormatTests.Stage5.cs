using System.Reflection;
using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Tests.Kernel;

/// <summary>
/// Task 5.15: every event of stage 5 has its frozen sample (<see cref="Every_event_type_has_a_golden_sample"/> holds the
/// types), every value of an enum such an event stores is frozen too — a renamed value fails here, not in a stored season —
/// and the snapshots an undo stores of the state of stage 5 (polls, global events, settlements for everyone) keep their
/// form. Written by <c>test-author</c> from the SPEC («События в логе не переписываются»).
/// </summary>
public partial class EventFormatTests
{
    // The mechanics of stage 5: their events by namespace, and the events of other namespaces the stage brought
    private static readonly string[] s_stage5Namespaces =
    [
        "GameEvent.Engine.Decks", "GameEvent.Engine.Polls", "GameEvent.Engine.Interactions", "GameEvent.Engine.Duels",
        "GameEvent.Engine.Challenges", "GameEvent.Engine.Suggestions", "GameEvent.Engine.GlobalEvents",
    ];

    private static readonly Type[] s_stage5Others =
    [
        typeof(ForAllQueued), typeof(ForAllSettled), typeof(EffectSuspended), typeof(EffectResumed), typeof(EffectMoveSkipped),
        typeof(Engine.Proofs.IrlProofSubmitted), typeof(Engine.Proofs.IrlProofReviewed), typeof(Engine.Runs.RunConditionChecked),
    ];

    private static bool IsStage5(Type type) => s_stage5Namespaces.Contains(type.Namespace) || s_stage5Others.Contains(type);

    /// <summary>
    /// The values of the enums of stage 5 events that the samples of each mechanic leave out, frozen like
    /// <see cref="ValueSamples"/>: a value is stored by its name wherever it is, so one sample per value freezes it. A value
    /// only the state holds (a declined invitation, an active duel) is stored by an undo snapshot: its sample is a
    /// <see cref="Engine.Undo.CommandUndone"/> holding it.
    /// </summary>
    public static TheoryData<string, IGameEvent, string> Stage5ValueSamples() => new()
    {
        { "duel-settled", new Engine.Duels.DuelSettled(s_run, Engine.Duels.DuelStatus.Won, Engine.Duels.DuelEnd.Completed, s_player, s_game, 10, s_at), "\"status\":\"won\",\"end\":\"completed\"" },
    };


    [Theory]
    [MemberData(nameof(Stage5ValueSamples))]
    public void Stage_5_values_are_stored_in_the_frozen_format(string type, IGameEvent sample, string jsonOrFragment)
    {
        var stored = EventCodec.Encode(sample);

        Assert.Equal(type, stored.Type);
        Assert.Contains(jsonOrFragment, stored.Data, StringComparison.Ordinal);
        Assert.Equal(sample, EventCodec.Decode(stored));
    }

    [Fact]
    public void Every_value_of_an_enum_a_stage_5_event_stores_has_a_frozen_sample()
    {
        // The enums an event of stage 5 holds, in itself or in the records it carries; each value seen in some frozen sample
        var samples = Samples().Select(r => r.Data.Item2)
            .Concat(ValueSamples().Select(r => r.Data.Item2))
            .Concat(DuelValueSamples().Select(r => r.Data.Item2))
            .Concat(EconomyVariants().Select(r => r.Data.Item2))
            .Concat(Stage5ValueSamples().Select(r => r.Data.Item2));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            CollectValues(sample, seen, depth: 0);
        }

        var enums = new HashSet<Type>();
        foreach (var type in EventCatalog.Types.Where(IsStage5))
        {
            CollectEnums(type, enums, depth: 0);
        }

        var missing = enums
            .SelectMany(e => Enum.GetNames(e).Select(n => $"{e.Name}.{n}"))
            .Where(v => !seen.Contains(v))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(missing.Count == 0, $"Values without a frozen sample: {string.Join(", ", missing)}.");
    }

    // The enums of stage 5: those of its mechanics and those it added elsewhere; content definitions are frozen by their own
    // format (docs/CONTENT.md, the content schema), the core enums by the samples of their stages
    private static bool IsStage5Enum(Type type) =>
        s_stage5Namespaces.Contains(type.Namespace)
        || type == typeof(Engine.Decks.EventSource) || type == typeof(SuspendedStep) || type == typeof(ForAllSource)
        || type == typeof(Engine.Proofs.IrlProofStatus) || type == typeof(EventKind);

    private static bool IsEngineRecord(Type type) =>
        type.IsClass && type != typeof(string) && type.Namespace?.StartsWith("GameEvent.Engine", StringComparison.Ordinal) == true;

    private static void CollectEnums(Type type, HashSet<Type> enums, int depth)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            if (property.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is { Condition: System.Text.Json.Serialization.JsonIgnoreCondition.Always })
            {
                continue;
            }

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var element = propertyType.IsGenericType ? propertyType.GetGenericArguments()[0] : propertyType;
            element = Nullable.GetUnderlyingType(element) ?? element;
            if (element.IsEnum && IsStage5Enum(element))
            {
                enums.Add(element);
            }
            else if (depth < 2 && IsEngineRecord(element) && !element.IsAbstract)
            {
                CollectEnums(element, enums, depth + 1);
            }
        }
    }

    private static void CollectValues(object instance, HashSet<string> seen, int depth)
    {
        if (depth > 3)
        {
            return;
        }

        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            switch (property.GetValue(instance))
            {
                case Enum e:
                    seen.Add($"{e.GetType().Name}.{e}");
                    break;
                case string:
                    break;
                case System.Collections.IEnumerable items:
                    foreach (var item in items.Cast<object?>().Where(i => i is not null))
                    {
                        if (item is Enum ie)
                        {
                            seen.Add($"{ie.GetType().Name}.{ie}");
                        }
                        else if (IsEngineRecord(item!.GetType()))
                        {
                            CollectValues(item, seen, depth + 1);
                        }
                    }

                    break;
                case { } value when IsEngineRecord(value.GetType()):
                    CollectValues(value, seen, depth + 1);
                    break;
                default:
                    break;
            }
        }
    }

    // ---- The undo snapshots of the state of stage 5 ----

    private static readonly EffectSpec s_coinsOnAll = new()
    {
        Target = new TargetSpec { Selector = TargetSelector.All },
        Actions = [new ChangeResourceAction { Resource = "coins", Amount = ContentValue.Of(2) }],
    };

    private const string CoinsOnAllJson =
        """{"target":{"selector":"all","among":null,"excludeSelf":false},"trigger":null,"duration":null,"condition":null,"intercept":null,"actions":[{"type":"changeResource","resource":"coins","amount":2,"target":null}],"outcomes":null}""";

    [Fact]
    public void An_undo_of_a_poll_writes_it_with_its_votes_and_an_undo_of_anything_else_keeps_its_format()
    {
        var poll = new Poll(
            s_run, PollSource.Admin, "Какой жанр?", [new PollOption("Horror", "Horror"), new PollOption("RPG", "RPG")], PollVoters.Everyone,
            Anonymous: true, ShowLiveCounts: false, s_at, s_at.AddHours(24), PollStatus.Closed, [new Vote(s_user, "Horror")])
        {
            OnResult = s_coinsOnAll,
            Result = "Horror",
        };
        var filled = FilledUndo();
        var undo = filled with { Polls = [poll], RemovedPolls = [s_game] };

        var stored = EventCodec.Encode(undo);

        Assert.EndsWith(
            ""","polls":[{"pollId":"00000000-0000-0000-0000-000000000001","source":"admin","question":"Какой жанр?","options":[{"id":"Horror","label":"Horror"},{"id":"RPG","label":"RPG"}],"voters":"everyone","anonymous":true,"showLiveCounts":false,"startedAt":"2026-10-01T12:30:00+00:00","closesAt":"2026-10-02T12:30:00+00:00","status":"closed","votes":[{"userId":"40000000-0000-0000-0000-000000000001","optionId":"Horror"}],"onResult":"""
            + CoinsOnAllJson
            + ""","result":"Horror"}],"removedPolls":["20000000-0000-0000-0000-000000000001"]}""",
            stored.Data,
            StringComparison.Ordinal);
        Assert.Equal(undo, EventCodec.Decode(stored));
        Assert.DoesNotContain("polls", EventCodec.Encode(filled).Data, StringComparison.Ordinal);
    }

    [Fact]
    public void An_undo_of_a_global_event_writes_it_with_its_contributions_and_an_undo_of_anything_else_keeps_its_format()
    {
        var global = new GlobalEventState(
            s_run, "coin-rain", s_globalDefinition, s_at, s_at.AddHours(72), GlobalEventStatus.Ended, [new GlobalContribution(s_game, 7.5m)], Reached: true)
        {
            Goal = new GlobalEventGoal { Metric = GoalMetric.Runs, Target = 3 },
            Kind = EventKind.Good,
            EndReason = GlobalEventEndReason.Admin,
        };
        var filled = FilledUndo();
        var undo = filled with { GlobalEvents = [global], RemovedGlobalEvents = [s_game] };

        var stored = EventCodec.Encode(undo);

        Assert.Contains(
            ""","globalEvents":[{"globalEventId":"00000000-0000-0000-0000-000000000001","objectId":"coin-rain","definition":{"id":"coin-rain","kind":"event",""",
            stored.Data,
            StringComparison.Ordinal);
        Assert.EndsWith(
            ""","startedAt":"2026-10-01T12:30:00+00:00","endsAt":"2026-10-04T12:30:00+00:00","status":"ended","contributions":[{"runId":"20000000-0000-0000-0000-000000000001","amount":7.5}],"reached":true,"goal":{"metric":"runs","target":3},"kind":"good","endReason":"admin"}],"removedGlobalEvents":["20000000-0000-0000-0000-000000000001"]}""",
            stored.Data,
            StringComparison.Ordinal);
        Assert.Equal(undo, EventCodec.Decode(stored));
        Assert.DoesNotContain("globalEvents", EventCodec.Encode(filled).Data, StringComparison.Ordinal);
    }

    [Fact]
    public void An_undo_of_a_settlement_for_everyone_writes_it_and_an_undo_of_anything_else_keeps_its_format()
    {
        var settlement = new ForAllSettlement(s_run, ForAllSource.PollResult, s_game, "poll-result", s_coinsOnAll, null, [s_player, s_other], 1, 3, Halted: true);
        var filled = FilledUndo();
        var undo = filled with { ForAll = [settlement], RemovedForAll = [s_game] };

        var stored = EventCodec.Encode(undo);

        Assert.EndsWith(
            ""","forAll":[{"settlementId":"00000000-0000-0000-0000-000000000001","source":"pollResult","reference":"20000000-0000-0000-0000-000000000001","objectId":"poll-result","effect":"""
            + CoinsOnAllJson
            + ""","parameters":null,"remaining":["10000000-0000-0000-0000-000000000001","10000000-0000-0000-0000-000000000002"],"batches":1,"order":3,"halted":true}],"removedForAll":["20000000-0000-0000-0000-000000000001"]}""",
            stored.Data,
            StringComparison.Ordinal);
        Assert.Equal(undo, EventCodec.Decode(stored));
        Assert.DoesNotContain("forAll", EventCodec.Encode(filled).Data, StringComparison.Ordinal);
    }
}

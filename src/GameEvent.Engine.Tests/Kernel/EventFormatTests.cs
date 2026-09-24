using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Kernel;

/// <summary>
/// The stored log format is frozen: renaming a property or an enum value must fail here, not silently
/// change the log. A new format means a new version plus an upcaster, and a new golden sample.
/// </summary>
public class EventFormatTests
{
    private static readonly Guid s_player = SequentialIds.Make(0x10000000, 1);
    private static readonly Guid s_other = SequentialIds.Make(0x10000000, 2);
    private static readonly Guid s_user = SequentialIds.Make(0x40000000, 1);
    private static readonly Guid s_game = SequentialIds.Make(0x20000000, 1);
    private static readonly Guid s_run = SequentialIds.Make(0, 1);
    private static readonly Guid s_season = SequentialIds.Make(0x30000000, 1);
    private static readonly DateTimeOffset s_at = new(2026, 10, 1, 12, 30, 0, TimeSpan.Zero);

    private const string SnapshotJson =
        """{"rulesetVersion":1,"hours":7.5,"diceCount":{"hoursPerDie":3,"rounding":"nearest","min":1,"max":10},"dieByDifficulty":{"easy":{"sides":2,"grantEvent":null},"normal":{"sides":4,"grantEvent":null},"hard":{"sides":6,"grantEvent":null},"extreme":{"sides":6,"grantEvent":"good"}}}""";

    // The ruleset's own format is frozen by Rulesets/RulesetSchemaTests (docs/ruleset.schema.json); here it is embedded.
    private static string RulesetJsonText => System.Text.Json.JsonSerializer.Serialize(TestRuleset.Create(), EngineJson.Options);

    private static RunSnapshot Snapshot()
    {
        var ruleset = TestRuleset.Create();
        return new RunSnapshot(1, 7.5m, ruleset.Reward.DiceCount, ruleset.Reward.DieByDifficulty);
    }

    public static TheoryData<string, IGameEvent, int, string> Samples() => new()
    {
        {
            "season-created",
            new SeasonCreated(s_season, "Тестовый сезон", TestRuleset.Create(), LinearMap.Generate(1), s_at),
            1,
            """{"seasonId":"30000000-0000-0000-0000-000000000001","name":"Тестовый сезон","ruleset":""" + RulesetJsonText + ""","map":{"cells":[{"id":"start","type":"start"},{"id":"finish","type":"finish"}],"edges":[{"from":"start","to":"finish","isDefaultForward":true,"isPrimaryBackward":true}]},"deadline":"2026-10-01T12:30:00+00:00"}"""
        },
        {
            "season-status-changed",
            new SeasonStatusChanged(SeasonStatus.Draft, SeasonStatus.Active),
            1,
            """{"from":"draft","to":"active"}"""
        },
        {
            "season-deadline-set",
            new SeasonDeadlineSet(s_at),
            1,
            """{"deadline":"2026-10-01T12:30:00+00:00"}"""
        },
        {
            "player-inactivity-set",
            new PlayerInactivitySet(s_player, true),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","isInactive":true}"""
        },
        {
            "player-adjusted",
            new PlayerAdjusted(s_player, "Потерял скрин"),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","comment":"Потерял скрин"}"""
        },
        {
            "offer-discarded",
            new OfferDiscarded(s_player, s_game),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","gameId":"20000000-0000-0000-0000-000000000001"}"""
        },
        {
            "coins-changed",
            new CoinsChanged(s_player, -3, CoinsReason.AdminAdjustment, null),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","delta":-3,"reason":"adminAdjustment","runId":null}"""
        },
        {
            "resource-changed",
            new ResourceChanged(s_player, "tickets", 2, ResourceReason.AdminAdjustment),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","resource":"tickets","delta":2,"reason":"adminAdjustment"}"""
        },
        {
            "ruleset-changed",
            new RulesetChanged(2, TestRuleset.Create()),
            1,
            """{"version":2,"ruleset":""" + RulesetJsonText + "}"
        },
        {
            "season-player-added",
            new SeasonPlayerAdded(s_player, s_user, "Вася", "start"),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","userId":"40000000-0000-0000-0000-000000000001","name":"Вася","cellId":"start"}"""
        },
        {
            "game-rolled",
            new GameRolled(s_player, "Horror", [new RollMiss(s_game, RollMissReason.CompletedInSeason, s_other)], s_game, Snapshot(), s_at),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","category":"Horror","misses":[{"gameId":"20000000-0000-0000-0000-000000000001","reason":"completedInSeason","byPlayerId":"10000000-0000-0000-0000-000000000002"}],"gameId":"20000000-0000-0000-0000-000000000001","snapshot":"""
            + SnapshotJson + ""","rolledAt":"2026-10-01T12:30:00+00:00"}"""
        },
        {
            "game-choice-rolled",
            new GameChoiceRolled(s_player, "Horror", [], s_run, [new RollOffer(s_game, Snapshot(), s_at)]),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","category":"Horror","misses":[],"choiceId":"00000000-0000-0000-0000-000000000001","offers":[{"gameId":"20000000-0000-0000-0000-000000000001","snapshot":"""
            + SnapshotJson + ""","rolledAt":"2026-10-01T12:30:00+00:00"}]}"""
        },
        {
            "choice-made",
            new ChoiceMade(s_player, s_run, "20000000000000000000000000000001"),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","choiceId":"00000000-0000-0000-0000-000000000001","optionId":"20000000000000000000000000000001"}"""
        },
        {
            "choice-discarded",
            new ChoiceDiscarded(s_player, s_run),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","choiceId":"00000000-0000-0000-0000-000000000001"}"""
        },
        {
            "run-started",
            new RunStarted(s_run, s_player, s_game, Snapshot(), s_at, s_at.AddMinutes(5)),
            1,
            """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","gameId":"20000000-0000-0000-0000-000000000001","snapshot":"""
            + SnapshotJson + ""","rolledAt":"2026-10-01T12:30:00+00:00","startedAt":"2026-10-01T12:35:00+00:00"}"""
        },
        {
            "run-completed",
            new RunCompleted(s_run, s_player, Difficulty.Extreme, 7.5m, s_at),
            1,
            """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","difficulty":"extreme","hours":7.5,"completedAt":"2026-10-01T12:30:00+00:00"}"""
        },
        {
            "completion-rolled",
            new CompletionRolled(s_run, s_player, [new Die(6, 5), new Die(6, 1)]),
            1,
            """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","dice":[{"sides":6,"value":5},{"sides":6,"value":1}]}"""
        },
        {
            "points-changed",
            new PointsChanged(s_player, 6, PointsReason.CompletionRoll, s_run),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","delta":6,"reason":"completionRoll","runId":"00000000-0000-0000-0000-000000000001"}"""
        },
        {
            "player-moved",
            new PlayerMoved(s_player, "start", "c2", 2, ["c1", "c2"], MoveReason.CompletionRoll, s_run),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","from":"start","to":"c2","steps":2,"path":["c1","c2"],"reason":"completionRoll","runId":"00000000-0000-0000-0000-000000000001"}"""
        },
    };

    /// <summary>Enum values that are not in <see cref="Samples"/> (one sample per type) are frozen here.</summary>
    public static TheoryData<string, IGameEvent, string> ValueSamples() => new()
    {
        { "season-created", new SeasonCreated(s_season, "Тестовый сезон", TestRuleset.Create(), LinearMap.Generate(1), null), "\"deadline\":null" },
        { "season-status-changed", new SeasonStatusChanged(SeasonStatus.Active, SeasonStatus.Closing), """{"from":"active","to":"closing"}""" },
        { "season-status-changed", new SeasonStatusChanged(SeasonStatus.Finished, SeasonStatus.Archived), """{"from":"finished","to":"archived"}""" },
        { "season-deadline-set", new SeasonDeadlineSet(null), """{"deadline":null}""" },
        { "points-changed", new PointsChanged(s_player, 12, PointsReason.StartingBalance, null), """{"playerId":"10000000-0000-0000-0000-000000000001","delta":12,"reason":"startingBalance","runId":null}""" },
        { "points-changed", new PointsChanged(s_player, -4, PointsReason.AdminAdjustment, null), """{"playerId":"10000000-0000-0000-0000-000000000001","delta":-4,"reason":"adminAdjustment","runId":null}""" },
        { "coins-changed", new CoinsChanged(s_player, 7, CoinsReason.StartingBalance, null), """{"playerId":"10000000-0000-0000-0000-000000000001","delta":7,"reason":"startingBalance","runId":null}""" },
        { "player-moved", new PlayerMoved(s_player, "start", "c5", 0, ["c5"], MoveReason.StartingCell, null), """{"playerId":"10000000-0000-0000-0000-000000000001","from":"start","to":"c5","steps":0,"path":["c5"],"reason":"startingCell","runId":null}""" },
        { "player-moved", new PlayerMoved(s_player, "c5", "c2", 0, ["c2"], MoveReason.AdminAdjustment, null), """{"playerId":"10000000-0000-0000-0000-000000000001","from":"c5","to":"c2","steps":0,"path":["c2"],"reason":"adminAdjustment","runId":null}""" },
    };

    [Theory]
    [MemberData(nameof(ValueSamples))]
    public void Enum_values_and_nulls_are_stored_in_the_frozen_format(string type, IGameEvent sample, string jsonOrFragment)
    {
        var stored = EventCodec.Encode(sample);

        Assert.Equal(type, stored.Type);
        Assert.Contains(jsonOrFragment, stored.Data, StringComparison.Ordinal);
        Assert.Equal(sample, EventCodec.Decode(stored));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Event_is_stored_in_the_frozen_format(string type, IGameEvent sample, int version, string json)
    {
        Assert.Equal(new StoredEvent(type, version, json), EventCodec.Encode(sample));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Frozen_format_reads_back_into_the_same_event(string type, IGameEvent sample, int version, string json)
    {
        Assert.Equal(sample, EventCodec.Decode(new StoredEvent(type, version, json)));
    }

    [Fact]
    public void Every_event_type_has_a_golden_sample()
    {
        var sampled = Samples().Select(row => row.Data.Item1).Order(StringComparer.Ordinal);
        var catalog = EventCatalog.Types.Select(t => EventCatalog.Describe(t).Name).Order(StringComparer.Ordinal);

        Assert.Equal(catalog, sampled);
    }

    [Fact]
    public void Unknown_event_type_is_refused()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => EventCodec.Decode(new StoredEvent("no-such-event", 1, "{}")));
    }

    [Fact]
    public void Event_from_a_newer_build_is_refused()
    {
        Assert.Throws<System.Text.Json.JsonException>(
            () => EventCodec.Decode(new StoredEvent("points-changed", 99, "{}")));
    }

    [Fact]
    public void Older_version_is_upcast_step_by_step_before_reading()
    {
        // Pretend points-changed v1 had "amount" and v2 renamed nothing yet: two steps lead to today's shape.
        var steps = new List<string>();
        var upcasters = new Dictionary<(string Type, int FromVersion), Func<System.Text.Json.Nodes.JsonObject, System.Text.Json.Nodes.JsonObject>>
        {
            [("points-changed", -1)] = data =>
            {
                steps.Add("v-1");
                data["delta"] = data["amount"]!.DeepClone();
                data.Remove("amount");
                return data;
            },
            [("points-changed", 0)] = data =>
            {
                steps.Add("v0");
                data["reason"] = "completionRoll";
                return data;
            },
        };
        var old = new StoredEvent("points-changed", -1, """{"playerId":"10000000-0000-0000-0000-000000000001","amount":6,"runId":null}""");

        var decoded = EventCodec.Decode(old, upcasters);

        Assert.Equal(["v-1", "v0"], steps);
        Assert.Equal(new PointsChanged(s_player, 6, PointsReason.CompletionRoll, null), decoded);
    }

    [Fact]
    public void Older_version_without_an_upcaster_is_refused()
    {
        Assert.Throws<System.Text.Json.JsonException>(
            () => EventCodec.Decode(new StoredEvent("points-changed", 0, "{}")));
    }
}

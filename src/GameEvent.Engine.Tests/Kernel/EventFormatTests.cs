using GameEvent.Engine.Effects;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Ranking;
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
        """{"rulesetVersion":1,"hours":7.5,"diceCount":{"hoursPerDie":3,"rounding":"nearest","min":1,"max":10},"dieByDifficulty":{"easy":{"sides":2,"grantEvent":null},"normal":{"sides":4,"grantEvent":null},"hard":{"sides":6,"grantEvent":null},"extreme":{"sides":6,"grantEvent":"good"}},"techRerollWindowHours":48,"challengeExtraDice":1,"coins":{"perHour":1,"min":3}}""";

    // The ruleset's own format is frozen by Rulesets/RulesetSchemaTests (docs/ruleset.schema.json); here it is embedded.
    private static string RulesetJsonText => System.Text.Json.JsonSerializer.Serialize(TestRuleset.Create(), EngineJson.Options);

    private static RunSnapshot Snapshot()
    {
        var ruleset = TestRuleset.Create();
        return new RunSnapshot(1, 7.5m, ruleset.Reward.DiceCount, ruleset.Reward.DieByDifficulty, 48, 1, new CoinReward { PerHour = 1, Min = 3 });
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
            "game-excluded",
            new GameExcluded(s_player, s_game, ExclusionReason.AlreadyPlayed),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","gameId":"20000000-0000-0000-0000-000000000001","reason":"alreadyPlayed"}"""
        },
        {
            "game-rerolled",
            new GameRerolled(s_player, [s_game], RerollPayment.Coins),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","gameIds":["20000000-0000-0000-0000-000000000001"],"payment":"coins"}"""
        },
        {
            "manual-effect-created",
            new ManualEffectCreated(s_run, s_player, EventKind.Bad, ManualEffectSource.PaidReroll, null),
            1,
            """{"effectId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","drawEvent":"bad","source":"paidReroll","runId":null}"""
        },
        {
            "run-dropped",
            new RunDropped(s_run, s_player, [new Die(4, 3), new Die(4, 1)], s_at),
            1,
            """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","penaltyDice":[{"sides":4,"value":3},{"sides":4,"value":1}],"droppedAt":"2026-10-01T12:30:00+00:00"}"""
        },
        {
            "run-tech-rerolled",
            new RunTechRerolled(s_run, s_player, TechRerollReason.Other, "Вылетает на старте", false, s_at),
            1,
            """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","reason":"other","comment":"Вылетает на старте","byAdmin":false,"rerolledAt":"2026-10-01T12:30:00+00:00"}"""
        },
        {
            "tech-reroll-converted-to-drop",
            new TechRerollConvertedToDrop(s_run, s_player, "Игра запускалась", [new Die(4, 2)], s_at),
            1,
            """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","comment":"Игра запускалась","penaltyDice":[{"sides":4,"value":2}],"convertedAt":"2026-10-01T12:30:00+00:00"}"""
        },
        {
            "run-reviewed",
            new RunReviewed(s_run, s_player, 9, "Страшно и красиво", s_at),
            1,
            """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","rating":9,"text":"Страшно и красиво","reviewedAt":"2026-10-01T12:30:00+00:00"}"""
        },
        {
            "run-hours-corrected",
            new RunHoursCorrected(s_run, s_player, 6m, 12m, [new Die(4, 2), new Die(4, 3)], [], "Часы по HLTB", s_at),
            1,
            """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","oldHours":6,"newHours":12,"added":[{"sides":4,"value":2},{"sides":4,"value":3}],"removed":[],"comment":"Часы по HLTB","correctedAt":"2026-10-01T12:30:00+00:00"}"""
        },
        {
            "run-difficulty-changed",
            new RunDifficultyChanged(
                s_run, s_player, Difficulty.Hard, Difficulty.Normal,
                [new DieChange(new Die(6, 5), new Die(4, 4))], [new DieChange(new Die(6, 1), new Die(4, 1))], "По пруфу — нормальная", s_at),
            1,
            """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","oldDifficulty":"hard","newDifficulty":"normal","dice":[{"before":{"sides":6,"value":5},"after":{"sides":4,"value":4}}],"challengeDice":[{"before":{"sides":6,"value":1},"after":{"sides":4,"value":1}}],"comment":"По пруфу — нормальная","changedAt":"2026-10-01T12:30:00+00:00"}"""
        },
        {
            "manual-effect-resolved",
            new ManualEffectResolved(s_run, s_player, s_run, ManualEffectOutcome.NotApplicable, "Сложность понижена по пруфу"),
            1,
            """{"effectId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","runId":"00000000-0000-0000-0000-000000000001","outcome":"notApplicable","comment":"Сложность понижена по пруфу"}"""
        },
        {
            "proof-submitted",
            new ProofSubmitted(s_run, s_player, ["https://imgur.com/a/credits"], "Титры", s_other, s_at),
            1,
            """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","links":["https://imgur.com/a/credits"],"note":"Титры","witnessId":"10000000-0000-0000-0000-000000000002","submittedAt":"2026-10-01T12:30:00+00:00"}"""
        },
        {
            "proof-approved",
            new ProofApproved(s_run, s_player, true, "Видел на стриме", s_at),
            1,
            """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","withoutProof":true,"comment":"Видел на стриме","approvedAt":"2026-10-01T12:30:00+00:00"}"""
        },
        {
            "proof-rejected",
            new ProofRejected(s_run, s_player, "На скрине другая игра", s_at),
            1,
            """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","comment":"На скрине другая игра","rejectedAt":"2026-10-01T12:30:00+00:00"}"""
        },
        {
            "player-finished",
            new PlayerFinished(s_player, s_run, 2, s_at, 3),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","runId":"00000000-0000-0000-0000-000000000001","order":2,"finishedAt":"2026-10-01T12:30:00+00:00","surplus":3}"""
        },
        {
            "season-result-recorded",
            new SeasonResultRecorded(
                [
                    new LeaderboardRow(s_player, 1, 4, 0, true, false),
                    new LeaderboardRow(s_other, 2, 12, null, false, false),
                ]),
            1,
            """{"rows":[{"playerId":"10000000-0000-0000-0000-000000000001","place":1,"points":4,"cellsToFinish":0,"isFirst":true,"provisional":false},{"playerId":"10000000-0000-0000-0000-000000000002","place":2,"points":12,"cellsToFinish":null,"isFirst":false,"provisional":false}]}"""
        },
        {
            "finish-surplus-changed",
            new FinishSurplusChanged(s_player, -2),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","delta":-2}"""
        },
        {
            "player-frozen",
            new PlayerFrozen(s_player),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001"}"""
        },
        {
            "player-finish-revoked",
            new PlayerFinishRevoked(s_player, s_run),
            1,
            """{"playerId":"10000000-0000-0000-0000-000000000001","runId":"00000000-0000-0000-0000-000000000001"}"""
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
            new RunCompleted(s_run, s_player, Difficulty.Extreme, 7.5m, s_at, "https://howlongtobeat.com/game/1", true, AfterFinish: true, FreeMode: true),
            1,
            """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","difficulty":"extreme","hours":7.5,"completedAt":"2026-10-01T12:30:00+00:00","hoursSource":"https://howlongtobeat.com/game/1","challengeDone":true,"afterFinish":true,"freeMode":true}"""
        },
        {
            "completion-rolled",
            new CompletionRolled(s_run, s_player, [new Die(6, 5), new Die(6, 1)], [new Die(6, 4)]),
            1,
            """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","dice":[{"sides":6,"value":5},{"sides":6,"value":1}],"challengeDice":[{"sides":6,"value":4}]}"""
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

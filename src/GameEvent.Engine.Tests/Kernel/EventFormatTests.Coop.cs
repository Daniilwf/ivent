using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Tests.Kernel;

/// <summary>The frozen formats of coop and its invitations (D-520…D-523, task 5.10).</summary>
public partial class EventFormatTests
{
    private static TheoryData<string, IGameEvent, int, string> WithCoop(TheoryData<string, IGameEvent, int, string> data)
    {
        foreach (var (type, sample, json) in CoopSamples())
        {
            data.Add(type, sample, 1, json);
        }

        return data;
    }

    private static IEnumerable<(string Type, IGameEvent Sample, string Json)> CoopSamples() =>
    [
        (
            "interaction-created",
            new InteractionCreated(new Interaction(s_game, InteractionKind.Coop, s_player, s_other, s_run, s_at, s_at.AddHours(24))),
            """{"interaction":{"interactionId":"20000000-0000-0000-0000-000000000001","kind":"coop","fromPlayerId":"10000000-0000-0000-0000-000000000001","toPlayerId":"10000000-0000-0000-0000-000000000002","runId":"00000000-0000-0000-0000-000000000001","createdAt":"2026-10-01T12:30:00+00:00","expiresAt":"2026-10-02T12:30:00+00:00","status":"pending"}}"""
        ),
        ("interaction-answered", new InteractionAnswered(s_game, s_other, true, s_at), """{"interactionId":"20000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000002","accepted":true,"answeredAt":"2026-10-01T12:30:00+00:00"}"""),
        ("interaction-withdrawn", new InteractionWithdrawn(s_game, s_player, s_at), """{"interactionId":"20000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","withdrawnAt":"2026-10-01T12:30:00+00:00"}"""),
        ("interaction-expired", new InteractionExpired(s_game, InteractionExpiry.RunEnded, s_at), """{"interactionId":"20000000-0000-0000-0000-000000000001","reason":"runEnded","expiredAt":"2026-10-01T12:30:00+00:00"}"""),
        ("coop-joined", new CoopJoined(s_run, s_other, s_player, s_game, s_at), """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000002","rollerId":"10000000-0000-0000-0000-000000000001","interactionId":"20000000-0000-0000-0000-000000000001","joinedAt":"2026-10-01T12:30:00+00:00"}"""),
        ("coop-left", new CoopLeft(s_run, s_other, CoopLeaveReason.RollerDropped, s_at), """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000002","reason":"rollerDropped","leftAt":"2026-10-01T12:30:00+00:00"}"""),
        (
            "coop-reward-split",
            new CoopRewardSplit(s_run, s_player, s_other, 7, 4, 3, 7, 4, 3, 0.5m, 0.5m),
            """{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","partnerId":"10000000-0000-0000-0000-000000000002","total":7,"rollerPoints":4,"partnerPoints":3,"coins":7,"rollerCoins":4,"partnerCoins":3,"pointsShare":0.5,"coinsShare":0.5}"""
        ),
    ];
}

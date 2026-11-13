using GameEvent.Engine.Duels;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Kernel;

/// <summary>The frozen formats of duels (stage 5, task 5.12, D-525, D-540).</summary>
public partial class EventFormatTests
{
    private static readonly Guid s_duel = SequentialIds.Make(0x7D000000, 1);

    private const string WonDuelJson =
        """{"duelId":"7d000000-0000-0000-0000-000000000001","challengerId":"10000000-0000-0000-0000-000000000001","opponentId":"10000000-0000-0000-0000-000000000002","createdAt":"2026-10-01T12:30:00+00:00","expiresAt":"2026-10-09T12:30:00+00:00","status":"won","acceptedAt":"2026-10-02T12:30:00+00:00","runsAtAcceptance":["20000000-0000-0000-0000-000000000001"],"winnerId":"10000000-0000-0000-0000-000000000001","runId":"00000000-0000-0000-0000-000000000001","coins":10,"end":"completed","endedAt":"2026-10-03T12:30:00+00:00"}""";

    private static Duel WonDuel() =>
        new(s_duel, s_player, s_other, s_at, s_at.AddDays(8), DuelStatus.Won, s_at.AddDays(1), [s_game], s_player, s_run, 10, DuelEnd.Completed, s_at.AddDays(2));

    private static TheoryData<string, IGameEvent, int, string> WithDuels(TheoryData<string, IGameEvent, int, string> data)
    {
        foreach (var (type, sample, json) in DuelSamples())
        {
            data.Add(type, sample, 1, json);
        }

        return data;
    }

    private static IEnumerable<(string Type, IGameEvent Sample, string Json)> DuelSamples() =>
    [
        (
            "duel-challenged",
            new DuelChallenged(new Duel(s_duel, s_player, s_other, s_at, s_at.AddHours(24))),
            """{"duel":{"duelId":"7d000000-0000-0000-0000-000000000001","challengerId":"10000000-0000-0000-0000-000000000001","opponentId":"10000000-0000-0000-0000-000000000002","createdAt":"2026-10-01T12:30:00+00:00","expiresAt":"2026-10-02T12:30:00+00:00","status":"pending","acceptedAt":null,"runsAtAcceptance":[],"winnerId":null,"runId":null,"coins":0,"end":null,"endedAt":null}}"""
        ),
        (
            "duel-answered",
            new DuelAnswered(s_duel, true, s_at, s_at.AddDays(7), [s_run]),
            """{"duelId":"7d000000-0000-0000-0000-000000000001","accepted":true,"answeredAt":"2026-10-01T12:30:00+00:00","expiresAt":"2026-10-08T12:30:00+00:00","runsAtAcceptance":["00000000-0000-0000-0000-000000000001"]}"""
        ),
        ("duel-withdrawn", new DuelWithdrawn(s_duel, s_at), """{"duelId":"7d000000-0000-0000-0000-000000000001","withdrawnAt":"2026-10-01T12:30:00+00:00"}"""),
        ("duel-expired", new DuelExpired(s_duel, DuelExpiry.Timeout, s_at), """{"duelId":"7d000000-0000-0000-0000-000000000001","reason":"timeout","expiredAt":"2026-10-01T12:30:00+00:00"}"""),
        (
            "duel-settled",
            new DuelSettled(s_duel, DuelStatus.Won, DuelEnd.Completed, s_player, s_run, 10, s_at),
            """{"duelId":"7d000000-0000-0000-0000-000000000001","status":"won","end":"completed","winnerId":"10000000-0000-0000-0000-000000000001","runId":"00000000-0000-0000-0000-000000000001","coins":10,"settledAt":"2026-10-01T12:30:00+00:00"}"""
        ),
        (
            "duel-win-revoked",
            new DuelWinRevoked(s_duel, s_player, s_run, 10),
            """{"duelId":"7d000000-0000-0000-0000-000000000001","winnerId":"10000000-0000-0000-0000-000000000001","runId":"00000000-0000-0000-0000-000000000001","coins":10}"""
        ),
    ];

    /// <summary>The values of the duel enums and the nulls of a decline and a forfeit, frozen like <see cref="ValueSamples"/>.</summary>
    public static TheoryData<string, IGameEvent, string> DuelValueSamples() => new()
    {
        { "duel-answered", new DuelAnswered(s_duel, false, s_at, null, []), """{"duelId":"7d000000-0000-0000-0000-000000000001","accepted":false,"answeredAt":"2026-10-01T12:30:00+00:00","expiresAt":null,"runsAtAcceptance":[]}""" },
        { "duel-expired", new DuelExpired(s_duel, DuelExpiry.Deadline, s_at), "\"reason\":\"deadline\"" },
        { "duel-expired", new DuelExpired(s_duel, DuelExpiry.Inactive, s_at), "\"reason\":\"inactive\"" },
        { "duel-expired", new DuelExpired(s_duel, DuelExpiry.FirstFinisher, s_at), "\"reason\":\"firstFinisher\"" },
        {
            "duel-settled",
            new DuelSettled(s_duel, DuelStatus.Won, DuelEnd.Forfeit, s_other, null, 10, s_at),
            """{"duelId":"7d000000-0000-0000-0000-000000000001","status":"won","end":"forfeit","winnerId":"10000000-0000-0000-0000-000000000002","runId":null,"coins":10,"settledAt":"2026-10-01T12:30:00+00:00"}"""
        },
        { "duel-settled", new DuelSettled(s_duel, DuelStatus.Drawn, DuelEnd.TimeOut, null, null, 0, s_at), "\"status\":\"drawn\",\"end\":\"timeOut\",\"winnerId\":null,\"runId\":null,\"coins\":0" },
        { "duel-settled", new DuelSettled(s_duel, DuelStatus.Drawn, DuelEnd.Deadline, null, null, 0, s_at), "\"status\":\"drawn\",\"end\":\"deadline\"," },
        { "duel-settled", new DuelSettled(s_duel, DuelStatus.Drawn, DuelEnd.WinRejected, null, null, 0, s_at), "\"end\":\"winRejected\"," },
        { "duel-settled", new DuelSettled(s_duel, DuelStatus.Cancelled, DuelEnd.TechReroll, null, null, 0, s_at), "\"status\":\"cancelled\",\"end\":\"techReroll\"," },
        { "duel-settled", new DuelSettled(s_duel, DuelStatus.Cancelled, DuelEnd.Inactive, null, null, 0, s_at), "\"end\":\"inactive\"," },
        { "duel-settled", new DuelSettled(s_duel, DuelStatus.Cancelled, DuelEnd.FirstFinisher, null, null, 0, s_at), "\"end\":\"firstFinisher\"," },
        { "duel-challenged", new DuelChallenged(WonDuel()), "{\"duel\":" + WonDuelJson + "}" },
        { "duel-challenged", new DuelChallenged(WonDuel() with { Status = DuelStatus.Declined }), "\"status\":\"declined\"" },
        { "duel-challenged", new DuelChallenged(WonDuel() with { Status = DuelStatus.Withdrawn }), "\"status\":\"withdrawn\"" },
        { "duel-challenged", new DuelChallenged(WonDuel() with { Status = DuelStatus.Expired }), "\"status\":\"expired\"" },
        { "duel-challenged", new DuelChallenged(WonDuel() with { Status = DuelStatus.Active }), "\"status\":\"active\"" },
        { "coins-changed", new CoinsChanged(s_player, 10, CoinsReason.Duel, s_run), """{"playerId":"10000000-0000-0000-0000-000000000001","delta":10,"reason":"duel","runId":"00000000-0000-0000-0000-000000000001"}""" },
        { "coins-changed", new CoinsChanged(s_player, -10, CoinsReason.DuelRevoked, s_run), """{"playerId":"10000000-0000-0000-0000-000000000001","delta":-10,"reason":"duelRevoked","runId":"00000000-0000-0000-0000-000000000001"}""" },
    };

    [Theory]
    [MemberData(nameof(DuelValueSamples))]
    public void Duel_values_and_nulls_are_stored_in_the_frozen_format(string type, IGameEvent sample, string jsonOrFragment)
    {
        var stored = EventCodec.Encode(sample);

        Assert.Equal(type, stored.Type);
        Assert.Contains(jsonOrFragment, stored.Data, StringComparison.Ordinal);
        Assert.Equal(sample, EventCodec.Decode(stored));
    }

    [Fact]
    public void An_undo_of_a_duel_writes_it_and_an_undo_of_anything_else_keeps_its_format()
    {
        var undo = new Engine.Undo.CommandUndone(s_run, "Ошибка", [], [], [], [], [], [], null, Duels: [WonDuel()], RemovedDuels: [s_game]);

        var stored = EventCodec.Encode(undo);

        Assert.EndsWith(
            ""","season":null,"duels":[""" + WonDuelJson + """],"removedDuels":["20000000-0000-0000-0000-000000000001"]}""",
            stored.Data,
            StringComparison.Ordinal);
        Assert.Equal(undo, EventCodec.Decode(stored));
        Assert.DoesNotContain("duels", EventCodec.Encode(FilledUndo()).Data, StringComparison.Ordinal);
    }
}

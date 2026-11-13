using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Tests.Kernel;

/// <summary>The frozen formats of the partner board (D-524, task 5.11).</summary>
public partial class EventFormatTests
{
    private static TheoryData<string, IGameEvent, int, string> WithPartnerBoard(TheoryData<string, IGameEvent, int, string> data)
    {
        foreach (var (type, sample, json) in PartnerBoardSamples())
        {
            data.Add(type, sample, 1, json);
        }

        return data;
    }

    private static IEnumerable<(string Type, IGameEvent Sample, string Json)> PartnerBoardSamples() =>
    [
        (
            "partner-request-posted",
            new PartnerRequestPosted(new PartnerRequest(s_game, s_player, s_run, s_at, s_at.AddHours(48))),
            """{"request":{"requestId":"20000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","runId":"00000000-0000-0000-0000-000000000001","createdAt":"2026-10-01T12:30:00+00:00","expiresAt":"2026-10-03T12:30:00+00:00","status":"open"}}"""
        ),
        (
            "partner-request-closed",
            new PartnerRequestClosed(s_game, PartnerRequestCloseReason.RunEnded, s_at),
            """{"requestId":"20000000-0000-0000-0000-000000000001","reason":"runEnded","closedAt":"2026-10-01T12:30:00+00:00"}"""
        ),
    ];

    [Fact]
    public void An_undo_of_a_partner_request_writes_it_and_an_undo_of_anything_else_keeps_its_format()
    {
        var filled = FilledUndo();
        var request = new PartnerRequest(s_game, s_player, s_run, s_at, s_at.AddHours(48), PartnerRequestStatus.Closed);
        var undo = filled with { PartnerRequests = [request], RemovedPartnerRequests = [s_run] };

        var stored = EventCodec.Encode(undo);

        Assert.EndsWith(
            ""","partnerRequests":[{"requestId":"20000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","runId":"00000000-0000-0000-0000-000000000001","createdAt":"2026-10-01T12:30:00+00:00","expiresAt":"2026-10-03T12:30:00+00:00","status":"closed"}],"removedPartnerRequests":["00000000-0000-0000-0000-000000000001"]}""",
            stored.Data,
            StringComparison.Ordinal);
        Assert.Equal(undo, EventCodec.Decode(stored));
        Assert.DoesNotContain("partnerRequests", EventCodec.Encode(filled).Data, StringComparison.Ordinal);
    }
}

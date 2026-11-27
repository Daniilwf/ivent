using System.Collections.Frozen;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

internal sealed class FeedReactionTargets : IFeedReactionTargets
{
    // A feed command needs a headline. Technical facts can add details to a row but do not form a row on their own.
    private static readonly FrozenSet<string> s_headlines = new[]
    {
        "command-undone", "for-all-settled", "poll-started", "vote-cast", "vote-changed", "poll-closed", "poll-cancelled",
        "global-event-launched", "global-event-ended", "global-progress-changed", "global-goal-reached",
        "season-created", "season-result-recorded", "season-status-changed", "season-deadline-set", "ruleset-changed",
        "finish-bonus-rules-refreshed", "season-player-added", "run-completed", "proof-reject-penalized",
        "proof-rejected", "proof-approved", "tech-reroll-converted-to-drop", "run-dropped", "run-tech-rerolled",
        "run-hours-corrected", "run-difficulty-changed", "run-condition-checked", "run-reviewed", "proof-submitted", "run-started",
        "game-excluded", "game-rolled", "game-choice-rolled", "game-rerolled", "choice-made", "item-used", "inventory-adjusted",
        "content-published", "lot-bought", "shop-rolled", "bet-placed", "map-published", "offer-discarded", "choice-discarded",
        "player-finish-revoked", "player-finished", "player-adjusted", "player-inactivity-set", "irl-proof-reviewed", "irl-proof-submitted",
        "effect-resumed", "manual-effect-resolved", "event-drawn", "event-draw-missed", "effect-suspended", "manual-effect-created",
        "points-changed", "coins-changed", "player-moved",
    }.Where(type => FeedVisibility.Types.GetValueOrDefault(type, FeedVisibility.Access.NotInFeed) != FeedVisibility.Access.NotInFeed)
        .ToFrozenSet(StringComparer.Ordinal);

    public async Task<IReadOnlySet<Guid>> FindAsync(GameEventDbContext db, Guid seasonId, IReadOnlyCollection<Guid> commandIds, CancellationToken ct)
    {
        if (commandIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var rows = await db.Events.AsNoTracking()
            .Where(e => e.SeasonId == seasonId && commandIds.Contains(e.CommandId) && e.UndoneByEventId == null)
            .Select(e => new { e.CommandId, e.Type, e.Version, VoteData = e.Type == "vote-cast" || e.Type == "vote-changed" ? e.Data : null })
            .ToListAsync(ct);
        var anonymousCommands = rows.Where(e => e.VoteData is not null
                && EventCodec.Decode(new StoredEvent(e.Type, e.Version, e.VoteData)) is (VoteCast { Anonymous: true } or VoteChanged { Anonymous: true }))
            .Select(e => e.CommandId)
            .ToHashSet();
        return rows.Where(e => s_headlines.Contains(e.Type) && !anonymousCommands.Contains(e.CommandId))
            .Select(e => e.CommandId)
            .ToHashSet();
    }
}

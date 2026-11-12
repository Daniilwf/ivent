using GameEvent.Engine.Decks;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

/// <summary>
/// The event I drew last (D-500, stage 5): <c>sequence</c> — its place in the log, so the page shows a new one as new;
/// <c>deck</c> and <c>kind</c> — where it came from; <c>name</c> and <c>description</c> — its card from the content in
/// force when it was drawn; <c>source</c> — a cell or an item.
/// </summary>
public sealed record DrawnEventView(long Sequence, string Deck, EventKind Kind, string ObjectId, string Name, string Description, EventSource Source);

/// <summary>Reads the events drawn in a season from its log, by the events' types, never by their data (invariant 9).</summary>
public static class EventViews
{
    private static readonly string s_drawn = EventCatalog.Describe(typeof(EventDrawn)).Name;

    /// <summary>How many draws of the season are read at a time while looking for my latest one.</summary>
    private const int DrawsPerPage = 64;

    /// <summary>My latest draw that was not undone; null when there is none.</summary>
    public static async Task<DrawnEventView?> LastDrawnAsync(GameEventDbContext db, ContentCards contentCards, Guid seasonId, Guid playerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(contentCards);
        long? before = null;
        while (true)
        {
            var rows = await db.Events.AsNoTracking()
                .Where(e => e.SeasonId == seasonId && e.Type == s_drawn && e.UndoneByEventId == null && (before == null || e.Sequence < before))
                .OrderByDescending(e => e.Sequence)
                .Take(DrawsPerPage)
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                if (EventCodec.Decode(new StoredEvent(row.Type, row.Version, row.Data)) is EventDrawn drawn && drawn.PlayerId == playerId)
                {
                    var card = (await contentCards.OfAsync(db, seasonId, ct)).GetValueOrDefault(drawn.ObjectId);
                    return new DrawnEventView(
                        row.Sequence, drawn.Deck, drawn.Kind, drawn.ObjectId, card?.Name ?? drawn.ObjectId, card?.Description ?? "", drawn.Source);
                }
            }

            if (rows.Count < DrawsPerPage)
            {
                return null;
            }

            before = rows[^1].Sequence;
        }
    }
}

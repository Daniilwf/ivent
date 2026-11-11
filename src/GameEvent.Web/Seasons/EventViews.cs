using System.Collections.Concurrent;
using GameEvent.Engine.Content;
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
    private static readonly string s_published = EventCatalog.Describe(typeof(ContentPublished)).Name;

    /// <summary>How many draws of the season are read at a time while looking for my latest one.</summary>
    private const int DrawsPerPage = 64;

    // The cards of every object a season ever published, by the command of its latest publication (unique to that log):
    // content is published rarely, so a view decodes the packs once, not on every request (the memory budget, RUNBOOK)
    private static readonly ConcurrentDictionary<Guid, (Guid Command, IReadOnlyDictionary<string, ObjectCard> Cards)> s_cards = new();

    /// <summary>My latest draw that was not undone; null when there is none.</summary>
    public static async Task<DrawnEventView?> LastDrawnAsync(GameEventDbContext db, Guid seasonId, Guid playerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
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
                    var card = (await CardsAsync(db, seasonId, ct)).GetValueOrDefault(drawn.ObjectId);
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

    /// <summary>
    /// The name and text of every object the season ever published, the latest publication winning: an event dropped
    /// from a later pack keeps its card (D-400), the same in the season view and in the feed.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, ObjectCard>> CardsAsync(GameEventDbContext db, Guid seasonId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var latest = await db.Events.AsNoTracking()
            .Where(e => e.SeasonId == seasonId && e.Type == s_published)
            .OrderByDescending(e => e.Sequence)
            .Select(e => (Guid?)e.CommandId)
            .FirstOrDefaultAsync(ct);
        if (latest is not { } command)
        {
            return new Dictionary<string, ObjectCard>();
        }

        if (s_cards.TryGetValue(seasonId, out var cached) && cached.Command == command)
        {
            return cached.Cards;
        }

        var cards = new Dictionary<string, ObjectCard>(StringComparer.Ordinal);
        var packs = await db.Events.AsNoTracking()
            .Where(e => e.SeasonId == seasonId && e.Type == s_published)
            .OrderBy(e => e.Sequence)
            .Select(e => new { e.Type, e.Version, e.Data })
            .ToListAsync(ct);
        foreach (var pack in packs)
        {
            foreach (var definition in ((ContentPublished)EventCodec.Decode(new StoredEvent(pack.Type, pack.Version, pack.Data))).Pack.Objects)
            {
                cards[definition.Id] = new ObjectCard(definition.Name, definition.Description);
            }
        }

        s_cards[seasonId] = (command, cards);
        return cards;
    }
}

/// <summary>An object's card as the content describes it: its name and text.</summary>
public sealed record ObjectCard(string Name, string Description);

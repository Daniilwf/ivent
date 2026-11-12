using System.Collections.Concurrent;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

/// <summary>An object's card as the content describes it: its name and text.</summary>
public sealed record ObjectCard(string Name, string Description);

/// <summary>
/// The cards of a season's objects (D-444, D-446): the latest name and text of each id over the season's content packs
/// that were not undone (a definition dropped from a later pack keeps the card it last had, D-400). One mechanism for
/// every view that names an object — the feed's item uses, gifts and drawn events, the season view's drawn event. Kept
/// per season and recomputed only when the latest publication changes: a request checks one number instead of decoding
/// every pack (up to 1 MB each, the memory budget of RUNBOOK).
/// </summary>
public sealed class ContentCards
{
    private static readonly string s_published = EventCatalog.Describe(typeof(ContentPublished)).Name;

    private readonly ConcurrentDictionary<Guid, (long Sequence, IReadOnlyDictionary<string, ObjectCard> Cards)> _bySeason = new();
    private int _packReads;

    /// <summary>How many times the packs were read and decoded (for tests: a second request reads none).</summary>
    public int PackReads => Volatile.Read(ref _packReads);

    public async Task<IReadOnlyDictionary<string, ObjectCard>> OfAsync(GameEventDbContext db, Guid seasonId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var packs = db.Events.AsNoTracking().Where(e => e.SeasonId == seasonId && e.Type == s_published && e.UndoneByEventId == null);
        var latest = await packs.MaxAsync(e => (long?)e.Sequence, ct) ?? 0;
        if (latest == 0)
        {
            return new Dictionary<string, ObjectCard>();
        }

        if (_bySeason.TryGetValue(seasonId, out var cached) && cached.Sequence == latest)
        {
            return cached.Cards;
        }

        Interlocked.Increment(ref _packReads);
        var cards = new Dictionary<string, ObjectCard>(StringComparer.Ordinal);
        foreach (var row in await packs.OrderBy(e => e.Sequence).ToListAsync(ct))
        {
            if (EventCodec.Decode(new StoredEvent(row.Type, row.Version, row.Data)) is ContentPublished { Pack: var pack })
            {
                foreach (var definition in pack.Objects)
                {
                    cards[definition.Id] = new ObjectCard(definition.Name, definition.Description);
                }
            }
        }

        _bySeason[seasonId] = (latest, cards);
        return cards;
    }
}

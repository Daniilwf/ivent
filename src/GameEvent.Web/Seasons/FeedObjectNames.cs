using System.Collections.Concurrent;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

/// <summary>
/// The names of a season's objects for the feed (D-444): the latest name of each id over the season's content packs (a
/// deleted definition keeps the name it last had). Kept per season and recomputed only when a newer pack is published —
/// a page checks one number instead of decoding every pack (up to 1 MB each).
/// </summary>
public sealed class FeedObjectNames
{
    private readonly ConcurrentDictionary<Guid, (long Sequence, IReadOnlyDictionary<string, string> Names)> _bySeason = new();
    private int _packReads;

    /// <summary>How many times the packs were read and decoded (for tests: a second page reads none).</summary>
    public int PackReads => Volatile.Read(ref _packReads);

    public async Task<IReadOnlyDictionary<string, string>> OfAsync(GameEventDbContext db, Guid seasonId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var published = EventCatalog.Describe(typeof(Engine.Content.ContentPublished)).Name;
        var packs = db.Events.AsNoTracking().Where(e => e.SeasonId == seasonId && e.Type == published && e.UndoneByEventId == null);
        var latest = await packs.MaxAsync(e => (long?)e.Sequence, ct) ?? 0;
        if (_bySeason.TryGetValue(seasonId, out var cached) && cached.Sequence == latest)
        {
            return cached.Names;
        }

        Interlocked.Increment(ref _packReads);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in await packs.OrderBy(e => e.Sequence).ToListAsync(ct))
        {
            if (EventCodec.Decode(new StoredEvent(row.Type, row.Version, row.Data)) is Engine.Content.ContentPublished { Pack: var pack })
            {
                foreach (var definition in pack.Objects)
                {
                    names[definition.Id] = definition.Name;
                }
            }
        }

        _bySeason[seasonId] = (latest, names);
        return names;
    }
}

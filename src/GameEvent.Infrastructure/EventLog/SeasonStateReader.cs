using System.Collections.Concurrent;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.EventLog;

/// <summary>
/// The season folded from its log for the site's reads (D-828): kept between requests and caught up with the events
/// written since, so a read after every command folds only that command's events, not the whole log. The log is
/// append-only (an undo writes compensating events), so the fold of the old events plus the new ones is the fold of the
/// whole log; when the last event read is no longer there (the database was replaced), the season is folded anew.
/// Thread-safe: the state is immutable, and a newer entry is never replaced by an older one.
/// </summary>
public sealed class SeasonStateReader
{
    private readonly ConcurrentDictionary<Guid, Entry> _seasons = new();

    /// <summary>The season's state as the fold of its whole log now.</summary>
    public async Task<SeasonState> ReadAsync(GameEventDbContext db, Guid seasonId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var known = _seasons.GetValueOrDefault(seasonId) is { LastSequence: > 0 } entry ? entry : null;

        // The last event read comes again first: it proves the log the state was folded from is still this one
        var rows = await db.Events.AsNoTracking()
            .Where(e => e.SeasonId == seasonId && e.Sequence >= (known == null ? 0 : known.LastSequence))
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);
        if (known is not null && (rows.Count == 0 || rows[0].Sequence != known.LastSequence || rows[0].Id != known.LastId))
        {
            known = null;
            rows = await db.Events.AsNoTracking().Where(e => e.SeasonId == seasonId).OrderBy(e => e.Sequence).ToListAsync(ct);
        }

        var state = known?.State ?? SeasonState.Empty;
        foreach (var row in known is null ? rows : rows.Skip(1))
        {
            state = SeasonEngine.Apply(state, EventCodec.Decode(new StoredEvent(row.Type, row.Version, row.Data)));
        }

        if (rows.Count > 0)
        {
            var read = new Entry(state, rows[^1].Sequence, rows[^1].Id);
            _seasons.AddOrUpdate(seasonId, read, (_, current) => current.LastSequence > read.LastSequence ? current : read);
        }

        return state;
    }

    private sealed record Entry(SeasonState State, long LastSequence, long LastId);
}

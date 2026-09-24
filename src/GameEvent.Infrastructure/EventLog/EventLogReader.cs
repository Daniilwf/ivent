using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.EventLog;

public static class EventLogReader
{
    /// <summary>The season log in order, decoded (older formats upcast).</summary>
    public static async Task<List<IGameEvent>> ReadSeasonAsync(GameEventDbContext db, Guid seasonId, CancellationToken ct = default)
    {
        var rows = await ReadRowsAsync(db, seasonId, ct);
        return [.. rows.Select(Decode)];
    }

    /// <summary>Season state as the fold of its log, and the last sequence number (0 for a new season).</summary>
    public static async Task<(SeasonState State, long LastSequence)> ReplaySeasonAsync(
        GameEventDbContext db, Guid seasonId, CancellationToken ct = default)
    {
        var rows = await ReadRowsAsync(db, seasonId, ct);
        return (SeasonEngine.Replay(rows.Select(Decode)), rows.Count == 0 ? 0 : rows[^1].Sequence);
    }

    private static async Task<List<GameEventRecord>> ReadRowsAsync(GameEventDbContext db, Guid seasonId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Events.AsNoTracking()
            .Where(e => e.SeasonId == seasonId)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);
    }

    private static IGameEvent Decode(GameEventRecord r) => EventCodec.Decode(new StoredEvent(r.Type, r.Version, r.Data));
}

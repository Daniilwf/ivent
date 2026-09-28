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

    /// <summary>The season log by command, in log order: what an undo decides on (D-104).</summary>
    public static async Task<List<Engine.Undo.LoggedCommand>> ReadCommandsAsync(GameEventDbContext db, Guid seasonId, CancellationToken ct = default)
    {
        var rows = await ReadRowsAsync(db, seasonId, ct);
        var commands = new List<Engine.Undo.LoggedCommand>();
        foreach (var group in rows.GroupBy(r => r.CommandId).OrderBy(g => g.Min(r => r.Sequence)))
        {
            commands.Add(new Engine.Undo.LoggedCommand(group.Key, [.. group.OrderBy(r => r.Sequence).Select(Decode)]));
        }

        return commands;
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

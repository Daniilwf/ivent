using GameEvent.Infrastructure.Database;

namespace GameEvent.Infrastructure.EventLog;

/// <summary>Identifies public feed rows that still accept reactions. The queue checks the same policy as the feed reads.</summary>
public interface IFeedReactionTargets
{
    Task<IReadOnlySet<Guid>> FindAsync(GameEventDbContext db, Guid seasonId, IReadOnlyCollection<Guid> commandIds, CancellationToken ct);
}

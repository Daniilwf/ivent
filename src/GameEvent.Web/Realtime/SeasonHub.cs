using System.Threading.Channels;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Realtime;

/// <summary>
/// Sent to everyone watching a season after each committed command: refetch what you show. The sequences are the
/// command's place in the season log: a client that last saw <c>FromSequence - 1</c> missed nothing.
/// </summary>
public sealed record SeasonUpdate(Guid SeasonId, long FromSequence, long ToSequence, IReadOnlyList<string> Types);

/// <summary>
/// The answer to joining a season (E3, D-122): the last sequence of its log now, and what was committed after the
/// sequence the client last saw — one update per command, in log order. <c>Reload</c> — too much was missed to list:
/// refetch everything.
/// </summary>
public sealed record SeasonJoin(long LastSequence, IReadOnlyList<SeasonUpdate> Missed, bool Reload);

/// <summary>Sent to everyone watching the pool after a committed change of the pool or the category wheel (D-122).</summary>
public sealed record PoolUpdate(long FromSequence, long ToSequence, IReadOnlyList<string> Types);

/// <summary>
/// Real-time updates: a client joins the group of the season it shows, and the pool's group when it shows the pool. The
/// season log is public. A client that lost its connection resumes from the last sequence it saw and gets what it missed
/// (E3, D-122).
/// </summary>
[Authorize]
public sealed class SeasonHub(HubSessions sessions, IDbContextFactory<GameEventDbContext> dbFactory) : Hub
{
    public const string Path = "/hubs/season";
    public const string UpdateMethod = "seasonUpdated";
    public const string PoolGroup = "pool";
    public const string PoolUpdateMethod = "poolUpdated";

    /// <summary>How many missed commands a resume lists; more — the client reloads instead.</summary>
    public const int MaxMissedCommands = 100;

    public override Task OnConnectedAsync()
    {
        sessions.Add(Context);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        sessions.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public static string Group(Guid seasonId) => $"season:{seasonId}";

    /// <summary>Starts watching a season: nothing is missed yet, the answer only says where the log is.</summary>
    public Task<SeasonJoin> Join(Guid seasonId) => Resume(seasonId, long.MaxValue);

    /// <summary>
    /// Watches a season again after a lost connection (or a gap in the updates): what was committed after
    /// <paramref name="after"/>. The client is in the group before the log is read, so nothing falls between the two; an
    /// update both listed here and broadcast is told apart by its sequence.
    /// </summary>
    public async Task<SeasonJoin> Resume(Guid seasonId, long after)
    {
        if (seasonId == Guid.Empty)
        {
            // The global log is not a season
            throw new HubException("Unknown season.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(seasonId));
        await using var db = await dbFactory.CreateDbContextAsync(Context.ConnectionAborted);
        var last = await db.Events.Where(e => e.SeasonId == seasonId).MaxAsync(e => (long?)e.Sequence, Context.ConnectionAborted) ?? 0;
        if (after >= last)
        {
            return new SeasonJoin(last, [], false);
        }

        // One read, bounded: a command has a few events, so the first commands past the limit say "reload"
        var missed = await db.Events.AsNoTracking()
            .Where(e => e.SeasonId == seasonId && e.Sequence > after)
            .OrderBy(e => e.Sequence)
            .Select(e => new { e.Sequence, e.CommandId, e.Type })
            .Take((MaxMissedCommands + 1) * 50)
            .ToListAsync(Context.ConnectionAborted);
        var updates = new List<SeasonUpdate>();
        foreach (var command in missed.GroupBy(e => e.CommandId))
        {
            var events = command.ToList();
            updates.Add(new SeasonUpdate(seasonId, events[0].Sequence, events[^1].Sequence, [.. events.Select(e => e.Type)]));
        }

        return updates.Count > MaxMissedCommands || updates[^1].ToSequence < last
            ? new SeasonJoin(last, [], true)
            : new SeasonJoin(last, updates, false);
    }

    public Task Leave(Guid seasonId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(seasonId));

    /// <summary>Starts watching the pool and the category wheel (the pool page, the add form).</summary>
    public Task JoinPool() => Groups.AddToGroupAsync(Context.ConnectionId, PoolGroup);

    public Task LeavePool() => Groups.RemoveFromGroupAsync(Context.ConnectionId, PoolGroup);
}

/// <summary>
/// Hands committed events to <see cref="SeasonBroadcastWorker"/> without waiting: a slow client connection
/// must never hold up the command queue or the HTTP answer.
/// </summary>
public sealed class SeasonBroadcaster : ICommittedEventsListener
{
    // Season and pool updates share one channel, so the worker sends them in commit order
    private readonly Channel<object> _updates = Channel.CreateUnbounded<object>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    internal ChannelReader<object> Updates => _updates.Reader;

    public Task OnCommittedAsync(IReadOnlyList<LoggedEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return Task.CompletedTask;
        }

        IReadOnlyList<string> types = [.. events.Select(e => EventCatalog.Describe(e.Event.GetType()).Name)];
        if (events[0].SeasonId != Guid.Empty)
        {
            _updates.Writer.TryWrite(new SeasonUpdate(events[0].SeasonId, events[0].Sequence, events[^1].Sequence, types));
        }
        else if (events.Any(e => e.Event is GameAdded or GameChanged or GameDeleted or GameRestored or CategorySet or CategoryRemoved))
        {
            // Of the global log only the pool is anyone's to watch: accounts, files and bug reports are not broadcast
            _updates.Writer.TryWrite(new PoolUpdate(events[0].Sequence, events[^1].Sequence, types));
        }

        return Task.CompletedTask;
    }
}

/// <summary>Sends season and pool updates to hub groups, in commit order, off the command queue.</summary>
public sealed partial class SeasonBroadcastWorker(
    SeasonBroadcaster broadcaster, IHubContext<SeasonHub> hub, ILogger<SeasonBroadcastWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var update in broadcaster.Updates.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await (update switch
                    {
                        SeasonUpdate season => hub.Clients.Group(SeasonHub.Group(season.SeasonId)).SendAsync(SeasonHub.UpdateMethod, season, stoppingToken),
                        PoolUpdate pool => hub.Clients.Group(SeasonHub.PoolGroup).SendAsync(SeasonHub.PoolUpdateMethod, pool, stoppingToken),
                        _ => throw new InvalidOperationException($"Unknown update {update.GetType().Name}."),
                    });
                }
#pragma warning disable CA1031 // a failed broadcast is logged; clients catch up by resuming from their last sequence (D-122)
                catch (Exception e) when (e is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    LogBroadcastFailed(logger, e, update);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Broadcast of {Update} failed")]
    private static partial void LogBroadcastFailed(ILogger logger, Exception exception, object update);
}

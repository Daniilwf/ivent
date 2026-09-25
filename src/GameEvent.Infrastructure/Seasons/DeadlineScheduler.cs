using System.Security.Cryptography;
using System.Text;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameEvent.Infrastructure.Seasons;

/// <summary>How often the scheduler looks for seasons whose deadline has come (<c>Scheduler:IntervalSeconds</c>).</summary>
public sealed record DeadlineSchedulerSettings(TimeSpan Interval);

/// <summary>
/// Closes active seasons at their deadline (D-101). It only reads the projection and sends <see cref="ReachDeadline"/>
/// through the common queue as the system (no author); the engine decides by its own clock. The command id comes from
/// the season and the deadline, so a repeated tick is a duplicate, and a moved deadline gets a new id.
/// </summary>
public sealed partial class DeadlineScheduler(
    CommandBus bus,
    IDbContextFactory<GameEventDbContext> dbFactory,
    IClock clock,
    DeadlineSchedulerSettings settings,
    ILogger<DeadlineScheduler> logger) : BackgroundService
{
    /// <summary>One pass: every active season whose deadline has come gets a <see cref="ReachDeadline"/>.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        List<(Guid Id, DateTimeOffset Deadline)> due;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // DateTimeOffset is compared in memory: SQLite stores it as text.
            due = [.. (await db.Seasons.AsNoTracking()
                    .Where(s => s.Status == SeasonStatus.Active && s.Deadline != null)
                    .Select(s => new { s.Id, s.Deadline })
                    .ToListAsync(ct))
                .Where(s => s.Deadline <= now)
                .Select(s => (s.Id, s.Deadline!.Value))];
        }

        foreach (var (seasonId, deadline) in due)
        {
            var outcome = await bus.SendAsync(new CommandEnvelope(CommandIdFor(seasonId, deadline), seasonId, new ReachDeadline(), AuthorId: null), ct);
            if (outcome.IsAccepted && !outcome.IsDuplicate)
            {
                LogClosed(logger, seasonId, deadline);
            }
            else if (!outcome.IsAccepted)
            {
                LogRefused(logger, seasonId, outcome.Rejection?.Code);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.Interval);
        try
        {
            do
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failed pass is retried on the next tick; the deadline is still in the table.
                    LogTickFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    internal static Guid CommandIdFor(Guid seasonId, DateTimeOffset deadline)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"reach-deadline:{seasonId:N}:{deadline.UtcTicks}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Season {SeasonId} closed at its deadline {Deadline}")]
    private static partial void LogClosed(ILogger logger, Guid seasonId, DateTimeOffset deadline);

    [LoggerMessage(Level = LogLevel.Information, Message = "Season {SeasonId} was not closed at the deadline: {Code}")]
    private static partial void LogRefused(ILogger logger, Guid seasonId, string? code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Deadline scheduler pass failed")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}

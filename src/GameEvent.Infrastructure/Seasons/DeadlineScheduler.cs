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

/// <summary>
/// How often the scheduler looks for seasons whose deadline has come (<c>Scheduler:IntervalSeconds</c>, above 0), and
/// whether its own loop runs (<c>Scheduler:Enabled</c>; tests turn it off and call <see cref="DeadlineScheduler.TickAsync"/>).
/// </summary>
public sealed record DeadlineSchedulerSettings
{
    public DeadlineSchedulerSettings(TimeSpan interval, bool enabled = true)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Scheduler:IntervalSeconds must be above 0.");
        }

        Interval = interval;
        Enabled = enabled;
    }

    public TimeSpan Interval { get; }

    public bool Enabled { get; }
}

/// <summary>
/// Closes active seasons at their deadline (D-101) and fires the economy's timers (D-404). It only reads the projection
/// and sends <see cref="ReachDeadline"/> and <see cref="Engine.Economy.FireTimers"/> through the common queue as the
/// system (no author); the engine decides by its own clock. The command id comes from
/// the season and the deadline, so a repeated tick is a duplicate, and a moved deadline gets a new id.
/// </summary>
public sealed partial class DeadlineScheduler(
    CommandBus bus,
    IDbContextFactory<GameEventDbContext> dbFactory,
    IClock clock,
    DeadlineSchedulerSettings settings,
    ILogger<DeadlineScheduler> logger,
    Site.MaintenanceMode? maintenance = null) : BackgroundService
{
    /// <summary>
    /// One pass: every active season whose deadline has come gets a <see cref="ReachDeadline"/>. Under maintenance the
    /// pass waits (D-121): the queue would refuse, and the first pass after it closes the season.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (maintenance?.IsOn == true)
        {
            return;
        }

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

        await FireTimersAsync(now, ct);

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

    /// <summary>
    /// The economy's timers (D-404): every season with a player whose earliest timer (shop lots, effects of hours, bets)
    /// has come gets <see cref="Engine.Economy.FireTimers"/>; the command id comes from the season, that moment and the
    /// season's last event, so a repeated tick is a duplicate and a refusal (nothing due by the engine's clock) writes nothing.
    /// </summary>
    private async Task FireTimersAsync(DateTimeOffset now, CancellationToken ct)
    {
        List<(Guid SeasonId, DateTimeOffset Due)> due;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var running = db.Seasons.AsNoTracking().Where(s => s.Status == SeasonStatus.Active || s.Status == SeasonStatus.Closing).Select(s => s.Id);
            due = [.. (await db.SeasonPlayers.AsNoTracking()
                    .Where(p => p.NextTimerAt != null && running.Contains(p.SeasonId))
                    .Select(p => new { p.SeasonId, p.NextTimerAt })
                    .ToListAsync(ct))
                .Where(p => p.NextTimerAt <= now)
                .GroupBy(p => p.SeasonId)
                .Select(g => (g.Key, g.Min(p => p.NextTimerAt!.Value)))];
        }

        foreach (var (seasonId, moment) in due)
        {
            // The season's last event joins the id: after an undo brings back what a pass fired, the next pass is a new
            // command, not a duplicate of the undone one (D-404)
            long last;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                last = await db.Events.AsNoTracking().Where(e => e.SeasonId == seasonId).MaxAsync(e => (long?)e.Sequence, ct) ?? 0;
            }

            var id = CommandIdFor($"fire-timers:{last}", seasonId, moment);
            var outcome = await bus.SendAsync(new CommandEnvelope(id, seasonId, new Engine.Economy.FireTimers(), AuthorId: null), ct);
            if (!outcome.IsAccepted)
            {
                LogTimersRefused(logger, seasonId, outcome.Rejection?.Code);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(settings.Interval);
        try
        {
            do
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
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

    internal static Guid CommandIdFor(Guid seasonId, DateTimeOffset deadline) => CommandIdFor("reach-deadline", seasonId, deadline);

    private static Guid CommandIdFor(string kind, Guid seasonId, DateTimeOffset moment)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{seasonId:N}:{moment.UtcTicks}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Season {SeasonId} closed at its deadline {Deadline}")]
    private static partial void LogClosed(ILogger logger, Guid seasonId, DateTimeOffset deadline);

    [LoggerMessage(Level = LogLevel.Information, Message = "Season {SeasonId} was not closed at the deadline: {Code}")]
    private static partial void LogRefused(ILogger logger, Guid seasonId, string? code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Timers of season {SeasonId} were not fired: {Code}")]
    private static partial void LogTimersRefused(ILogger logger, Guid seasonId, string? code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Deadline scheduler pass failed")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}

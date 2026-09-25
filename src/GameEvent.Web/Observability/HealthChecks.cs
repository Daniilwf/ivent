using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GameEvent.Web.Observability;

/// <summary>The database answers a read of a real table (a missing schema is a failure, not an empty site).</summary>
public sealed class DatabaseHealthCheck(IDbContextFactory<GameEventDbContext> factory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await db.Seasons.AsNoTracking().AnyAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
#pragma warning disable CA1031 // any failure of the database is the answer, not a crash of the check
        catch (Exception e)
#pragma warning restore CA1031
        {
            return HealthCheckResult.Unhealthy("The database does not answer.", e);
        }
    }
}

/// <summary>Where the database file lives has room left (<c>Health:MinFreeDiskMb</c>, 200 by default).</summary>
public sealed class DiskHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(configuration.GetConnectionString("Main")).DataSource;
        var directory = Path.GetDirectoryName(Path.GetFullPath(string.IsNullOrEmpty(dataSource) ? "." : dataSource)) ?? ".";
        var minimum = configuration.GetValue("Health:MinFreeDiskMb", 200L) * 1024 * 1024;
        // On Linux the database sits on a mounted volume, not on the root filesystem: the drive is the folder itself
        var free = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(directory) ?? directory : directory).AvailableFreeSpace;
        return Task.FromResult(free >= minimum
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"{free / 1024 / 1024} MB free, {minimum / 1024 / 1024} MB needed."));
    }
}

/// <summary>
/// The command queue's consumer is running, no command hangs in it (<c>Health:MaxCommandSeconds</c>, 60 by default) and it
/// keeps up (<c>Health:MaxQueuedCommands</c>, 100 by default).
/// </summary>
public sealed class QueueHealthCheck(CommandBus bus, IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!bus.IsConsuming)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("The command queue is not running."));
        }

        var maxSeconds = configuration.GetValue("Health:MaxCommandSeconds", 60);
        if (bus.CurrentCommandRunningFor is { } running && running > TimeSpan.FromSeconds(maxSeconds))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy($"A command has run for {running.TotalSeconds:F0} s."));
        }

        var queued = bus.Queued;
        var limit = configuration.GetValue("Health:MaxQueuedCommands", 100);
        return Task.FromResult(queued <= limit
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded($"{queued} commands wait in the queue."));
    }
}

using GameEvent.Infrastructure.Site;

namespace GameEvent.Web.Hosting;

/// <summary>
/// Holds the site's lock while the site runs (<c>Site:LockFile</c>, /data/site.lock in the image; D-127): a one-off
/// command with a queue of its own refuses meanwhile, and the site refuses to start while such a command runs. Started
/// before the command queue, so no command of the site is handled without it.
/// </summary>
public sealed class SiteLockService(IConfiguration configuration, IHostEnvironment environment) : IHostedService, IDisposable
{
    private SiteLock? _lock;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(environment.ContentRootPath, configuration["Site:LockFile"]!);
        _lock = SiteLock.TryAcquire(path)
            ?? throw new InvalidOperationException($"{path} is held by another process (the first admin, an import): the site starts when it ends.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }
}

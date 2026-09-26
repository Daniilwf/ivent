using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Site;

namespace GameEvent.Web.Hosting;

/// <summary>
/// The server's one-off commands (J1, D-127): the first admin of a new site, and the container's health check. They run
/// in the image next to the site, with the same configuration.
/// </summary>
public static class Operations
{
    /// <summary>
    /// Creates the first admin through the queue with a temporary password shown once (D-106). Only on a site without an
    /// active admin — the queue checks it: after that, accounts are made from the admin page. The site must be stopped:
    /// the command has its own queue, and the site's lock keeps the two apart (<see cref="SiteLockService"/>).
    /// </summary>
    public static async Task<int> CreateFirstAdminAsync(IServiceProvider services, string login, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var outcome = await services.GetRequiredService<CommandBus>()
            .SendAsync(new CommandEnvelope(Guid.CreateVersion7(), Guid.Empty, new CreateFirstAdmin(login, name), AuthorId: null), ct);
        if (!outcome.IsAccepted)
        {
            await Console.Error.WriteLineAsync($"Refused: {outcome.Rejection!.Code} — {outcome.Rejection.Detail}");
            return 1;
        }

        Console.WriteLine($"Admin «{login}» created. Temporary password (shown once, changed at the first sign-in): {outcome.Secret}");
        return 0;
    }

    /// <summary>
    /// The one-off commands run next to the site's data: they take the site's lock first when the site keeps one
    /// (<c>Site:LockFile</c>), so they refuse while the site runs — and the site does not start while they do.
    /// </summary>
    public static SiteLock? LockForOneOff(IConfiguration configuration, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration["Site:LockFile"] is not { Length: > 0 } file)
        {
            return null;
        }

        return SiteLock.TryAcquire(Path.Combine(contentRoot, file))
            ?? throw new InvalidOperationException("The site is running: stop it first (docker compose stop site), the command has its own queue.");
    }

    /// <summary>The container's health check: 0 when the site answers /health with 200, 1 otherwise.</summary>
    public static async Task<int> HealthCheckAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await http.GetAsync(new Uri(url));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}

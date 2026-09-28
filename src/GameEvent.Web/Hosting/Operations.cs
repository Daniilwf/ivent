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
    /// (<see cref="SitePaths.LockFile"/>), so they refuse while the site runs — and the site does not start while they do.
    /// </summary>
    public static SiteLock? LockForOneOff(IConfiguration configuration, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (SitePaths.LockFile(configuration, contentRoot) is not { } file)
        {
            return null;
        }

        return SiteLock.TryAcquire(file)
            ?? throw new InvalidOperationException("The site is running: stop it first (docker compose stop site), the command has its own queue.");
    }

    public const string Usage =
        "Usage: [options] | migrate | create-admin <login> <name> | seed-dev | seed-demo | healthcheck | backup | backup-verify … | restore …";

    private static readonly HashSet<string> s_commands =
        ["migrate", "create-admin", "seed-dev", "seed-demo", "healthcheck", "backup", "backup-verify", "restore"];

    /// <summary>
    /// The first argument is a command the image knows, or no command at all (none, an option <c>--urls …</c> or a
    /// <c>key=value</c> setting): the site. Anything else is refused, so a mistyped command — or one an older image does
    /// not have yet — never starts a second site on the same data (D-214).
    /// </summary>
    public static bool IsKnownCommand(string? first) =>
        first is null || s_commands.Contains(first) || first.StartsWith('-') || first.Contains('=', StringComparison.Ordinal);

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

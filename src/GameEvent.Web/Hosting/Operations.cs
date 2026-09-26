using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Hosting;

/// <summary>
/// The server's one-off commands (J1, D-127): the first admin of a new site, and the container's health check. They run
/// in the image next to the site, with the same configuration.
/// </summary>
public static class Operations
{
    /// <summary>
    /// Creates the first admin through the queue with a temporary password shown once (D-106). Only on a site without an
    /// active admin: after that, accounts are made from the admin page. Run it with the site stopped — the command has its
    /// own queue and there must be one.
    /// </summary>
    public static async Task<int> CreateFirstAdminAsync(IServiceProvider services, string login, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using (var db = await services.GetRequiredService<IDbContextFactory<GameEventDbContext>>().CreateDbContextAsync(ct))
        {
            if (await db.Users.AnyAsync(u => u.Role == Role.Admin && !u.IsDeleted, ct))
            {
                await Console.Error.WriteLineAsync("The site has an admin already: new accounts are made on the admin page.");
                return 1;
            }
        }

        var outcome = await services.GetRequiredService<CommandBus>()
            .SendAsync(new CommandEnvelope(Guid.CreateVersion7(), Guid.Empty, new CreateAccount(login, name, Role.Admin), AuthorId: null), ct);
        if (!outcome.IsAccepted)
        {
            await Console.Error.WriteLineAsync($"Refused: {outcome.Rejection!.Code} — {outcome.Rejection.Detail}");
            return 1;
        }

        Console.WriteLine($"Admin «{login}» created. Temporary password (shown once, changed at the first sign-in): {outcome.Secret}");
        return 0;
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

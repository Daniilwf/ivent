using GameEvent.Infrastructure.Site;
using GameEvent.Web.Backups;
using GameEvent.Web.Hosting;

// Commands: (none) — run the site; `migrate` — apply migrations and exit (a separate deployment step, D-27);
// `seed-dev` — fill a local database for development and exit; `seed-demo` — the demo season (16 players, bots, F2);
// `create-admin <login> <name>` — the first admin of a new site, with a temporary password (J1, D-127);
// `healthcheck` — ask the running site's /health and exit 0 when it is healthy (the container's health check);
// `backup` — a backup archive while the site runs; `backup-verify [archive or folder]` — prove the newest (or a given)
// backup restores; `restore <archive> [--without-keys]` — only with the site stopped (J3, D-210…D-212).
var command = args.FirstOrDefault();

if (command == "healthcheck")
{
    return await Operations.HealthCheckAsync(Environment.GetEnvironmentVariable("HEALTHCHECK_URL") ?? "http://localhost:8080/health");
}

if (command is "backup" or "backup-verify" or "restore")
{
    // The site's configuration without the site: no queue, no lock of its own, no port. Arguments are paths, not settings
    var settingsHost = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
    var settings = BackupSettings.From(settingsHost.Configuration, settingsHost.Environment.ContentRootPath);
    var rest = args.Skip(1).ToList();
    var withoutKeys = rest.Remove("--without-keys");
    switch (command)
    {
        case "backup" when rest.Count == 0:
            return await BackupCommands.BackupAsync(settings, DateTimeOffset.UtcNow, Console.Out);
        case "backup-verify" when rest.Count <= 1:
            return await BackupCommands.VerifyAsync(rest.FirstOrDefault(), settings, Console.Out);
        case "restore" when rest.Count == 1:
            return await BackupCommands.RestoreAsync(rest[0], !withoutKeys, settings, DateTimeOffset.UtcNow, Console.Out);
        default:
            await Console.Error.WriteLineAsync("Usage: backup | backup-verify [archive or folder] | restore <archive> [--without-keys]");
            return 2;
    }
}

var builder = WebApplication.CreateBuilder(args);
var oneOff = command is "seed-dev" or "seed-demo" or "create-admin";
if (oneOff)
{
    // A one-off command runs its own queue for its commands only: no deadlines, no site lock of its own (it takes the
    // site's instead), no public port
    builder.Configuration["Scheduler:Enabled"] = "false";
    builder.Configuration["Site:OneOff"] = "true";
    builder.WebHost.UseUrls("http://127.0.0.1:0");
}

builder.AddGameEvent();

var app = builder.Build();
app.UseGameEvent();

switch (command)
{
    case "migrate":
        await AppSetup.MigrateAsync(app.Configuration);
        return 0;

    case "create-admin":
        if (args.Length != 3)
        {
            await Console.Error.WriteLineAsync("Usage: create-admin <login> <name>");
            return 2;
        }

        SiteLock? siteLock;
        try
        {
            siteLock = Operations.LockForOneOff(app.Configuration, app.Environment.ContentRootPath);
        }
        catch (InvalidOperationException e)
        {
            await Console.Error.WriteLineAsync(e.Message);
            return 1;
        }

        using (siteLock)
        {
            await app.StartAsync();
            var exit = await Operations.CreateFirstAdminAsync(app.Services, args[1], args[2]);
            await app.StopAsync();
            return exit;
        }

    case "seed-dev" or "seed-demo":
        if (!app.Environment.IsDevelopment())
        {
            await Console.Error.WriteLineAsync($"{command} runs only in the Development environment.");
            return 1;
        }

        await AppSetup.MigrateAsync(app.Configuration);
        await app.StartAsync();
        var contentRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", ".."));
        if (command == "seed-demo")
        {
            try
            {
                await DemoSeed.RunAsync(app.Services, contentRoot);
            }
            catch (InvalidOperationException e)
            {
                await Console.Error.WriteLineAsync(e.Message);
                await app.StopAsync();
                return 1;
            }
        }
        else
        {
            await DevSeed.RunAsync(app.Services, contentRoot);
        }

        await app.StopAsync();
        return 0;

    default:
        await app.RunAsync();
        return 0;
}

/// <summary>Entry point, exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;

using GameEvent.Web.Hosting;

// Commands: (none) — run the site; `migrate` — apply migrations and exit (a separate deployment step, D-27);
// `seed-dev` — fill a local database for development and exit.
var command = args.FirstOrDefault();

var builder = WebApplication.CreateBuilder(args);
builder.AddGameEvent();
if (command == "seed-dev")
{
    builder.WebHost.UseUrls("http://127.0.0.1:0");
}

var app = builder.Build();
app.UseGameEvent();

switch (command)
{
    case "migrate":
        await AppSetup.MigrateAsync(app.Configuration);
        return 0;

    case "seed-dev":
        if (!app.Environment.IsDevelopment())
        {
            await Console.Error.WriteLineAsync("seed-dev runs only in the Development environment.");
            return 1;
        }

        await AppSetup.MigrateAsync(app.Configuration);
        await app.StartAsync();
        await DevSeed.RunAsync(app.Services, Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..")));
        await app.StopAsync();
        return 0;

    default:
        await app.RunAsync();
        return 0;
}

/// <summary>Entry point, exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;

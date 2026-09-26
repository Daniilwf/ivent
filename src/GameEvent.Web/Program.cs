using GameEvent.Web.Hosting;

// Commands: (none) — run the site; `migrate` — apply migrations and exit (a separate deployment step, D-27);
// `seed-dev` — fill a local database for development and exit; `seed-demo` — the demo season (16 players, bots, F2).
var command = args.FirstOrDefault();

var builder = WebApplication.CreateBuilder(args);
builder.AddGameEvent();
if (command is "seed-dev" or "seed-demo")
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

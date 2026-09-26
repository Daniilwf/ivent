using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Hosting;

/// <summary>
/// Small seed for local development (`dotnet run -- seed-dev`, Development only, F2, D-126): an admin, five players, a
/// spectator, the 50 games of content/pool.dev.json and a running season. Everything goes through the command queue, the
/// pool too; a re-run adds only what is missing.
/// </summary>
public static class DevSeed
{
    public static readonly Guid SeasonId = Guid.Parse("5ea50000-0000-0000-0000-000000000001");

    private static readonly (string Login, string Name, Role Role)[] s_users =
    [
        ("admin", "Админ", Role.Admin),
        ("vasya", "Вася", Role.Player),
        ("petya", "Петя", Role.Player),
        ("masha", "Маша", Role.Player),
        ("kolya", "Коля", Role.Player),
        ("dasha", "Даша", Role.Player),
        ("zritel", "Зритель", Role.Spectator),
    ];

    public static async Task RunAsync(IServiceProvider services, string contentRoot, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<GameEventDbContext>>();
        var bus = services.GetRequiredService<CommandBus>();
        var password = services.GetRequiredService<IConfiguration>()["DevSeed:Password"]
            ?? throw new InvalidOperationException("DevSeed:Password is not set (appsettings.Development.json).");
        await SeedAccountsAsync(bus, factory, s_users, password, ct);
        await SeedPoolAsync(bus, factory, Path.Combine(contentRoot, "content", "pool.dev.json"), ct);
        await SeedSeasonAsync(bus, factory, ct);
    }

    /// <summary>The accounts that are not there yet, through the queue like any account (D-106), with a known password.</summary>
    internal static async Task SeedAccountsAsync(
        CommandBus bus, IDbContextFactory<GameEventDbContext> factory, IEnumerable<(string Login, string Name, Role Role)> users, string password, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.Users.Select(u => u.NormalizedLogin).ToListAsync(ct);
        foreach (var (login, name, role) in users.Where(u => !existing.Contains(UserRecord.Normalize(u.Login))))
        {
            await SendAsync(bus, Guid.Empty, new SeedAccount(login, name, role, password), ct);
        }
    }

    /// <summary>A pool file through the queue (D-126): categories, then the games the pool does not have yet.</summary>
    internal static async Task SeedPoolAsync(CommandBus bus, IDbContextFactory<GameEventDbContext> factory, string path, CancellationToken ct)
    {
        var pool = JsonSerializer.Deserialize<PoolFile>(await File.ReadAllTextAsync(path, ct), PoolImport.DemoJson)
            ?? throw new InvalidDataException($"{path} is not a pool file.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var categories = await db.Categories.Select(c => c.Name).ToListAsync(ct);
        foreach (var category in pool.Categories.Where(c => !categories.Contains(c.Name)))
        {
            await SendAsync(bus, Guid.Empty, new SetCategory(category.Name, category.Weight), ct);
        }

        var titles = await db.Games.Select(g => g.Title).ToListAsync(ct);
        foreach (var game in pool.Games.Where(g => !titles.Any(t => PoolRules.IsSame(t, g.Title))))
        {
            // The pool files hold alike titles on purpose (the table's sequels): confirmed, as the import confirms them
            var card = new GameCard(game.Title, [.. game.Tags], game.Hours, null, null, null, game.Note, false);
            await SendAsync(bus, Guid.Empty, new AddGame(card, null, Force: true, game.Author), ct);
        }
    }

    /// <summary>Creates the season and adds every player who is not in it yet: safe to re-run after a partial seed.</summary>
    private static async Task SeedSeasonAsync(CommandBus bus, IDbContextFactory<GameEventDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Seasons.AnyAsync(s => s.Id == SeasonId, ct))
        {
            await SendAsync(bus, SeasonId, new CreateSeason(SeasonId, "Сезон разработки", RulesetJson.Default()), ct);
        }

        if (await db.Seasons.AnyAsync(s => s.Id == SeasonId && s.Status == SeasonStatus.Draft, ct))
        {
            await SendAsync(bus, SeasonId, new ChangeSeasonStatus(SeasonStatus.Active), ct);
        }

        var joined = await db.SeasonPlayers.Where(p => p.SeasonId == SeasonId).Select(p => p.UserId).ToListAsync(ct);
        var players = await db.Users.Where(u => u.Role == Role.Player && !joined.Contains(u.Id)).OrderBy(u => u.Login).ToListAsync(ct);
        foreach (var player in players)
        {
            await SendAsync(bus, SeasonId, new AddSeasonPlayer(Guid.CreateVersion7(), player.Id, player.Name), ct);
        }
    }

    internal static async Task SendAsync(CommandBus bus, Guid seasonId, ICommand command, CancellationToken ct, Guid? authorId = null)
    {
        var outcome = await bus.SendAsync(new CommandEnvelope(Guid.CreateVersion7(), seasonId, command, authorId), ct);
        if (!outcome.IsAccepted)
        {
            throw new InvalidOperationException($"Seed command {command.GetType().Name} was rejected: {outcome.Rejection}");
        }
    }
}

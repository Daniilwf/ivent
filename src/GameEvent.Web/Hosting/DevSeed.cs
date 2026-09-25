using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Hosting;

/// <summary>
/// Small seed for local development (`dotnet run -- seed-dev`, Development only): an admin, three players,
/// a spectator, the pool from content/pool.dev.json and a season. Accounts and the season go through the command queue;
/// the pool is written directly (there are no pool commands yet, task E2).
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
        ("zritel", "Зритель", Role.Spectator),
    ];

    public static async Task RunAsync(IServiceProvider services, string contentRoot, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<GameEventDbContext>>();
        await SeedAccountsAndPoolAsync(services, factory, contentRoot, ct);
        await SeedSeasonAsync(services.GetRequiredService<CommandBus>(), factory, ct);
    }

    private static async Task SeedAccountsAndPoolAsync(
        IServiceProvider services, IDbContextFactory<GameEventDbContext> factory, string contentRoot, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Users.AnyAsync(ct))
        {
            return;
        }

        var password = services.GetRequiredService<IConfiguration>()["DevSeed:Password"]
            ?? throw new InvalidOperationException("DevSeed:Password is not set (appsettings.Development.json).");

        // Accounts go through the queue like any account (D-106), with a known password and no change required
        var bus = services.GetRequiredService<CommandBus>();
        foreach (var (login, name, role) in s_users)
        {
            var outcome = await bus.SendAsync(
                new CommandEnvelope(Guid.CreateVersion7(), Guid.Empty, new SeedAccount(login, name, role, password), AuthorId: null), ct);
            if (!outcome.IsAccepted)
            {
                throw new InvalidOperationException($"Seed account {login} was rejected: {outcome.Rejection}");
            }
        }

        var pool = JsonSerializer.Deserialize<PoolFile>(
            await File.ReadAllTextAsync(Path.Combine(contentRoot, "content", "pool.dev.json"), ct), EngineJson.Options)!;
        db.Categories.AddRange(pool.Categories.Select(c => new CategoryRecord { Name = c.Name, Weight = c.Weight }));
        db.Games.AddRange(pool.Games.Select(g => new GameRecord
        {
            Id = Guid.CreateVersion7(),
            Title = g.Title,
            TagsJson = PoolReader.TagsToJson(g.Tags),
            Hours = g.Hours,
        }));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Creates the season and adds every player who is not in it yet: safe to re-run after a partial seed.</summary>
    private static async Task SeedSeasonAsync(CommandBus bus, IDbContextFactory<GameEventDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Seasons.AnyAsync(s => s.Id == SeasonId, ct))
        {
            await SendAsync(bus, new CreateSeason(SeasonId, "Сезон разработки", RulesetJson.Default()), ct);
        }

        if (await db.Seasons.AnyAsync(s => s.Id == SeasonId && s.Status == SeasonStatus.Draft, ct))
        {
            await SendAsync(bus, new ChangeSeasonStatus(SeasonStatus.Active), ct);
        }

        var joined = await db.SeasonPlayers.Where(p => p.SeasonId == SeasonId).Select(p => p.UserId).ToListAsync(ct);
        var players = await db.Users.Where(u => u.Role == Role.Player && !joined.Contains(u.Id)).OrderBy(u => u.Login).ToListAsync(ct);
        foreach (var player in players)
        {
            await SendAsync(bus, new AddSeasonPlayer(Guid.CreateVersion7(), player.Id, player.Name), ct);
        }
    }

    private static async Task SendAsync(CommandBus bus, ICommand command, CancellationToken ct)
    {
        var outcome = await bus.SendAsync(new CommandEnvelope(Guid.CreateVersion7(), SeasonId, command, AuthorId: null), ct);
        if (!outcome.IsAccepted)
        {
            throw new InvalidOperationException($"Seed command {command} was rejected: {outcome.Rejection}");
        }
    }

    private sealed record PoolFile(IReadOnlyList<CategoryEntry> Categories, IReadOnlyList<GameEntry> Games);

    private sealed record CategoryEntry(string Name, int Weight);

    private sealed record GameEntry(string Title, IReadOnlyList<string> Tags, decimal? Hours);
}

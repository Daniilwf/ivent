using System.Text.Json;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Web.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The seeds (F2, D-126): the small one for development and the demo season — 16 players, the whole demo pool and weeks
/// of play by bots through the engine. The demo season passes the integrity check, holds every kind of state the screens
/// must show, is the same every time, lies wholly in the past, and is never built over another database. The bots take
/// seconds, so these tests are Slow: they run in `npm test` and CI, not before every turn.
/// </summary>
[Trait("Category", "Slow")]
public sealed class DemoSeedTests : IAsyncLifetime
{
    private const string Password = "demo-password-1";

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task The_demo_season_is_whole_holds_every_state_and_lies_in_the_past()
    {
        using var site = _site.WithWebHostBuilder(b => b.UseSetting("DevSeed:Password", Password));
        var start = _site.Clock.UtcNow;

        Assert.True(await DemoSeed.RunAsync(site.Services, RepositoryRoot(), Ct));

        Assert.Equal(start, _site.Clock.UtcNow);
        await using var db = _site.NewDb();
        Assert.Equal(16, await db.SeasonPlayers.CountAsync(p => p.SeasonId == DemoSeed.SeasonId, Ct));
        Assert.Equal(604, await db.Games.CountAsync(Ct));
        Assert.Equal(14, await db.Categories.CountAsync(Ct));
        var events = await db.Events.Where(e => e.SeasonId == DemoSeed.SeasonId).OrderBy(e => e.Sequence).ToListAsync(Ct);
        var types = events.Select(e => e.Type).ToList();
        Assert.True(types.Count > 300, $"only {types.Count} events");
        string[] expected =
        [
            "game-rolled", "game-rerolled", "run-started", "run-completed", "run-reviewed", "run-dropped", "run-tech-rerolled",
            "proof-submitted", "proof-approved", "proof-rejected", "manual-effect-created", "manual-effect-resolved",
            "player-inactivity-set", "player-adjusted", "command-undone", "player-finished", "player-frozen", "season-deadline-set",
        ];
        Assert.Empty(expected.Except(types));

        // The deadline is the last command and nothing is later than now: real commands follow in order
        Assert.Equal("season-deadline-set", types[^1]);
        Assert.All(events, e => Assert.True(e.OccurredAt <= start, $"{e.Type} at {e.OccurredAt:O} is after {start:O}"));

        // Waiting states for the screens: a proof in the queue, a manual effect to play, a run, an inactive player, the first frozen
        Assert.True(await db.Proofs.AnyAsync(p => p.SeasonId == DemoSeed.SeasonId && p.Status == Engine.Proofs.ProofStatus.Pending, Ct));
        Assert.True(await db.ManualEffects.AnyAsync(e => e.SeasonId == DemoSeed.SeasonId, Ct));
        Assert.True(await db.SeasonPlayers.AnyAsync(p => p.SeasonId == DemoSeed.SeasonId && p.Phase == TurnPhase.Playing, Ct));
        Assert.True(await db.SeasonPlayers.AnyAsync(p => p.SeasonId == DemoSeed.SeasonId && p.IsInactive, Ct));
        Assert.True(await db.SeasonPlayers.AnyAsync(p => p.SeasonId == DemoSeed.SeasonId && p.Frozen, Ct));
        var integrity = await SeasonIntegrity.CheckAsync(db, DemoSeed.SeasonId, Ct);
        Assert.True(integrity!.IsIntact, string.Join("; ", integrity.Differences));

        // Built once: a second run finds it whole and adds nothing
        Assert.True(await DemoSeed.RunAsync(site.Services, RepositoryRoot(), Ct));
        Assert.Equal(events.Count, await db.Events.CountAsync(e => e.SeasonId == DemoSeed.SeasonId, Ct));
    }

    [Fact]
    public async Task The_same_seed_gives_the_same_demo_season()
    {
        await using var other = new SiteFactory();
        var first = await PlayAsync(_site);
        var second = await PlayAsync(other);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task A_demo_season_cut_short_is_not_taken_for_a_whole_one()
    {
        using var site = _site.WithWebHostBuilder(b => b.UseSetting("DevSeed:Password", Password));
        _ = site.Server;
        var outcome = await site.Services.GetRequiredService<CommandBus>()
            .SendAsync(new CommandEnvelope(Guid.NewGuid(), DemoSeed.SeasonId, new CreateSeason(DemoSeed.SeasonId, "Демо-сезон", Engine.Rulesets.RulesetJson.Default()), null), Ct);
        Assert.True(outcome.IsAccepted);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DemoSeed.RunAsync(site.Services, RepositoryRoot(), Ct));

        Assert.Contains("seed:demo", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_demo_season_is_not_built_over_another_database()
    {
        await _site.SeedAsync();
        using var site = _site.WithWebHostBuilder(b => b.UseSetting("DevSeed:Password", Password));

        await Assert.ThrowsAsync<InvalidOperationException>(() => DemoSeed.RunAsync(site.Services, RepositoryRoot(), Ct));

        await using var db = _site.NewDb();
        Assert.False(await db.Seasons.AnyAsync(s => s.Id == DemoSeed.SeasonId, Ct));
        Assert.Equal(3, await db.Games.CountAsync(Ct));
    }

    [Fact]
    public async Task The_small_seed_has_an_admin_five_players_fifty_games_and_a_season()
    {
        using var site = _site.WithWebHostBuilder(b => b.UseSetting("DevSeed:Password", Password));

        await DevSeed.RunAsync(site.Services, RepositoryRoot(), Ct);
        await DevSeed.RunAsync(site.Services, RepositoryRoot(), Ct);

        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Users.CountAsync(u => u.Role == Infrastructure.Accounts.Role.Admin, Ct));
        Assert.Equal(5, await db.SeasonPlayers.CountAsync(p => p.SeasonId == DevSeed.SeasonId, Ct));
        Assert.Equal(50, await db.Games.CountAsync(Ct));
        Assert.True(await db.Games.AnyAsync(g => g.Note != null, Ct));
        Assert.True((await SeasonIntegrity.CheckAsync(db, DevSeed.SeasonId, Ct))!.IsIntact);
    }

    // ---- Helpers ----

    /// <summary>The demo season as a sequence of event types with the titles of the rolled games.</summary>
    private static async Task<List<string>> PlayAsync(SiteFactory factory)
    {
        using var site = factory.WithWebHostBuilder(b => b.UseSetting("DevSeed:Password", Password));
        await DemoSeed.RunAsync(site.Services, RepositoryRoot(), Ct);
        await using var db = factory.NewDb();
        var titles = await db.Games.ToDictionaryAsync(g => g.Id, g => g.Title, Ct);
        return [.. (await db.Events.Where(e => e.SeasonId == DemoSeed.SeasonId).OrderBy(e => e.Sequence).ToListAsync(Ct))
            .Select(e => e.Type == "game-rolled"
                ? $"{e.Type} {titles[JsonDocument.Parse(e.Data).RootElement.GetProperty("gameId").GetGuid()]}"
                : e.Type)];
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "content", "pool.demo.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root with content/ is not found.");
    }
}

/// <summary>
/// The demo pool for the public repository (D-30, D-126): the table's authors become «Автор N» in the order they first
/// appear, notes never reach the file, a title twice is written once, a blank author is none.
/// </summary>
public sealed class DemoPoolTests
{
    [Fact]
    public void No_nickname_and_no_note_reaches_the_demo_pool()
    {
        var table = new ImportedTable(
            [
                new ImportedGame(2, "Hades", ["Roguelike"], "Яковка", "Челлендж от Винила: без смертей"),
                new ImportedGame(3, "Celeste", ["Platformer"], " Винил ", null),
                new ImportedGame(4, " hades ", ["Roguelike"], "Дензел", "Повтор"),
                new ImportedGame(5, "Portal", ["Puzzle"], "яковка", "Для Дензела"),
                new ImportedGame(6, "Limbo", ["Puzzle"], "   ", null),
            ],
            [new ImportedCategory(2, "Puzzle", 3), new ImportedCategory(3, "Roguelike", 2), new ImportedCategory(4, "puzzle", 9)],
            []);

        var demo = PoolImport.DemoPool(table);
        var json = JsonSerializer.Serialize(demo, PoolImport.DemoJson);

        Assert.Equal(["Hades", "Celeste", "Portal", "Limbo"], demo.Games.Select(g => g.Title));
        Assert.Equal(["Автор 1", "Автор 2", "Автор 1", null], demo.Games.Select(g => g.Author));
        Assert.All(demo.Games, g => Assert.Null(g.Note));
        Assert.Equal(2, demo.NotesDropped);
        Assert.Equal([("Puzzle", 3), ("Roguelike", 2)], demo.Categories.Select(c => (c.Name, c.Weight)));
        foreach (var nickname in new[] { "Яковка", "Винил", "Дензел", "Челлендж", "note", "notesDropped" })
        {
            Assert.DoesNotContain(nickname, json, StringComparison.OrdinalIgnoreCase);
        }
    }
}

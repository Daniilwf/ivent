using GameEvent.Infrastructure.Seasons;
using GameEvent.Web.Hosting;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The seeds (F2, D-126): the small one for development and the demo season — 16 players, the whole demo pool and weeks
/// of play by bots through the engine. The demo season passes the integrity check and holds every kind of state the
/// screens must show.
/// </summary>
public sealed class DemoSeedTests : IAsyncLifetime
{
    private const string Password = "demo-password-1";

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task The_demo_season_is_whole_and_holds_every_state()
    {
        using var site = _site.WithWebHostBuilder(b => b.UseSetting("DevSeed:Password", Password));
        var start = _site.Clock.UtcNow;

        await DemoSeed.RunAsync(site.Services, RepositoryRoot(), Ct);

        Assert.Equal(start, _site.Clock.UtcNow);
        await using var db = _site.NewDb();
        Assert.Equal(16, await db.SeasonPlayers.CountAsync(p => p.SeasonId == DemoSeed.SeasonId, Ct));
        Assert.Equal(604, await db.Games.CountAsync(Ct));
        Assert.Equal(14, await db.Categories.CountAsync(Ct));
        var types = await db.Events.Where(e => e.SeasonId == DemoSeed.SeasonId).Select(e => e.Type).ToListAsync(Ct);
        Assert.True(types.Count > 300, $"only {types.Count} events");
        foreach (var type in new[]
        {
            "game-rolled", "game-rerolled", "run-started", "run-completed", "run-reviewed", "run-dropped", "run-tech-rerolled",
            "proof-submitted", "proof-approved", "proof-rejected", "manual-effect-created", "manual-effect-resolved",
            "player-inactivity-set", "player-adjusted", "command-undone", "season-deadline-set",
        })
        {
            Assert.Contains(type, types);
        }

        // Waiting states for the screens: a proof in the queue, a manual effect to play, someone playing, an inactive player
        Assert.True(await db.Proofs.AnyAsync(p => p.SeasonId == DemoSeed.SeasonId && p.Status == Engine.Proofs.ProofStatus.Pending, Ct));
        Assert.True(await db.ManualEffects.AnyAsync(e => e.SeasonId == DemoSeed.SeasonId, Ct));
        Assert.True(await db.SeasonPlayers.AnyAsync(p => p.SeasonId == DemoSeed.SeasonId && p.Phase == Engine.Seasons.TurnPhase.Playing, Ct));
        Assert.True(await db.SeasonPlayers.AnyAsync(p => p.SeasonId == DemoSeed.SeasonId && p.IsInactive, Ct));
        var integrity = await SeasonIntegrity.CheckAsync(db, DemoSeed.SeasonId, Ct);
        Assert.True(integrity!.IsIntact, string.Join("; ", integrity.Differences));
    }

    [Fact]
    public async Task The_demo_season_is_the_same_every_time_and_is_seeded_once()
    {
        using var site = _site.WithWebHostBuilder(b => b.UseSetting("DevSeed:Password", Password));
        await DemoSeed.RunAsync(site.Services, RepositoryRoot(), Ct);
        int events;
        await using (var db = _site.NewDb())
        {
            events = await db.Events.CountAsync(Ct);
        }

        await DemoSeed.RunAsync(site.Services, RepositoryRoot(), Ct);

        await using var after = _site.NewDb();
        Assert.Equal(events, await after.Events.CountAsync(Ct));
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
        Assert.Equal(0, await db.Events.CountAsync(e => e.SeasonId == Guid.Empty && e.Type == "game-added" && e.AuthorId != null, Ct));
        Assert.True((await SeasonIntegrity.CheckAsync(db, DevSeed.SeasonId, Ct))!.IsIntact);
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

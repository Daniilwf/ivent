using System.Net.Http.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Tests.Queue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The whole site over a fresh SQLite file: accounts (admin, three players, a spectator), a small pool
/// and a season with Вася and Петя. Маша is a player outside the season.
/// </summary>
internal sealed class SiteFactory : WebApplicationFactory<Program>
{
    public const string Password = "test-password-1";
    public static readonly Guid SeasonId = Guid.Parse("30000000-0000-0000-0000-0000000000aa");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "game-event-tests", Guid.NewGuid().ToString("N"));
    private readonly int _loginAttemptsPerMinute;
    private readonly string _environment;

    public SiteFactory() : this(loginAttemptsPerMinute: 1000)
    {
    }

    public SiteFactory(int loginAttemptsPerMinute, string environment = "Test", string? frontendPath = null)
    {
        _loginAttemptsPerMinute = loginAttemptsPerMinute;
        _environment = environment;
        FrontendPath = frontendPath ?? Path.Combine(_directory, "no-frontend");
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "site.db");
        ConnectionString = $"Data Source={file};Pooling=False";

        // Migrating costs about as much as a whole test: the schema is migrated once per test run and copied
        File.Copy(s_template.Value, file);
    }

    /// <summary>A database migrated once for the whole test run; every site starts from a copy of it.</summary>
    private static readonly Lazy<string> s_template = new(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "game-event-tests", "template-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "site.db");
        SqliteDatabase.MigrateAsync($"Data Source={file};Pooling=False").GetAwaiter().GetResult();
        return file;
    });

    /// <summary>
    /// One hash of <see cref="Password"/> for every seeded account, in the site's own format (PBKDF2, the iteration count
    /// stored in the hash) but with few iterations: the production count is slow on purpose, and every sign-in of every
    /// test would pay for it. The site verifies by the count in the hash, so sign-in runs the same code.
    /// </summary>
    private static readonly Lazy<string> s_passwordHash = new(() =>
        new PasswordHasher<UserRecord>(Options.Create(new PasswordHasherOptions { IterationCount = 1000 })).HashPassword(null!, Password));

    public string ConnectionString { get; }

    /// <summary>Where this site stores uploaded files (D-108): inside its own temp folder.</summary>
    public string FilesPath => Path.Combine(_directory, "files");

    /// <summary>Built frontend to serve; by default a missing folder, so tests run API-only.</summary>
    public string FrontendPath { get; }

    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(7_654_321));

    public Dictionary<string, Guid> Users { get; } = [];

    public Dictionary<string, Guid> Players { get; } = [];

    /// <summary>Accounts, pool and (unless <paramref name="withSeason"/> is false) the season. Call once.</summary>
    public async Task SeedAsync(bool withSeason = true)
    {
        await using (var db = NewDb())
        {
            foreach (var (login, role) in new[] { ("vasya", Role.Player), ("petya", Role.Player), ("masha", Role.Player), ("zritel", Role.Spectator), ("admin", Role.Admin) })
            {
                var user = new UserRecord
                {
                    Id = Guid.NewGuid(),
                    Login = login,
                    NormalizedLogin = login,
                    Name = login,
                    PasswordHash = s_passwordHash.Value,
                    SecurityStamp = Guid.NewGuid().ToString("N"),
                    Role = role,
                    CreatedAt = Clock.UtcNow,
                };
                db.Users.Add(user);
                Users[login] = user.Id;
            }

            db.Categories.Add(new CategoryRecord { Name = "Horror", Weight = 1 });
            foreach (var (title, hours) in new[] { ("Silent Hill", 12m), ("Alan Wake", 15m), ("Outlast", 5m) })
            {
                db.Games.Add(new GameRecord { Id = Guid.NewGuid(), Title = title, TagsJson = PoolReader.TagsToJson(["Horror"]), Hours = hours });
            }

            await db.SaveChangesAsync();
        }

        if (!withSeason)
        {
            return;
        }

        var bus = Services.GetRequiredService<CommandBus>();
        await SendAsync(bus, new CreateSeason(SeasonId, "Тестовый сезон", RulesetJson.Default()));
        await SendAsync(bus, new ChangeSeasonStatus(SeasonStatus.Active));
        foreach (var login in new[] { "vasya", "petya" })
        {
            Players[login] = Guid.NewGuid();
            await SendAsync(bus, new AddSeasonPlayer(Players[login], Users[login], login));
        }
    }

    /// <summary>Sends an admin command to the seeded season through the queue; throws when it is rejected.</summary>
    public Task SendAsync(ICommand command) => SendAsync(Services.GetRequiredService<CommandBus>(), command);

    /// <summary>Another season, created now by the test clock, with nobody in it.</summary>
    public async Task<Guid> CreateSeasonAsync()
    {
        var id = Guid.NewGuid();
        var outcome = await Services.GetRequiredService<CommandBus>().SendAsync(new CommandEnvelope(Guid.NewGuid(), id, new CreateSeason(id, "Тестовый сезон", RulesetJson.Default()), AuthorId: null));
        return outcome.IsAccepted ? id : throw new InvalidOperationException($"Season not created: {outcome.Rejection}");
    }

    public GameEventDbContext NewDb()
    {
        var builder = new DbContextOptionsBuilder<GameEventDbContext>();
        SqliteDatabase.Configure(builder, ConnectionString);
        return new GameEventDbContext(builder.Options);
    }

    /// <summary>A browser-like client: keeps cookies, sends the antiforgery header.</summary>
    public async Task<HttpClient> SignedInAsync(string login)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await RefreshCsrfAsync(client);
        var response = await client.PostAsJsonAsync("/api/auth/login", new { login, password = Password });
        response.EnsureSuccessStatusCode();
        await RefreshCsrfAsync(client);
        return client;
    }

    public async Task<HttpClient> AnonymousAsync()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await RefreshCsrfAsync(client);
        return client;
    }

    public static async Task RefreshCsrfAsync(HttpClient client)
    {
        var token = await client.GetFromJsonAsync<AntiforgeryToken>("/api/auth/antiforgery");
        client.DefaultRequestHeaders.Remove(token!.HeaderName);
        client.DefaultRequestHeaders.Add(token.HeaderName, token.Token);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("ConnectionStrings:Main", ConnectionString);
        builder.UseSetting("Frontend:DistPath", FrontendPath);

        // The deadline scheduler's own loop would race the tests that move the clock; they call TickAsync themselves.
        builder.UseSetting("Scheduler:Enabled", "false");

        builder.UseSetting("Files:Path", FilesPath);

        // Tests never reach the outside: the pool's services are replaced by the tests that need them (D-118)
        builder.UseSetting("Metadata:SteamEnabled", "false");
        builder.UseSetting("Metadata:IgdbClientId", "");
        builder.UseSetting("Metadata:HltbSearchUrl", "");

        // Tests log to the console only; one observability test turns the file on in its own folder
        builder.UseSetting("Logging:File:Enabled", "false");
        builder.UseSetting("Security:LoginAttemptsPerMinute", _loginAttemptsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
            services.RemoveAll<IRandomSource>();
            services.AddSingleton<IRandomSource>(new SeededRandom(11));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Locked on Windows; the OS cleans the temp folder.
        }
    }

    private static async Task SendAsync(CommandBus bus, ICommand command)
    {
        var outcome = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), SeasonId, command, AuthorId: null));
        if (!outcome.IsAccepted)
        {
            throw new InvalidOperationException($"Seed {command} rejected: {outcome.Rejection}");
        }
    }
}

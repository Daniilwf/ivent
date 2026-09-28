using System.Text.Json;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Backups;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Kernel;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Infrastructure.Site;
using GameEvent.Tools.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

// Project tools (package.json): season export, import and the integrity check (C12b, D-105); the pool from xlsx (F1, D-125).
//   season-export <season id> <archive.zip> [--db <path>]
//   season-import <archive.zip> [--db <path>] [--allow-role-mismatch] [--with-pool]
//   season-check <season id> | --all [--db <path>]   — --all: every season (the migration check, J5)
//   db-census [--db <path>]   — rows per table, the seasons, the last migration and the schema (the migration check, J5)
//   pool-import <games.xlsx> [--db <path>] [--report-only]   — with the site stopped: the import runs its own queue
//   backup-verify [<archive> | <folder>] [--sha256 <hex>]   — the newest backup of var/backups (or GAMEEVENT_BACKUPS) by default: unpacked
//     into a temporary folder and checked — manifest and hashes, SQLite integrity, the census, every season (J3, D-211)
// The database defaults to the development one (var/dev.db) or GAMEEVENT_DB. Never point an import at production.
var arguments = args.ToList();
var allowRoleMismatch = arguments.Remove("--allow-role-mismatch");
var withPool = arguments.Remove("--with-pool");
var reportOnly = arguments.Remove("--report-only");
var dbIndex = arguments.IndexOf("--db");
if (dbIndex >= 0 && dbIndex + 1 >= arguments.Count)
{
    Console.Error.WriteLine("--db needs a path.");
    return 2;
}

var dbPath = Path.GetFullPath(dbIndex >= 0 ? arguments[dbIndex + 1] : Environment.GetEnvironmentVariable("GAMEEVENT_DB") ?? Path.Combine("var", "dev.db"));
if (dbIndex >= 0)
{
    arguments.RemoveRange(dbIndex, 2);
}

var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    switch (arguments.FirstOrDefault())
    {
        case "season-export" when arguments.Count == 3 && Guid.TryParse(arguments[1], out var seasonId):
            {
                await using var db = Open(connectionString);
                var archive = await SeasonTransfer.ExportAsync(db, seasonId, DateTimeOffset.UtcNow, cancel.Token);
                if (archive is null)
                {
                    Console.Error.WriteLine($"Season {seasonId} is not in {dbPath}.");
                    return 1;
                }

                await using (var file = File.Create(arguments[2]))
                {
                    await SeasonTransfer.WriteZipAsync(archive, file, cancel.Token);
                }

                Console.WriteLine($"Season «{archive.Name}»: {archive.Events.Count} events → {arguments[2]}. The archive holds logins and the log: keep it private.");
                return 0;
            }

        case "season-import" when arguments.Count == 2:
            {
                SeasonArchive archive;
                await using (var file = File.OpenRead(arguments[1]))
                {
                    archive = await SeasonTransfer.ReadZipAsync(file, cancel.Token);
                }

                using var siteLock = LockSite(dbPath);

                // A new database gets its tables; an existing one is imported into as it is.
                if (!File.Exists(dbPath))
                {
                    await SqliteDatabase.MigrateAsync(connectionString);
                }

                await using var db = Open(connectionString);
                var result = await SeasonTransfer.ImportAsync(db, archive, DateTimeOffset.UtcNow, new ImportOptions(allowRoleMismatch, withPool), cancel.Token);
                Console.WriteLine($"Season «{archive.Name}» ({result.SeasonId}): {result.Events} events, integrity ok.");
                foreach (var user in result.MatchedUsers)
                {
                    Console.WriteLine($"Matched account {user.Login}: {user.LocalRole} here, {user.ArchiveRole} in the archive.");
                }

                if (result.CreatedUsers.Count > 0)
                {
                    Console.WriteLine($"Placeholder accounts (deleted spectators, no usable password): {string.Join(", ", result.CreatedUsers)}");
                }

                return 0;
            }

        case "season-check" when arguments.Count == 2 && arguments[1] == "--all":
            {
                await using var db = Open(connectionString);
                var seasons = await db.Seasons.AsNoTracking().OrderBy(x => x.CreatedAt).Select(x => x.Id).ToListAsync(cancel.Token);
                if (seasons.Count == 0)
                {
                    Console.Error.WriteLine($"There are no seasons in {dbPath}.");
                    return 1;
                }

                var worst = 0;
                foreach (var id in seasons)
                {
                    worst = Math.Max(worst, await CheckSeasonAsync(db, id, dbPath, cancel.Token));
                }

                return worst;
            }

        case "db-census" when arguments.Count == 1:
            {
                // Raw SQLite, not the model: the census must read an older schema (a rolled-back copy) as it is
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync(cancel.Token);
                Console.WriteLine(JsonSerializer.Serialize(await DatabaseCensus.TakeAsync(connection, cancel.Token), DatabaseCensus.Json));
                return 0;
            }

        case "season-check" when arguments.Count == 2 && Guid.TryParse(arguments[1], out var seasonId):
            {
                await using var db = Open(connectionString);
                return await CheckSeasonAsync(db, seasonId, dbPath, cancel.Token);
            }

        case "pool-import" when arguments.Count == 2:
            {
                ImportedTable table;
                await using (var file = File.OpenRead(arguments[1]))
                {
                    table = XlsxPoolReader.Read(file);
                }

                // A report is about a database that is there: a mistyped path would report "everything is new"
                if (!File.Exists(dbPath))
                {
                    if (reportOnly)
                    {
                        Console.Error.WriteLine($"There is no database at {dbPath}.");
                        return 1;
                    }

                    await SqliteDatabase.MigrateAsync(connectionString);
                }

                Console.WriteLine($"Database: {dbPath}");
                PoolImportPlan plan;
                await using (var db = Open(connectionString))
                {
                    plan = await PoolImport.PlanAsync(db, table, cancel.Token);
                }

                Console.WriteLine(PoolImport.Report(plan));
                if (reportOnly)
                {
                    Console.WriteLine("Report only: nothing was written.");
                    return 0;
                }

                // The same queue as the site's, in this process: the site must be stopped meanwhile (one consumer, D-125, D-127)
                using var siteLock = LockSite(dbPath);
                var options = new DbContextOptionsBuilder<GameEventDbContext>();
                SqliteDatabase.Configure(options, connectionString);
                var bus = new CommandBus();
                using var processor = new CommandProcessor(
                    bus,
                    new PooledDbContextFactory<GameEventDbContext>(options.Options),
                    new SystemClock(),
                    new CryptoRandomSource(),
                    new GuidV7Ids(),
                    new NoPasswords(),
                    [],
                    NullLogger<CommandProcessor>.Instance);
                await processor.StartAsync(cancel.Token);
                try
                {
                    var result = await PoolImport.ApplyAsync(plan, bus, cancel.Token);
                    Console.WriteLine($"Added {result.GamesAdded} games, set {result.CategoriesSet} categories.");
                    foreach (var refused in result.Refused)
                    {
                        Console.WriteLine("Refused: " + refused);
                    }

                    return result.Refused.Count == 0 ? 0 : 3;
                }
                finally
                {
                    await processor.StopAsync(CancellationToken.None);
                }
            }

        case "backup-verify":
            {
                string? sha256 = null;
                if (arguments.IndexOf("--sha256") is var at and >= 0)
                {
                    if (at + 1 >= arguments.Count)
                    {
                        Console.Error.WriteLine("--sha256 needs the archive's SHA-256.");
                        return 2;
                    }

                    sha256 = arguments[at + 1];
                    arguments.RemoveRange(at, 2);
                }

                if (arguments.Count > 2)
                {
                    goto default;
                }

                var target = Path.GetFullPath(arguments.Count == 2 ? arguments[1] : Environment.GetEnvironmentVariable("GAMEEVENT_BACKUPS") ?? Path.Combine("var", "backups"));
                var archive = Directory.Exists(target) ? SiteBackup.Latest(target) : File.Exists(target) ? target : null;
                if (archive is null)
                {
                    Console.Error.WriteLine($"No backup at {target}.");
                    return 1;
                }

                return await VerifyBackupAsync(archive, sha256, cancel.Token);
            }

        case "pool-demo" when arguments.Count == 3:
            {
                // content/pool.demo.json for the demo season and CI (D-30, D-126): the table's games without its authors
                ImportedTable table;
                await using (var file = File.OpenRead(arguments[1]))
                {
                    table = XlsxPoolReader.Read(file);
                }

                var demo = PoolImport.DemoPool(table);
                await File.WriteAllTextAsync(arguments[2], JsonSerializer.Serialize(demo, PoolImport.DemoJson) + "\n", cancel.Token);
                Console.WriteLine($"{demo.Games.Count} games, {demo.Categories.Count} categories → {arguments[2]}; {demo.NotesDropped} notes left out.");
                return 0;
            }

        default:
            Console.Error.WriteLine(
                "Usage: season-export <season id> <archive.zip> | season-import <archive.zip> [--allow-role-mismatch] [--with-pool] | season-check <season id> | season-check --all | db-census | pool-import <games.xlsx> [--report-only]; each takes [--db <path>] | backup-verify [<archive> | <folder>] [--sha256 <hex>]");
            return 2;
    }
}
catch (Exception e) when (e is InvalidDataException or InvalidOperationException or JsonException or IOException or DbUpdateException or SqliteException
    or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Stopped: whatever was written before stays, a new run goes on from there.");
    return 130;
}

// The integrity check of one season: replays its log and compares with the projection (C12b); exit 0, 1 or 3
static async Task<int> CheckSeasonAsync(GameEventDbContext db, Guid seasonId, string dbPath, CancellationToken ct)
{
    var report = await SeasonIntegrity.CheckAsync(db, seasonId, ct);
    if (report is null)
    {
        Console.Error.WriteLine($"Season {seasonId} is not in {dbPath}.");
        return 1;
    }

    Console.WriteLine(report.IsIntact
        ? $"Season {seasonId}: intact up to event {report.LastSequence}."
        : $"Season {seasonId}: {report.Differences.Count} difference(s) up to event {report.LastSequence}{(report.Settled ? "" : " (the log kept moving: check again)")}:");
    foreach (var difference in report.Differences)
    {
        Console.WriteLine("  " + difference);
    }

    return report.IsIntact ? 0 : 3;
}

// The restore check of a backup (J3, D-211): the archive's own check — manifest and hashes, SQLite integrity, the
// schema, every season replayed (the same check as season-check --all) — plus the census of the unpacked database, as
// db-census reads it. Exit 0 sound, 3 not sound (anything that went wrong, the reason printed); 1 is «no archive».
static async Task<int> VerifyBackupAsync(string archive, string? sha256, CancellationToken ct)
{
    var work = Path.Combine(Path.GetTempPath(), "game-event-verify-" + Path.GetRandomFileName());
    var sound = false;
    try
    {
        var report = await BackupVerifier.VerifyAsync(archive, work, sha256, ct);
        Console.WriteLine($"Backup {archive}" + (report.Manifest is { } m ? $", made {m.CreatedAt:u}, {m.Entries.Count} entries" : "") + (report.Sha256 is { } h ? $", SHA-256 {h}" : ""));
        if (report.UnpackedDatabase is { } unpacked && report.CheckedDatabase is not null)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = unpacked, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync(ct);
            var census = await DatabaseCensus.TakeAsync(connection, ct);
            Console.WriteLine($"Census: {census.Rows.Count} tables, {census.Rows.Values.Sum()} rows, {census.Seasons.Count} season(s), migration {census.LastMigration ?? "none"}.");
            foreach (var (table, count) in census.Rows.Where(r => r.Value > 0))
            {
                Console.WriteLine($"  {table}: {count}");
            }
        }

        foreach (var note in report.Notes)
        {
            Console.WriteLine("  " + note);
        }

        foreach (var problem in report.Problems)
        {
            Console.WriteLine("  PROBLEM " + problem);
        }

        sound = report.IsSound;
        Console.WriteLine(sound ? "The backup is sound: it restores." : $"The backup is NOT sound. The unpacked copy stays in {work} for a look.");
        return sound ? 0 : 3;
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        if (sound && Directory.Exists(work))
        {
            Directory.Delete(work, recursive: true);
        }
    }
}

// The site holds this lock while it runs on the database (D-127): a writing command refuses meanwhile
static SiteLock LockSite(string dbPath) =>
    SiteLock.TryAcquire(Path.Combine(Path.GetDirectoryName(dbPath)!, "site.lock"))
    ?? throw new InvalidOperationException("The site is running on this database: stop it first, the import has its own queue.");

static GameEventDbContext Open(string connectionString)
{
    var options = new DbContextOptionsBuilder<GameEventDbContext>();
    SqliteDatabase.Configure(options, connectionString);
    return new GameEventDbContext(options.Options);
}

/// <summary>The pool import sends no account commands: passwords are the site's.</summary>
internal sealed class NoPasswords : IPasswords
{
    public string Hash(UserRecord user, string password) => throw new NotSupportedException("The import tool does not handle passwords.");

    public bool Verify(UserRecord? user, string password) => throw new NotSupportedException("The import tool does not handle passwords.");
}

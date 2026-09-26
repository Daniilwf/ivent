using System.Text.Json;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Kernel;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Tools.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

// Project tools (package.json): season export, import and the integrity check (C12b, D-105); the pool from xlsx (F1, D-125).
//   season-export <season id> <archive.zip> [--db <path>]
//   season-import <archive.zip> [--db <path>] [--allow-role-mismatch] [--with-pool]
//   season-check <season id> [--db <path>]
//   pool-import <games.xlsx> [--db <path>] [--report-only]   — with the site stopped: the import runs its own queue
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

        case "season-check" when arguments.Count == 2 && Guid.TryParse(arguments[1], out var seasonId):
            {
                await using var db = Open(connectionString);
                var report = await SeasonIntegrity.CheckAsync(db, seasonId, cancel.Token);
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

                // The same queue as the site's, in this process: the site must be stopped meanwhile (one consumer, D-125)
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
                "Usage: season-export <season id> <archive.zip> | season-import <archive.zip> [--allow-role-mismatch] [--with-pool] | season-check <season id> | pool-import <games.xlsx> [--report-only]; each takes [--db <path>]");
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

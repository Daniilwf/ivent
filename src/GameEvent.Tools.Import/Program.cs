using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;

// Project tools (package.json): season export and import (C12b, D-105); the xlsx import comes with task F1.
//   season-export <season id> <archive.zip> [--db <path>]
//   season-import <archive.zip> [--db <path>]
// The database defaults to the development one (var/dev.db) or GAMEEVENT_DB.
var arguments = args.ToList();
var dbIndex = arguments.IndexOf("--db");
var dbPath = dbIndex >= 0 && dbIndex + 1 < arguments.Count ? arguments[dbIndex + 1] : Environment.GetEnvironmentVariable("GAMEEVENT_DB")
    ?? Path.Combine("var", "dev.db");
if (dbIndex >= 0)
{
    arguments.RemoveRange(dbIndex, Math.Min(2, arguments.Count - dbIndex));
}

var connectionString = $"Data Source={Path.GetFullPath(dbPath)}";
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

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

            Console.WriteLine($"Season «{archive.Name}»: {archive.Events.Count} events → {arguments[2]}");
            return 0;
        }

    case "season-import" when arguments.Count == 2:
        {
            await SqliteDatabase.MigrateAsync(connectionString);
            await using var db = Open(connectionString);
            SeasonArchive archive;
            await using (var file = File.OpenRead(arguments[1]))
            {
                archive = await SeasonTransfer.ReadZipAsync(file, cancel.Token);
            }

            try
            {
                var result = await SeasonTransfer.ImportAsync(db, archive, DateTimeOffset.UtcNow, cancel.Token);
                Console.WriteLine($"Season «{archive.Name}» ({result.SeasonId}): {result.Events} events, integrity ok.");
                if (result.CreatedUsers.Count > 0)
                {
                    Console.WriteLine($"Placeholder accounts (deleted, no password): {string.Join(", ", result.CreatedUsers)}");
                }

                return 0;
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
        }

    case "xlsx":
        Console.Error.WriteLine("xlsx import is implemented in task F1.");
        return 1;

    default:
        Console.Error.WriteLine("Usage: season-export <season id> <archive.zip> [--db <path>] | season-import <archive.zip> [--db <path>]");
        return 2;
}

static GameEventDbContext Open(string connectionString)
{
    var options = new DbContextOptionsBuilder<GameEventDbContext>();
    SqliteDatabase.Configure(options, connectionString);
    return new GameEventDbContext(options.Options);
}

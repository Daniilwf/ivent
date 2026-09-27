using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GameEvent.Infrastructure.Database;

/// <summary>
/// SQLite in WAL mode: one writer (the command queue), readers in parallel.
/// Migrations run as a separate step before the app starts (D-27), never on startup.
/// </summary>
public static class SqliteDatabase
{
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options
            .UseSqlite(connectionString)
            .AddInterceptors(new ConnectionPragmas());
    }

    /// <summary>Applies migrations and switches the file to WAL (persistent per file).</summary>
    public static Task MigrateAsync(string connectionString, CancellationToken ct = default) =>
        MigrateAsync(connectionString, targetMigration: null, ct);

    /// <summary>
    /// Applies migrations up to <paramref name="targetMigration"/> (all when null) and switches the file to WAL. A target
    /// gives the exact schema an older version made: the backup check compares a restored database with it (D-214).
    /// </summary>
    public static async Task MigrateAsync(string connectionString, string? targetMigration, CancellationToken ct = default)
    {
        var options = Configure(new DbContextOptionsBuilder<GameEventDbContext>(), connectionString).Options;
        await using var db = new GameEventDbContext((DbContextOptions<GameEventDbContext>)options);
        await db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(targetMigration, ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }

    /// <summary>Per-connection settings: SQLite does not persist them.</summary>
    private sealed class ConnectionPragmas : DbConnectionInterceptor
    {
        private const string Pragmas = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
            Run(connection);

        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Run(connection);
            return Task.CompletedTask;
        }

        private static void Run(DbConnection connection)
        {
            if (connection is not SqliteConnection)
            {
                return;
            }

            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // constant SQL
            command.CommandText = Pragmas;
#pragma warning restore CA2100
            command.ExecuteNonQuery();
        }
    }
}

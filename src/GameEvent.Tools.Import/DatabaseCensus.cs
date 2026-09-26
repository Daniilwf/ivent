using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GameEvent.Tools.Import;

/// <summary>
/// What a database holds, for the migration check (J5, D-200): rows per table, the seasons, the last applied migration
/// and the schema. A migration must not lose rows or seasons; a rollback must bring the schema back exactly.
/// </summary>
internal sealed record DatabaseCensus(
    IReadOnlyDictionary<string, long> Rows,
    IReadOnlyList<string> Seasons,
    string? LastMigration,
    IReadOnlyList<string> Schema)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<DatabaseCensus> TakeAsync(SqliteConnection connection, CancellationToken ct)
    {
        var tables = new List<string>();
        var schema = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var (type, name) = (reader.GetString(0), reader.GetString(1));
                schema.Add($"{type} {name}: {(reader.IsDBNull(2) ? "" : reader.GetString(2))}");
                if (type == "table")
                {
                    tables.Add(name);
                }
            }
        }

        var rows = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            rows[table] = await ScalarAsync<long>(connection, $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"", ct);
        }

        var seasons = tables.Contains("Season")
            ? await ListAsync(connection, "SELECT Id FROM Season ORDER BY Id", ct)
            : [];
        var last = tables.Contains("__EFMigrationsHistory")
            ? (await ListAsync(connection, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1", ct)).FirstOrDefault()
            : null;
        return new DatabaseCensus(rows, seasons, last, schema);
    }

    private static async Task<T> ScalarAsync<T>(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync(ct))!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<string>> ListAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var values = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            values.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        }

        return values;
    }
}

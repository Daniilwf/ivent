using System.IO.Compression;
using System.Text.Json;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Seasons;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Backups;

/// <summary>What a verify found. <see cref="CheckedDatabase"/> is the unpacked copy the checks ran on (migrated to this version).</summary>
public sealed record BackupReport(
    string Archive,
    string? Sha256,
    BackupManifest? Manifest,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Notes,
    string? UnpackedDatabase,
    string? CheckedDatabase)
{
    public bool IsSound => Problems.Count == 0;
}

/// <summary>
/// The restore check (SPEC «Проверка восстановления», J3, D-211, D-214): a backup is unpacked into a private work folder
/// and must prove it restores — the archive is the one whose SHA-256 was recorded outside the backups folder (when
/// given); every entry is the one the manifest lists, byte for byte, never unpacked past its size; the database passes
/// SQLite's own integrity and foreign key checks; its schema is exactly what its last migration makes (no foreign
/// tables, triggers or views); it is not from a newer version; every season replays from its log into exactly the stored
/// state; every uploaded file the database knows is in the archive. Anything that goes wrong is a problem, never a crash.
/// </summary>
public static class BackupVerifier
{
    private const int MaxLines = 20;

    public static async Task<BackupReport> VerifyAsync(string archive, string workFolder, string? expectedSha256 = null, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        string? sha256 = null;
        BackupManifest? manifest = null;
        var unpacked = Path.Combine(workFolder, SiteBackup.DatabaseEntry);
        var checkedCopy = Path.Combine(workFolder, "checked.db");
        var checkedDone = false;
        try
        {
            PrivateFiles.CreateDirectory(workFolder);
            sha256 = await SiteBackup.HashFileAsync(archive, ct);
            if (expectedSha256 is not null && !string.Equals(sha256, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"The archive's SHA-256 is {sha256}, the recorded one is {expectedSha256.Trim()}: it was changed after the backup.");
                return Report();
            }

            ZipArchive zip;
            try
            {
                zip = ZipFile.OpenRead(archive);
            }
            catch (InvalidDataException e)
            {
                problems.Add($"Not a readable backup archive: {e.Message}");
                return Report();
            }

            using (zip)
            {
                manifest = await ReadManifestAsync(zip, problems, ct);
                if (manifest is null)
                {
                    return Report();
                }

                await CheckEntriesAsync(zip, manifest, unpacked, problems, ct);
            }

            if (!File.Exists(unpacked) || problems.Count > 0)
            {
                return Report();
            }

            await using (var copy = PrivateFiles.Create(checkedCopy))
            await using (var source = File.OpenRead(unpacked))
            {
                await source.CopyToAsync(copy, ct);
            }

            checkedDone = true;
            await CheckDatabaseAsync(checkedCopy, workFolder, manifest, problems, notes, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException e)
        {
            problems.Add($"The database cannot be read: {e.Message}");
        }
        catch (Exception e)
        {
            // An unreadable zip, an old event the engine cannot read, a migration that fails on the copy: all say «no»
            problems.Add($"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        return Report();

        BackupReport Report() => new(archive, sha256, manifest, problems, notes,
            File.Exists(unpacked) ? unpacked : null, checkedDone ? checkedCopy : null);
    }

    private static async Task<BackupManifest?> ReadManifestAsync(ZipArchive zip, List<string> problems, CancellationToken ct)
    {
        if (zip.GetEntry(SiteBackup.ManifestEntry) is not { } entry)
        {
            problems.Add($"No {SiteBackup.ManifestEntry}: not a backup of this site, or cut short.");
            return null;
        }

        await using var stream = await entry.OpenAsync(ct);
        var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, SiteBackup.Json, ct);
        if (manifest?.Entries is null)
        {
            problems.Add($"{SiteBackup.ManifestEntry} is empty.");
            return null;
        }

        if (manifest.Format != BackupManifest.CurrentFormat)
        {
            problems.Add($"Backup format {manifest.Format}: this version reads format {BackupManifest.CurrentFormat}.");
            return null;
        }

        return manifest;
    }

    /// <summary>Entries and manifest agree name for name, size for size and hash for hash; the database lands in <paramref name="unpacked"/>.</summary>
    private static async Task CheckEntriesAsync(ZipArchive zip, BackupManifest manifest, string unpacked, List<string> problems, CancellationToken ct)
    {
        var listed = new Dictionary<string, BackupEntry>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            if (!IsSafeName(entry.Path))
            {
                problems.Add($"{entry.Path}: not a name a backup writes.");
            }
            else if (!listed.TryAdd(entry.Path, entry))
            {
                problems.Add($"{entry.Path}: listed twice.");
            }
        }

        if (!listed.ContainsKey(SiteBackup.DatabaseEntry))
        {
            problems.Add($"The manifest lists no {SiteBackup.DatabaseEntry}.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName == SiteBackup.ManifestEntry)
            {
                continue;
            }

            if (!seen.Add(entry.FullName))
            {
                problems.Add($"{entry.FullName}: twice in the archive.");
                continue;
            }

            if (!listed.TryGetValue(entry.FullName, out var expected))
            {
                problems.Add($"{entry.FullName}: in the archive, not in the manifest.");
                continue;
            }

            if (entry.Length != expected.Bytes)
            {
                problems.Add($"{entry.FullName}: {entry.Length} bytes, the manifest says {expected.Bytes}.");
                continue;
            }

            if (await CheckEntryAsync(entry, expected, entry.FullName == SiteBackup.DatabaseEntry ? unpacked : null, ct) is { } problem)
            {
                problems.Add(problem);
            }
        }

        foreach (var missing in listed.Keys.Where(name => !seen.Contains(name)))
        {
            problems.Add($"{missing}: in the manifest, missing from the archive.");
        }
    }

    /// <summary>
    /// Unpacks one entry to <paramref name="target"/> (or only hashes it), never past the manifest's size; null when it is
    /// the entry the manifest describes. The restore uses it again while extracting: an archive changed after its check is caught.
    /// </summary>
    internal static async Task<string?> CheckEntryAsync(ZipArchiveEntry entry, BackupEntry expected, string? target, CancellationToken ct)
    {
        try
        {
            await using var input = await entry.OpenAsync(ct);
            await using var output = target is null ? null : PrivateFiles.Create(target);
            var actual = await SiteBackup.CopyAsync(input, output, expected.Bytes, ct);
            return actual.Bytes == expected.Bytes && string.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"{entry.FullName}: damaged ({actual.Bytes} bytes, SHA-256 {actual.Sha256}; the manifest says {expected.Bytes}, {expected.Sha256}).";
        }
        catch (InvalidDataException e)
        {
            return $"{entry.FullName}: cannot be unpacked: {e.Message}";
        }
    }

    private static async Task CheckDatabaseAsync(string database, string workFolder, BackupManifest manifest, List<string> problems, List<string> notes, CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(ct);
            var integrity = await ListAsync(connection, "PRAGMA integrity_check", ct);
            if (integrity is not ["ok"])
            {
                problems.AddRange(integrity.Take(MaxLines).Select(line => "SQLite integrity: " + line));
                return;
            }

            var keys = await ListAsync(connection, "PRAGMA foreign_key_check", ct);
            problems.AddRange(keys.Take(MaxLines).Select(line => "Foreign key broken in table " + line));
        }

        var options = (DbContextOptions<GameEventDbContext>)SqliteDatabase.Configure(new DbContextOptionsBuilder<GameEventDbContext>(), connectionString).Options;
        int pending;
        string last;
        await using (var db = new GameEventDbContext(options))
        {
            var known = db.Database.GetMigrations().ToList();
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
            if (applied.Count == 0)
            {
                problems.Add("The database has no migration history: not a database of this site.");
                return;
            }

            if (applied.FirstOrDefault(id => !known.Contains(id)) is { } unknown)
            {
                problems.Add($"Made by a newer version (migration {unknown}): verify and restore it with that version.");
                return;
            }

            last = applied[^1];
            pending = known.Count(id => !applied.Contains(id));
        }

        // The schema must be the one its migrations make: a table, index, trigger or view of anyone else's is refused
        var reference = Path.Combine(workFolder, "reference.db");
        PrivateFiles.Create(reference).Dispose();
        await SqliteDatabase.MigrateAsync(new SqliteConnectionStringBuilder { DataSource = reference, Pooling = false }.ToString(), last, ct);
        var expected = await SchemaAsync(reference, ct);
        var actual = await SchemaAsync(database, ct);
        problems.AddRange(actual.Except(expected).Take(MaxLines).Select(line => "Not in the schema of " + last + ": " + line));
        problems.AddRange(expected.Except(actual).Take(MaxLines).Select(line => "Missing from the schema of " + last + ": " + line));
        if (problems.Count > 0)
        {
            return;
        }

        notes.Add($"Last migration: {manifest.LastMigration ?? last}.");
        if (pending > 0)
        {
            // What a restore does next: the migrate step of this version. The checks run on the migrated copy
            notes.Add($"{pending} migration(s) of this version not applied yet: checked after applying them to the copy.");
            await SqliteDatabase.MigrateAsync(connectionString, ct);
        }

        await using var check = new GameEventDbContext(options);
        var seasons = await check.Seasons.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.Id).ToListAsync(ct);
        foreach (var id in seasons)
        {
            var report = await SeasonIntegrity.CheckAsync(check, id, ct);
            if (report is { IsIntact: true })
            {
                notes.Add($"Season {id}: intact up to event {report.LastSequence}.");
            }
            else if (report is not null)
            {
                problems.Add($"Season {id}: {report.Differences.Count} difference(s) between the log and the stored state.");
                problems.AddRange(report.Differences.Take(MaxLines).Select(d => "  " + d));
            }
        }

        if (seasons.Count == 0)
        {
            notes.Add("No seasons yet.");
        }

        var inArchive = manifest.Entries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        var files = await check.Files.AsNoTracking().Select(f => new { f.Id, f.MediaType }).ToListAsync(ct);
        var missing = files
            .SelectMany(f => new[] { FileNames.Main(f.Id, f.MediaType), FileNames.Thumbnail(f.Id, f.MediaType) })
            .Where(name => !inArchive.Contains(SiteBackup.FilesFolder + name))
            .ToList();
        problems.AddRange(missing.Take(MaxLines).Select(name => $"Uploaded file {name}: in the database, not in the archive."));
        if (missing.Count > MaxLines)
        {
            problems.Add($"…and {missing.Count - MaxLines} more missing file(s).");
        }

        notes.Add($"{files.Count} uploaded file(s), {manifest.Entries.Count(e => e.Path.StartsWith(SiteBackup.KeysFolder, StringComparison.Ordinal))} key file(s).");
    }

    /// <summary>Every table, index, trigger and view with its SQL, SQLite's own objects left out.</summary>
    private static async Task<List<string>> SchemaAsync(string database, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(ct);
        return await ListAsync(connection, "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name", ct);
    }

    /// <summary>A name a backup writes: the database, or a relative path under files/ or keys/ that stays there.</summary>
    internal static bool IsSafeName(string name) =>
        name == SiteBackup.DatabaseEntry
        || ((name.StartsWith(SiteBackup.FilesFolder, StringComparison.Ordinal) || name.StartsWith(SiteBackup.KeysFolder, StringComparison.Ordinal))
            && !name.Contains('\\', StringComparison.Ordinal)
            && !name.Contains(':', StringComparison.Ordinal)
            && name.Split('/').Skip(1).All(part => part.Length > 0 && part != "." && part != ".."));

    private static async Task<List<string>> ListAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var lines = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            lines.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        }

        return lines;
    }
}

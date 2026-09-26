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
    BackupManifest? Manifest,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Notes,
    string? UnpackedDatabase,
    string? CheckedDatabase)
{
    public bool IsSound => Problems.Count == 0;
}

/// <summary>
/// The restore check (SPEC «Проверка восстановления», J3, D-211): a backup is unpacked into a temporary folder and must
/// prove it restores — every entry is the one the manifest lists, byte for byte; the database passes SQLite's own
/// integrity and foreign key checks; it is not from a newer version; every season replays from its log into exactly the
/// stored state; every uploaded file the database knows is in the archive. Only the database is unpacked: files are
/// checked by their hashes as they stream by, so a small temporary disk is enough.
/// </summary>
public static class BackupVerifier
{
    private const int MaxDifferencesPerSeason = 20;

    public static async Task<BackupReport> VerifyAsync(string archive, string workFolder, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        Directory.CreateDirectory(workFolder);
        var unpacked = Path.Combine(workFolder, SiteBackup.DatabaseEntry);
        var checkedCopy = Path.Combine(workFolder, "checked.db");

        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(archive);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            problems.Add($"Not a readable backup archive: {e.Message}");
            return new BackupReport(archive, null, problems, notes, null, null);
        }

        BackupManifest? manifest;
        using (zip)
        {
            manifest = await ReadManifestAsync(zip, problems, ct);
            if (manifest is null)
            {
                return new BackupReport(archive, null, problems, notes, null, null);
            }

            await CheckEntriesAsync(zip, manifest, unpacked, problems, ct);
        }

        if (!File.Exists(unpacked) || problems.Count > 0)
        {
            return new BackupReport(archive, manifest, problems, notes, File.Exists(unpacked) ? unpacked : null, null);
        }

        File.Copy(unpacked, checkedCopy, overwrite: true);
        try
        {
            await CheckDatabaseAsync(checkedCopy, manifest, problems, notes, ct);
        }
        catch (SqliteException e)
        {
            problems.Add($"The database cannot be read: {e.Message}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        return new BackupReport(archive, manifest, problems, notes, unpacked, checkedCopy);
    }

    private static async Task<BackupManifest?> ReadManifestAsync(ZipArchive zip, List<string> problems, CancellationToken ct)
    {
        if (zip.GetEntry(SiteBackup.ManifestEntry) is not { } entry)
        {
            problems.Add($"No {SiteBackup.ManifestEntry}: not a backup of this site, or cut short.");
            return null;
        }

        try
        {
            await using var stream = await entry.OpenAsync(ct);
            var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, SiteBackup.Json, ct);
            if (manifest is null || manifest.Entries is null)
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
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            problems.Add($"{SiteBackup.ManifestEntry} cannot be read: {e.Message}");
            return null;
        }
    }

    /// <summary>Entries and manifest agree name for name and hash for hash; the database lands in <paramref name="unpacked"/>.</summary>
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

            try
            {
                await using var input = await entry.OpenAsync(ct);
                (long Bytes, string Sha256) actual;
                if (entry.FullName == SiteBackup.DatabaseEntry)
                {
                    await using var output = new FileStream(unpacked, FileMode.Create, FileAccess.Write, FileShare.None);
                    SiteBackup.OwnerOnly(unpacked);
                    actual = await SiteBackup.CopyAsync(input, output, ct);
                }
                else
                {
                    actual = await SiteBackup.CopyAsync(input, null, ct);
                }

                if (actual.Bytes != expected.Bytes || !string.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{entry.FullName}: damaged ({actual.Bytes} bytes, SHA-256 {actual.Sha256}; the manifest says {expected.Bytes}, {expected.Sha256}).");
                }
            }
            catch (InvalidDataException e)
            {
                problems.Add($"{entry.FullName}: cannot be unpacked: {e.Message}");
            }
        }

        foreach (var missing in listed.Keys.Where(name => !seen.Contains(name)))
        {
            problems.Add($"{missing}: in the manifest, missing from the archive.");
        }
    }

    private static async Task CheckDatabaseAsync(string database, BackupManifest manifest, List<string> problems, List<string> notes, CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(ct);
            var integrity = await ListAsync(connection, "PRAGMA integrity_check", ct);
            if (integrity is not ["ok"])
            {
                problems.AddRange(integrity.Take(MaxDifferencesPerSeason).Select(line => "SQLite integrity: " + line));
                return;
            }

            var keys = await ListAsync(connection, "PRAGMA foreign_key_check", ct);
            problems.AddRange(keys.Take(MaxDifferencesPerSeason).Select(line => "Foreign key broken in table " + line));
        }

        var options = SqliteDatabase.Configure(new DbContextOptionsBuilder<GameEventDbContext>(), connectionString).Options;
        int pending;
        await using (var db = new GameEventDbContext((DbContextOptions<GameEventDbContext>)options))
        {
            var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
            var unknown = applied.Where(id => !known.Contains(id)).ToList();
            if (unknown.Count > 0)
            {
                problems.Add($"Made by a newer version (migration {unknown[^1]}): verify and restore it with that version.");
                return;
            }

            pending = known.Count(id => !applied.Contains(id));
            notes.Add($"Last migration: {manifest.LastMigration ?? "none"}.");
            if (pending > 0)
            {
                // What a restore does next: the migrate step of this version. The checks run on the migrated copy
                notes.Add($"{pending} migration(s) of this version not applied yet: checked after applying them to the copy.");
            }
        }

        if (pending > 0)
        {
            await SqliteDatabase.MigrateAsync(connectionString, ct);
        }

        await using var check = new GameEventDbContext((DbContextOptions<GameEventDbContext>)options);
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
                problems.AddRange(report.Differences.Take(MaxDifferencesPerSeason).Select(d => "  " + d));
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
        problems.AddRange(missing.Take(MaxDifferencesPerSeason).Select(name => $"Uploaded file {name}: in the database, not in the archive."));
        if (missing.Count > MaxDifferencesPerSeason)
        {
            problems.Add($"…and {missing.Count - MaxDifferencesPerSeason} more missing file(s).");
        }

        notes.Add($"{files.Count} uploaded file(s), {manifest.Entries.Count(e => e.Path.StartsWith(SiteBackup.KeysFolder, StringComparison.Ordinal))} key file(s).");
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
            lines.Add(string.Join(", ", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        }

        return lines;
    }
}

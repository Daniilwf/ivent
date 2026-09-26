using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GameEvent.Infrastructure.Backups;

/// <summary>What a backup takes: the database file, the uploaded files and the keys that sign sessions (J3, D-210).</summary>
public sealed record BackupSources(string DatabasePath, string FilesPath, string? KeysPath);

/// <summary>One entry of the archive with its size and SHA-256, so a verify finds any damaged byte.</summary>
public sealed record BackupEntry(string Path, long Bytes, string Sha256);

/// <summary>The archive's table of contents (<c>manifest.json</c>, written last).</summary>
public sealed record BackupManifest(int Format, DateTimeOffset CreatedAt, string? LastMigration, IReadOnlyList<BackupEntry> Entries)
{
    public const int CurrentFormat = 1;
}

public sealed record BackupResult(string ArchivePath, BackupManifest Manifest, IReadOnlyList<string> Removed);

/// <summary>
/// The site's backup (SPEC «Бэкапы», J3, D-210): one timestamped zip in a backups folder with the database, the uploaded
/// files and the session keys. The database is copied by SQLite's online backup API from a read transaction — a
/// consistent snapshot while the site keeps writing (WAL readers never block the writer), never a file copy of a WAL
/// database. Files are taken after the snapshot: a file is written before its record (D-108) and never deleted, so every
/// file the snapshot knows is there. The archive appears under its final name only when complete.
/// </summary>
public static class SiteBackup
{
    public const string DatabaseEntry = "game-event.db";
    public const string ManifestEntry = "manifest.json";
    public const string FilesFolder = "files/";
    public const string KeysFolder = "keys/";
    public const string Prefix = "backup-";
    public const string Extension = ".zip";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<BackupResult> CreateAsync(BackupSources sources, string folder, int keep, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        if (!File.Exists(sources.DatabasePath))
        {
            throw new InvalidOperationException($"There is no database at {sources.DatabasePath}.");
        }

        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var name = Prefix + stamp;
        for (var n = 2; File.Exists(Path.Combine(folder, name + Extension)); n++)
        {
            name = $"{Prefix}{stamp}-{n}";
        }

        var partial = Path.Combine(folder, "." + name + Extension + ".partial");
        var snapshot = Path.Combine(folder, "." + name + ".db");
        try
        {
            await SnapshotAsync(sources.DatabasePath, snapshot, ct);
            var lastMigration = await LastMigrationAsync(snapshot, ct);
            var entries = new List<BackupEntry>();
            var manifest = new BackupManifest(BackupManifest.CurrentFormat, now, lastMigration, entries);
            await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                OwnerOnly(partial);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
                entries.Add(await AddAsync(zip, DatabaseEntry, snapshot, CompressionLevel.Optimal, ct));

                // Pictures are WebP and GIF: already compressed
                foreach (var (path, entry) in Tree(sources.FilesPath, FilesFolder))
                {
                    if (await TryAddAsync(zip, entry, path, CompressionLevel.NoCompression, ct) is { } added)
                    {
                        entries.Add(added);
                    }
                }

                foreach (var (path, entry) in sources.KeysPath is null ? [] : Tree(sources.KeysPath, KeysFolder))
                {
                    if (await TryAddAsync(zip, entry, path, CompressionLevel.Optimal, ct) is { } added)
                    {
                        entries.Add(added);
                    }
                }

                var manifestEntry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                await using (var output = await manifestEntry.OpenAsync(ct))
                {
                    await JsonSerializer.SerializeAsync(output, manifest, Json, ct);
                }
            }

            var archive = Path.Combine(folder, name + Extension);
            File.Move(partial, archive);
            var removed = Prune(folder, keep);
            return new BackupResult(archive, manifest, removed);
        }
        finally
        {
            File.Delete(partial);
            File.Delete(snapshot);
            File.Delete(snapshot + "-journal");
        }
    }

    /// <summary>The newest archives stay, older ones are deleted; returns the deleted names.</summary>
    public static IReadOnlyList<string> Prune(string folder, int keep)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        var removed = new List<string>();
        foreach (var archive in Archives(folder).Skip(keep))
        {
            File.Delete(archive);
            removed.Add(Path.GetFileName(archive));
        }

        return removed;
    }

    /// <summary>The finished archives of a folder, newest first (the names sort by time).</summary>
    public static IReadOnlyList<string> Archives(string folder) =>
        Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, Prefix + "*" + Extension).OrderByDescending(Path.GetFileName, StringComparer.Ordinal)]
            : [];

    /// <summary>The newest finished archive of a folder, or null.</summary>
    public static string? Latest(string folder) => Archives(folder) is [var newest, ..] ? newest : null;

    /// <summary>A consistent copy of a live database in one file (rollback journal, no -wal beside it).</summary>
    private static async Task SnapshotAsync(string database, string target, CancellationToken ct)
    {
        // ReadWrite, not ReadWriteCreate: a wrong path must fail, not back up a new empty database
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());
        await using var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
        await source.OpenAsync(ct);
        await copy.OpenAsync(ct);
        await ExecuteAsync(source, "PRAGMA busy_timeout=30000;", ct);

        // One step of the backup API reads every page inside one read transaction: a snapshot at one moment
        source.BackupDatabase(copy);
        OwnerOnly(target);

        // The copy carries the source's WAL flag: back to a rollback journal, so the file is the whole database
        await ExecuteAsync(copy, "PRAGMA journal_mode=DELETE;", ct);
    }

    internal static async Task<string?> LastMigrationAsync(string database, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'";
        if (await command.ExecuteScalarAsync(ct) is null)
        {
            return null;
        }

        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1";
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Every file of a folder with its entry name; half-written uploads (<c>*.part-*</c>) are left out.</summary>
    private static IEnumerable<(string Path, string Entry)> Tree(string root, string folder)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Contains(".part-", StringComparison.Ordinal))
            .Select(path => (path, folder + Path.GetRelativePath(root, path).Replace('\\', '/')))
            .OrderBy(x => x.Item2, StringComparer.Ordinal);
    }

    /// <summary>A file that went away meanwhile is skipped: it was not part of the snapshot's world anyway.</summary>
    private static async Task<BackupEntry?> TryAddAsync(ZipArchive zip, string entry, string path, CompressionLevel level, CancellationToken ct)
    {
        try
        {
            return await AddAsync(zip, entry, path, level, ct);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static async Task<BackupEntry> AddAsync(ZipArchive zip, string entry, string path, CompressionLevel level, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        var zipEntry = zip.CreateEntry(entry, level);
        await using var output = await zipEntry.OpenAsync(ct);
        var (bytes, sha256) = await CopyAsync(input, output, ct);
        return new BackupEntry(entry, bytes, sha256);
    }

    /// <summary>Copies a stream and returns its length and SHA-256 (lower-case hex).</summary>
    internal static async Task<(long Bytes, string Sha256)> CopyAsync(Stream input, Stream? output, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            if (output is not null)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            total += read;
        }

        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>The archive holds the session keys and every account's password hash: only its owner reads it.</summary>
    internal static void OwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

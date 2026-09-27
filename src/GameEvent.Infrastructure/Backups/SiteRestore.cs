using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace GameEvent.Infrastructure.Backups;

/// <summary>A restore: the verify's report, and where the data it replaced was put (null when refused).</summary>
public sealed record RestoreResult(BackupReport Report, string? ReplacedDataFolder);

/// <summary>
/// A restore that failed half-way and was undone: the site's own data is back in place, the pieces of the backup that
/// were already written are in <see cref="FailedFolder"/> for a look.
/// </summary>
public sealed class RestoreFailedException(string message, string failedFolder, Exception inner) : IOException(message, inner)
{
    public string FailedFolder { get; } = failedFolder;
}

/// <summary>
/// Restores a backup over the site's data (J3, D-212, D-214). Only with the site stopped — the caller holds the site's
/// lock. The archive is verified first (with its recorded SHA-256 when given) and nothing is touched when it fails. The
/// current database, files and keys are not deleted but moved aside into <c>before-restore-&lt;time&gt;</c> next to the
/// database; if anything fails after that, the written pieces go to <c>failed-restore-&lt;time&gt;</c> and the old data
/// comes back. Every entry is hashed again while it is extracted. Keys only when asked (<c>withKeys</c>) and
/// when the archive has them. The maintenance flag is not part of the data: it stays as it was. The migrate step of the
/// deploy brings an older backup up to the running version afterwards.
/// </summary>
public static class SiteRestore
{
    private static readonly string[] s_sidecars = ["-wal", "-shm", "-journal"];

    public static async Task<RestoreResult> RestoreAsync(
        string archive, BackupSources target, bool withKeys, DateTimeOffset now, string? expectedSha256 = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var dataFolder = Path.GetDirectoryName(Path.GetFullPath(target.DatabasePath))!;
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        // On the data's own disk: the database may not fit a small temporary one, and the move in is a rename
        var work = Path.Combine(dataFolder, ".restore-" + stamp);
        try
        {
            var report = await BackupVerifier.VerifyAsync(archive, work, expectedSha256, ct);
            if (!report.IsSound)
            {
                return new RestoreResult(report, null);
            }

            var keys = withKeys && target.KeysPath is not null
                && report.Manifest!.Entries.Any(e => e.Path.StartsWith(SiteBackup.KeysFolder, StringComparison.Ordinal))
                ? target.KeysPath
                : null;
            var pieces = new List<(string Current, string Name)> { (target.DatabasePath, Path.GetFileName(target.DatabasePath)) };
            pieces.AddRange(s_sidecars.Select(s => (target.DatabasePath + s, Path.GetFileName(target.DatabasePath) + s)));
            pieces.Add((target.FilesPath, "files"));
            if (keys is not null)
            {
                pieces.Add((keys, "keys"));
            }

            var aside = Path.Combine(dataFolder, "before-restore-" + stamp);
            PrivateFiles.CreateDirectory(aside);
            var moved = new List<(string Current, string Name)>();
            foreach (var piece in pieces)
            {
                if (Move(piece.Current, Path.Combine(aside, piece.Name)))
                {
                    moved.Add(piece);
                }
            }

            try
            {
                await InstallAsync(archive, report, target, keys, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var failed = Path.Combine(dataFolder, "failed-restore-" + stamp);
                PrivateFiles.CreateDirectory(failed);
                foreach (var piece in pieces)
                {
                    Move(piece.Current, Path.Combine(failed, piece.Name));
                }

                foreach (var piece in moved)
                {
                    Move(Path.Combine(aside, piece.Name), piece.Current);
                }

                Directory.Delete(aside);
                throw new RestoreFailedException(
                    $"The restore failed and was undone: {e.Message} The site's data is back in place; what the backup had written is in {failed}.", failed, e);
            }

            return new RestoreResult(report, aside);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }

    private static async Task InstallAsync(string archive, BackupReport report, BackupSources target, string? keys, CancellationToken ct)
    {
        File.Move(report.UnpackedDatabase!, target.DatabasePath);
        Directory.CreateDirectory(target.FilesPath);
        if (keys is not null)
        {
            PrivateFiles.CreateDirectory(keys);
        }

        var listed = report.Manifest!.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        using (var zip = ZipFile.OpenRead(archive))
        {
            foreach (var entry in zip.Entries)
            {
                var destination = entry.FullName switch
                {
                    var name when name.StartsWith(SiteBackup.FilesFolder, StringComparison.Ordinal) =>
                        Path.Combine(target.FilesPath, name[SiteBackup.FilesFolder.Length..]),
                    var name when keys is not null && name.StartsWith(SiteBackup.KeysFolder, StringComparison.Ordinal) =>
                        Path.Combine(keys, name[SiteBackup.KeysFolder.Length..]),
                    _ => null,
                };
                if (destination is null)
                {
                    continue;
                }

                // The archive was checked a moment ago; checked again as it is written, so a swap in between is caught
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!listed.TryGetValue(entry.FullName, out var expected)
                    || await BackupVerifier.CheckEntryAsync(entry, expected, destination, ct) is not null)
                {
                    throw new InvalidDataException($"{entry.FullName} changed after the check.");
                }
            }
        }

        // The site's database runs in WAL mode (the backup's copy has a rollback journal)
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static bool Move(string from, string to)
    {
        if (File.Exists(from))
        {
            File.Move(from, to);
            return true;
        }

        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
            return true;
        }

        return false;
    }
}

using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace GameEvent.Infrastructure.Backups;

/// <summary>A restore: the verify's report, and where the data it replaced was put (null when refused).</summary>
public sealed record RestoreResult(BackupReport Report, string? ReplacedDataFolder);

/// <summary>
/// Restores a backup over the site's data (J3, D-212). Only with the site stopped — the caller holds the site's lock.
/// The archive is verified first and nothing is touched when it fails. The current database, files and keys are not
/// deleted but moved aside into <c>before-restore-&lt;time&gt;</c> next to the database, so a wrong restore can be
/// undone by hand. The migrate step of the deploy brings an older backup up to the running version afterwards.
/// </summary>
public static class SiteRestore
{
    public static async Task<RestoreResult> RestoreAsync(string archive, BackupSources target, bool withKeys, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var dataFolder = Path.GetDirectoryName(Path.GetFullPath(target.DatabasePath))!;
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        // On the data's own disk: the database may not fit a small temporary one, and the move in is a rename
        var work = Path.Combine(dataFolder, ".restore-" + stamp);
        try
        {
            var report = await BackupVerifier.VerifyAsync(archive, work, ct);
            if (!report.IsSound)
            {
                return new RestoreResult(report, null);
            }

            var aside = Path.Combine(dataFolder, "before-restore-" + stamp);
            Directory.CreateDirectory(aside);
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                MoveIfThere(target.DatabasePath + suffix, Path.Combine(aside, Path.GetFileName(target.DatabasePath) + suffix));
            }

            MoveIfThere(target.FilesPath, Path.Combine(aside, "files"));
            if (withKeys && target.KeysPath is not null)
            {
                MoveIfThere(target.KeysPath, Path.Combine(aside, "keys"));
            }

            File.Move(report.UnpackedDatabase!, target.DatabasePath);
            Directory.CreateDirectory(target.FilesPath);
            using (var zip = ZipFile.OpenRead(archive))
            {
                foreach (var entry in zip.Entries)
                {
                    var destination = entry.FullName switch
                    {
                        var name when name.StartsWith(SiteBackup.FilesFolder, StringComparison.Ordinal) =>
                            Path.Combine(target.FilesPath, name[SiteBackup.FilesFolder.Length..]),
                        var name when withKeys && target.KeysPath is not null && name.StartsWith(SiteBackup.KeysFolder, StringComparison.Ordinal) =>
                            Path.Combine(target.KeysPath, name[SiteBackup.KeysFolder.Length..]),
                        _ => null,
                    };
                    if (destination is null)
                    {
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await entry.ExtractToFileAsync(destination, overwrite: false, ct);
                }
            }

            // The site's database runs in WAL mode (the backup's copy has a rollback journal)
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL;";
                await command.ExecuteNonQueryAsync(ct);
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

    private static void MoveIfThere(string from, string to)
    {
        if (File.Exists(from))
        {
            File.Move(from, to);
        }
        else if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
    }
}

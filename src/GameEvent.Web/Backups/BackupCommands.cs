using GameEvent.Infrastructure.Backups;
using GameEvent.Infrastructure.Site;
using GameEvent.Web.Hosting;
using Microsoft.Data.Sqlite;

namespace GameEvent.Web.Backups;

/// <summary>Where the site's data and its backups are, from the site's own configuration (J3, D-210).</summary>
public sealed record BackupSettings(BackupSources Sources, string Folder, int Keep, string? LockFile, string EnvironmentName = "Production")
{
    public const int DefaultKeep = 14;

    public static BackupSettings From(IConfiguration configuration, string contentRoot, string environmentName = "Production")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = AppSetup.ResolveDataSource(configuration.ConnectionString(), contentRoot);
        var database = new SqliteConnectionStringBuilder(connectionString).DataSource;

        // The same defaults as the site's (SitePaths; DataProtection:KeysPath in AppSetup)
        var files = Path.GetFullPath(Path.Combine(contentRoot, configuration["Files:Path"] ?? SitePaths.DefaultFiles));
        var keys = configuration["DataProtection:KeysPath"] is { Length: > 0 } k ? Path.GetFullPath(Path.Combine(contentRoot, k)) : null;
        var folder = Path.GetFullPath(Path.Combine(contentRoot, configuration["Backup:Path"] ?? SitePaths.DefaultBackups));
        var keep = configuration.GetValue("Backup:Keep", DefaultKeep);
        return new BackupSettings(new BackupSources(database, files, keys), folder, keep, SitePaths.LockFile(configuration, contentRoot), environmentName);
    }
}

/// <summary>
/// The one-off backup commands of the image (J3, D-210…D-214), run next to the site with its configuration:
/// <c>backup</c> — while the site runs; <c>backup-verify [archive or folder] [--sha256 hex]</c> — the newest by default;
/// <c>restore &lt;archive or -&gt; [--sha256 hex] [--with-keys]</c> — only with the site stopped; <c>-</c> reads the
/// archive from standard input (a copy kept outside the server, whatever its owner and mode). Exit codes: 0 done,
/// 1 failed or refused (the reason is printed), 2 wrong arguments, 3 the backup is not sound.
/// </summary>
public static class BackupCommands
{
    public const string Usage =
        "Usage: backup | backup-verify [<archive> | <folder>] [--sha256 <hex>] | restore <archive> | - [--sha256 <hex>] [--with-keys]";

    /// <summary>Restore folders of the site's data older than this are named after every backup, to be deleted.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    public static async Task<int> RunAsync(
        string command, IReadOnlyList<string> arguments, BackupSettings settings, DateTimeOffset now, TextWriter output, Stream? input = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        var rest = arguments.ToList();
        var withKeys = rest.Remove("--with-keys");
        string? sha256 = null;
        if (rest.IndexOf("--sha256") is var at and >= 0)
        {
            if (at + 1 >= rest.Count || rest[at + 1].Length != 64 || !rest[at + 1].All(char.IsAsciiHexDigit))
            {
                await output.WriteLineAsync("--sha256 needs the archive's SHA-256: 64 hex digits.");
                return 2;
            }

            sha256 = rest[at + 1];
            rest.RemoveRange(at, 2);
        }

        if (rest.Any(a => a.StartsWith("--", StringComparison.Ordinal)))
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }

        switch (command)
        {
            case "backup" when rest.Count == 0 && sha256 is null && !withKeys:
                return await BackupAsync(settings, now, output, ct);
            case "backup-verify" when rest.Count <= 1 && !withKeys:
                return await VerifyAsync(rest.FirstOrDefault(), sha256, settings, output, ct);
            case "restore" when rest.Count == 1:
                return await RestoreAsync(rest[0], sha256, withKeys, settings, now, output, input, ct);
            default:
                await output.WriteLineAsync(Usage);
                return 2;
        }
    }

    public static async Task<int> BackupAsync(BackupSettings settings, DateTimeOffset now, TextWriter output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(output);
        if (settings.Keep < 1)
        {
            await output.WriteLineAsync($"Backup:Keep is {settings.Keep}: at least 1 backup must stay.");
            return 2;
        }

        try
        {
            var result = await SiteBackup.CreateAsync(settings.Sources, settings.Folder, settings.Keep, now, ct);
            var bytes = new FileInfo(result.ArchivePath).Length;
            await output.WriteLineAsync($"Backup: {result.ArchivePath} ({bytes / 1024} KB, {result.Manifest.Entries.Count} entries, migration {result.Manifest.LastMigration ?? "none"}).");

            // The line deploy/backup.sh records outside the backups folder, in `sha256sum -c` form (D-214)
            await output.WriteLineAsync($"SHA-256: {result.Sha256}  {Path.GetFileName(result.ArchivePath)}");
            foreach (var removed in result.Removed)
            {
                await output.WriteLineAsync($"Removed {removed} (Backup:Keep = {settings.Keep}).");
            }

            foreach (var stale in SiteBackup.StaleRestoreFolders(Path.GetDirectoryName(settings.Sources.DatabasePath)!, now, StaleAfter))
            {
                await output.WriteLineAsync($"WARNING {stale} is older than {StaleAfter.TotalDays:0} days: a restore left it; look and delete it.");
            }

            await output.WriteLineAsync("The archive holds password hashes and session keys: keep it private.");
            return 0;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or SqliteException)
        {
            await output.WriteLineAsync("Backup failed: " + e.Message);
            return 1;
        }
    }

    /// <summary>The archive a path names: the file itself, or the newest backup of a folder (the backups folder by default).</summary>
    public static string? ArchiveOf(string? path, BackupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var target = path ?? settings.Folder;
        return Directory.Exists(target) ? SiteBackup.Latest(target) : File.Exists(target) ? Path.GetFullPath(target) : null;
    }

    public static async Task<int> VerifyAsync(string? path, string? sha256, BackupSettings settings, TextWriter output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (ArchiveOf(path, settings) is not { } archive)
        {
            await output.WriteLineAsync($"No backup at {path ?? settings.Folder}.");
            return 1;
        }

        // Next to the archive, not in the container's small /tmp; a read-only folder falls back to the temporary one
        var work = WorkFolder(Path.GetDirectoryName(archive)!);
        try
        {
            var report = await BackupVerifier.VerifyAsync(archive, work, sha256, ct);
            await WriteReportAsync(report, output);
            return report.IsSound ? 0 : 3;
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

    public static async Task<int> RestoreAsync(
        string archive, string? sha256, bool withKeys, BackupSettings settings, DateTimeOffset now, TextWriter output, Stream? input = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(output);

        // Session keys sign every sign-in: only the live site takes them back, and only when asked (D-214)
        if (withKeys && settings.EnvironmentName != "Production")
        {
            await output.WriteLineAsync($"--with-keys is for the live site only (this is {settings.EnvironmentName}): its keys never sign sessions anywhere else.");
            return 1;
        }

        if (archive != "-" && !File.Exists(archive))
        {
            await output.WriteLineAsync($"No backup at {archive}.");
            return 1;
        }

        // The site writes the database while it runs: a restore under it would be overwritten or corrupted
        SiteLock? siteLock = null;
        if (settings.LockFile is not null && (siteLock = SiteLock.TryAcquire(settings.LockFile)) is null)
        {
            await output.WriteLineAsync("The site is running: stop it first (docker compose stop site).");
            return 1;
        }

        string? upload = null;
        try
        {
            if (archive == "-")
            {
                if (input is null)
                {
                    await output.WriteLineAsync("No standard input to read the archive from.");
                    return 1;
                }

                // Spooled next to the data, private: a copy kept outside the server comes in whoever owns it
                upload = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.Sources.DatabasePath))!, $".restore-upload-{Guid.CreateVersion7():N}.zip");
                await using (var file = PrivateFiles.Create(upload))
                {
                    await input.CopyToAsync(file, ct);
                }

                archive = upload;
            }

            if (sha256 is null)
            {
                await output.WriteLineAsync("WARNING no --sha256: the archive is checked against its own manifest only, not against the hash recorded when it was made.");
            }

            var result = await SiteRestore.RestoreAsync(archive, settings.Sources, withKeys, now, sha256, ct);
            await WriteReportAsync(result.Report, output);
            if (result.ReplacedDataFolder is null)
            {
                await output.WriteLineAsync("Nothing was restored: the backup did not pass its check.");
                return 3;
            }

            await output.WriteLineAsync($"Restored the backup with SHA-256 {result.Report.Sha256}. The data it replaced is in {result.ReplacedDataFolder}: delete it once the site is fine.");
            await output.WriteLineAsync(withKeys ? "Session keys restored." : "Session keys kept as they were (--with-keys restores them on the live site).");
            await output.WriteLineAsync("Next: the migrate step, then start the site.");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or SqliteException or InvalidOperationException)
        {
            await output.WriteLineAsync("Restore failed: " + e.Message);
            return 1;
        }
        finally
        {
            siteLock?.Dispose();
            if (upload is not null)
            {
                File.Delete(upload);
            }
        }
    }

    private static string WorkFolder(string near)
    {
        var name = ".verify-" + Guid.CreateVersion7().ToString("N");
        try
        {
            var work = Path.Combine(near, name);
            PrivateFiles.CreateDirectory(work);
            return work;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(Path.GetTempPath(), name);
        }
    }

    private static async Task WriteReportAsync(BackupReport report, TextWriter output)
    {
        await output.WriteLineAsync($"Backup {report.Archive}" + (report.Manifest is { } m ? $", made {m.CreatedAt:u}" : "") + (report.Sha256 is { } h ? $", SHA-256 {h}:" : ":"));
        foreach (var note in report.Notes)
        {
            await output.WriteLineAsync("  " + note);
        }

        foreach (var problem in report.Problems)
        {
            await output.WriteLineAsync("  PROBLEM " + problem);
        }

        await output.WriteLineAsync(report.IsSound ? "The backup is sound: it restores." : $"The backup is NOT sound: {report.Problems.Count} problem(s).");
    }
}

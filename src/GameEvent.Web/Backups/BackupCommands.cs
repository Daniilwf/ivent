using GameEvent.Infrastructure.Backups;
using GameEvent.Infrastructure.Site;
using GameEvent.Web.Hosting;

namespace GameEvent.Web.Backups;

/// <summary>Where the site's data and its backups are, from the site's own configuration (J3, D-210).</summary>
public sealed record BackupSettings(BackupSources Sources, string Folder, int Keep, string? LockFile)
{
    public const int DefaultKeep = 14;

    public static BackupSettings From(IConfiguration configuration, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = AppSetup.ResolveDataSource(configuration.ConnectionString(), contentRoot);
        var database = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;

        // The same defaults as the site's (Files:Path in FileEndpoints, DataProtection:KeysPath in AppSetup)
        var files = Path.GetFullPath(Path.Combine(contentRoot, configuration["Files:Path"] ?? Path.Combine("var", "files")));
        var keys = configuration["DataProtection:KeysPath"] is { Length: > 0 } k ? Path.GetFullPath(Path.Combine(contentRoot, k)) : null;
        var folder = Path.GetFullPath(Path.Combine(contentRoot, configuration["Backup:Path"] ?? Path.Combine("var", "backups")));
        var keep = configuration.GetValue("Backup:Keep", DefaultKeep);
        var lockFile = configuration["Site:LockFile"] is { Length: > 0 } l ? Path.Combine(contentRoot, l) : null;
        return new BackupSettings(new BackupSources(database, files, keys), folder, keep, lockFile);
    }
}

/// <summary>
/// The one-off backup commands of the image (J3, D-210…D-212), run next to the site with its configuration:
/// <c>backup</c> — while the site runs; <c>backup-verify [archive or folder]</c> — the newest archive by default;
/// <c>restore &lt;archive&gt; [--without-keys]</c> — only with the site stopped.
/// </summary>
public static class BackupCommands
{
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
            foreach (var removed in result.Removed)
            {
                await output.WriteLineAsync($"Removed the old backup {removed} (Backup:Keep = {settings.Keep}).");
            }

            await output.WriteLineAsync("The archive holds password hashes and session keys: keep it private.");
            return 0;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
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

    public static async Task<int> VerifyAsync(string? path, BackupSettings settings, TextWriter output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (ArchiveOf(path, settings) is not { } archive)
        {
            await output.WriteLineAsync($"No backup at {path ?? settings.Folder}.");
            return 1;
        }

        var work = Path.Combine(Path.GetTempPath(), "game-event-verify-" + Path.GetRandomFileName());
        try
        {
            var report = await BackupVerifier.VerifyAsync(archive, work, ct);
            await WriteReportAsync(report, output);
            return report.IsSound ? 0 : 3;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }

    public static async Task<int> RestoreAsync(string archive, bool withKeys, BackupSettings settings, DateTimeOffset now, TextWriter output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(output);
        if (!File.Exists(archive))
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

        using (siteLock)
        {
            var result = await SiteRestore.RestoreAsync(archive, settings.Sources, withKeys, now, ct);
            await WriteReportAsync(result.Report, output);
            if (result.ReplacedDataFolder is null)
            {
                await output.WriteLineAsync("Nothing was restored: the backup did not pass its check.");
                return 3;
            }

            await output.WriteLineAsync($"Restored {Path.GetFileName(archive)}. The data it replaced is in {result.ReplacedDataFolder}: delete it once the site is fine.");
            await output.WriteLineAsync("Next: the migrate step, then start the site.");
            return 0;
        }
    }

    private static async Task WriteReportAsync(BackupReport report, TextWriter output)
    {
        await output.WriteLineAsync($"Backup {report.Archive}" + (report.Manifest is { } m ? $", made {m.CreatedAt:u}:" : ":"));
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

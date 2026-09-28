using System.Text.Json;
using GameEvent.Infrastructure.Backups;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Site;
using GameEvent.Web.Backups;
using GameEvent.Web.Hosting;
using GameEvent.Web.Tests.Api;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GameEvent.Web.Tests.Backups;

/// <summary>
/// The rules the backup's reviews added (D-211, D-214): the archive against its recorded SHA-256; the schema exactly what
/// its migrations make; no archive from a newer version; an old archive checked after this version's migrations; sizes
/// before copying; the restore undone when it fails half-way; keys only on the live site and only when asked; restoring
/// from standard input; retention by the archive's name only; the command line that never starts a second site.
/// </summary>
public sealed class BackupRulesTests : IAsyncLifetime
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 1, 3, 0, 0, TimeSpan.Zero);

    private readonly SiteFactory _site = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "game-event-tests", "backup-rules-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Folder => Path.Combine(_directory, "backups");

    private string KeysPath => Path.Combine(_directory, "keys");

    private BackupSources Sources => new(new SqliteConnectionStringBuilder(_site.ConnectionString).DataSource, _site.FilesPath, KeysPath);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await _site.SeedAsync();
        Directory.CreateDirectory(KeysPath);
        await File.WriteAllTextAsync(Path.Combine(KeysPath, "key-1.xml"), "<key id=\"1\" />", Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _site.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Held by the OS on Windows: the temp folder is cleaned later.
        }
    }

    // ---- The recorded hash ----

    [Fact]
    public async Task The_backup_reports_the_archives_own_sha256_and_the_verify_checks_it()
    {
        var result = await BackupAsync();

        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(result.ArchivePath, Ct))), result.Sha256);
        Assert.True((await VerifyAsync(result.ArchivePath, result.Sha256)).IsSound);

        var other = new string('0', 64);
        var report = await VerifyAsync(result.ArchivePath, other);
        Assert.StartsWith($"The archive's SHA-256 is {result.Sha256}, the recorded one is {other}", Assert.Single(report.Problems), StringComparison.Ordinal);
    }

    // ---- Manifest and entries ----

    [Fact]
    public async Task A_manifest_of_another_format_fails_the_check()
    {
        var archive = (await BackupAsync()).ArchivePath;
        BackupTests.Rewrite(archive, (name, bytes) => name == SiteBackup.ManifestEntry ? ManifestWith(bytes, m => m with { Format = 2 }) : bytes, fixManifest: false);

        var report = await VerifyAsync(archive);

        Assert.Equal(["Backup format 2: this version reads format 1."], report.Problems);
    }

    [Fact]
    public async Task An_entry_listed_twice_in_the_manifest_fails_the_check()
    {
        var archive = (await BackupAsync()).ArchivePath;
        BackupTests.Rewrite(archive, (name, bytes) => name == SiteBackup.ManifestEntry ? ManifestWith(bytes, m => m with { Entries = [.. m.Entries, m.Entries[^1]] }) : bytes, fixManifest: false);

        var report = await VerifyAsync(archive);

        Assert.Contains("keys/key-1.xml: listed twice.", report.Problems);
    }

    [Theory]
    [InlineData("files/..\\evil.webp")]
    [InlineData("files/c:evil.webp")]
    [InlineData("files//evil.webp")]
    [InlineData("files/./evil.webp")]
    [InlineData("other/evil.webp")]
    public async Task A_name_a_backup_never_writes_fails_the_check(string name)
    {
        var archive = (await BackupAsync()).ArchivePath;
        BackupTests.Rewrite(archive, (_, bytes) => bytes, fixManifest: true, extra: (name, [1]));

        var report = await VerifyAsync(archive);

        Assert.Contains($"{name}: not a name a backup writes.", report.Problems);
    }

    [Fact]
    public async Task An_entry_longer_than_its_manifest_says_is_not_unpacked()
    {
        var archive = (await BackupAsync()).ArchivePath;
        BackupTests.Rewrite(archive, (name, bytes) => name == SiteBackup.DatabaseEntry ? [.. bytes, .. new byte[4096]] : bytes, fixManifest: false);

        var report = await VerifyAsync(archive);

        var problem = Assert.Single(report.Problems);
        Assert.StartsWith("game-event.db: ", problem, StringComparison.Ordinal);
        Assert.Contains("the manifest says", problem, StringComparison.Ordinal);
        Assert.Null(report.UnpackedDatabase);
    }

    // ---- The schema and the version ----

    [Fact]
    public async Task A_trigger_or_view_the_migrations_do_not_make_fails_the_check()
    {
        var archive = (await BackupAsync()).ArchivePath;
        BackupTests.Rewrite(archive, (name, bytes) => name == SiteBackup.DatabaseEntry
            ? Edited(bytes, "CREATE TRIGGER evil AFTER INSERT ON Season BEGIN DELETE FROM User; END; CREATE VIEW peek AS SELECT PasswordHash FROM User;")
            : bytes, fixManifest: true);

        var report = await VerifyAsync(archive);

        Assert.Contains(report.Problems, p => p.StartsWith("Not in the schema of ", StringComparison.Ordinal) && p.Contains("trigger | evil", StringComparison.Ordinal));
        Assert.Contains(report.Problems, p => p.StartsWith("Not in the schema of ", StringComparison.Ordinal) && p.Contains("view | peek", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_archive_from_a_newer_version_is_left_to_that_version()
    {
        var archive = (await BackupAsync()).ArchivePath;
        BackupTests.Rewrite(archive, (name, bytes) => name == SiteBackup.DatabaseEntry
            ? Edited(bytes, "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29990101000000_Future', '10.0.0');")
            : bytes, fixManifest: true);

        var report = await VerifyAsync(archive);

        Assert.Equal(["Made by a newer version (migration 29990101000000_Future): verify and restore it with that version."], report.Problems);
    }

    [Fact]
    public async Task An_archive_of_an_older_version_is_checked_after_this_versions_migrations()
    {
        // The usual restore after an update: a backup made before the update's migrations
        var old = Path.Combine(_directory, "old", "site.db");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        string first;
        await using (var db = new GameEventDbContext(new DbContextOptionsBuilder<GameEventDbContext>().UseSqlite($"Data Source={old}").Options))
        {
            var all = db.Database.GetMigrations().ToList();
            Assert.True(all.Count > 1, "This check needs a version with more than one migration.");
            first = all[0];
        }

        await SqliteDatabase.MigrateAsync($"Data Source={old};Pooling=False", first, Ct);
        var archive = (await SiteBackup.CreateAsync(Sources with { DatabasePath = old }, Folder, 5, s_now, Ct)).ArchivePath;

        var report = await VerifyAsync(archive);

        Assert.True(report.IsSound, string.Join("\n", report.Problems));
        Assert.Contains($"Last migration: {first}.", report.Notes);
        Assert.Contains(report.Notes, n => n.EndsWith("not applied yet: checked after applying them to the copy.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_event_the_engine_cannot_read_is_a_problem_not_a_crash()
    {
        var archive = (await BackupAsync()).ArchivePath;
        BackupTests.Rewrite(archive, (name, bytes) => name == SiteBackup.DatabaseEntry
            ? Edited(bytes, "UPDATE GameEvent SET Data = '{not json' WHERE Sequence = 1 AND SeasonId <> '00000000-0000-0000-0000-000000000000';")
            : bytes, fixManifest: true);

        var report = await VerifyAsync(archive);

        Assert.False(report.IsSound);
    }

    // ---- Retention ----

    [Fact]
    public async Task Retention_orders_the_same_moment_by_its_number_and_counts_only_archive_names()
    {
        Directory.CreateDirectory(Folder);
        foreach (var name in new[] { "backup-mine.zip", "backup-20261001T030000000Z.zip.bak", "backup-20261001T030000000Z-x.zip" })
        {
            await File.WriteAllTextAsync(Path.Combine(Folder, name), "not ours", Ct);
        }

        var first = await BackupAsync();
        var second = await BackupAsync();
        var third = await BackupAsync();

        Assert.EndsWith("-3.zip", third.ArchivePath, StringComparison.Ordinal);
        Assert.Equal([third.ArchivePath, second.ArchivePath, first.ArchivePath], SiteBackup.Archives(Folder));
        SiteBackup.Prune(Folder, 1, s_now);
        Assert.Equal([third.ArchivePath], SiteBackup.Archives(Folder));
        Assert.Equal(4, Directory.GetFiles(Folder).Length);
    }

    [Fact]
    public async Task Leftovers_of_a_killed_backup_are_removed_once_they_are_old()
    {
        Directory.CreateDirectory(Folder);
        var stale = Path.Combine(Folder, ".backup-20260101T000000000Z.zip.partial");
        var fresh = Path.Combine(Folder, ".backup-20261001T025959000Z.db");
        await File.WriteAllTextAsync(stale, "half", Ct);
        await File.WriteAllTextAsync(fresh, "half", Ct);
        File.SetLastWriteTimeUtc(stale, s_now.UtcDateTime.AddHours(-2));
        File.SetLastWriteTimeUtc(fresh, s_now.UtcDateTime.AddMinutes(-1));

        var result = await BackupAsync();

        Assert.Contains(Path.GetFileName(stale), result.Removed);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task The_backup_names_restore_folders_left_for_too_long()
    {
        var data = Path.GetDirectoryName(Sources.DatabasePath)!;
        var old = Directory.CreateDirectory(Path.Combine(data, "before-restore-20260901T000000Z")).FullName;
        Directory.SetLastWriteTimeUtc(old, s_now.UtcDateTime.AddDays(-8));
        Directory.CreateDirectory(Path.Combine(data, "before-restore-20260930T000000Z"));
        using var output = new StringWriter();

        Assert.Equal(0, await BackupCommands.BackupAsync(Settings(), s_now, output, Ct));

        Assert.Contains($"WARNING {old} is older than 7 days", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("20260930", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backup_that_fails_exits_1_with_the_reason()
    {
        using var output = new StringWriter();
        var settings = Settings() with { Sources = Sources with { DatabasePath = Path.Combine(_directory, "none.db") } };

        Assert.Equal(1, await BackupCommands.BackupAsync(settings, s_now, output, Ct));
        Assert.StartsWith("Backup failed: There is no database at", output.ToString(), StringComparison.Ordinal);
    }

    // ---- Restore ----

    [Fact]
    public async Task A_restore_that_fails_half_way_puts_the_old_data_back()
    {
        var archive = (await BackupAsync()).ArchivePath;
        var root = Path.Combine(_directory, "target");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "blocker"), "a file where the files folder must go", Ct);
        var target = new BackupSources(Path.Combine(root, "site.db"), Path.Combine(root, "blocker", "files"), Path.Combine(root, "keys"));
        await File.WriteAllTextAsync(target.DatabasePath, "the current database", Ct);

        var failure = await Assert.ThrowsAsync<RestoreFailedException>(() => SiteRestore.RestoreAsync(archive, target, false, s_now, null, Ct));

        Assert.Equal("the current database", await File.ReadAllTextAsync(target.DatabasePath, Ct));
        Assert.True(File.Exists(Path.Combine(failure.FailedFolder, "site.db")));
        Assert.Contains(failure.FailedFolder, failure.Message, StringComparison.Ordinal);
        Assert.Equal(["blocker", "failed-restore-20261001T030000Z", "site.db"], Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        using var output = new StringWriter();
        var settings = new BackupSettings(target, Folder, 5, LockFile: null);
        Assert.Equal(1, await BackupCommands.RestoreAsync(archive, null, false, settings, s_now.AddSeconds(1), output, null, Ct));
        Assert.Contains("Restore failed: The restore failed and was undone", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keys_come_back_only_when_asked_and_only_on_the_live_site()
    {
        var archive = (await BackupAsync()).ArchivePath;
        var target = NewTarget();
        Directory.CreateDirectory(target.KeysPath!);
        await File.WriteAllTextAsync(Path.Combine(target.KeysPath!, "own.xml"), "own", Ct);
        using var output = new StringWriter();

        var staging = new BackupSettings(target, Folder, 5, null, "Staging");
        Assert.Equal(1, await BackupCommands.RestoreAsync(archive, null, true, staging, s_now, output, null, Ct));
        Assert.False(File.Exists(target.DatabasePath));

        Assert.Equal(0, await BackupCommands.RestoreAsync(archive, null, false, staging, s_now, output, null, Ct));
        Assert.Equal(["own.xml"], Directory.EnumerateFiles(target.KeysPath!).Select(Path.GetFileName));

        var live = new BackupSettings(NewTarget(), Folder, 5, null, "Production");
        Assert.Equal(0, await BackupCommands.RestoreAsync(archive, null, true, live, s_now, output, null, Ct));
        Assert.Equal(["key-1.xml"], Directory.EnumerateFiles(live.Sources.KeysPath!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_restore_reads_the_archive_from_standard_input_and_checks_its_recorded_hash()
    {
        var result = await BackupAsync();
        var target = NewTarget();
        var settings = new BackupSettings(target, Folder, 5, null);
        using var output = new StringWriter();

        await using (var wrong = File.OpenRead(result.ArchivePath))
        {
            Assert.Equal(3, await BackupCommands.RunAsync("restore", ["-", "--sha256", new string('a', 64)], settings, s_now, output, wrong, Ct));
        }

        Assert.False(File.Exists(target.DatabasePath));

        await using (var right = File.OpenRead(result.ArchivePath))
        {
            Assert.Equal(0, await BackupCommands.RunAsync("restore", ["-", "--sha256", result.Sha256], settings, s_now.AddSeconds(1), output, right, Ct));
        }

        Assert.True(File.Exists(target.DatabasePath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(target.DatabasePath)!, ".restore-upload-*"));
    }

    [Fact]
    public async Task Without_a_lock_setting_the_restore_takes_the_lock_next_to_the_database()
    {
        var archive = (await BackupAsync()).ArchivePath;
        var target = NewTarget();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Main"] = $"Data Source={target.DatabasePath}",
            ["Files:Path"] = target.FilesPath,
            ["Backup:Path"] = Folder,
        }).Build();
        var settings = BackupSettings.From(configuration, _directory);
        using var output = new StringWriter();

        using (SiteLock.TryAcquire(Path.Combine(Path.GetDirectoryName(target.DatabasePath)!, "site.lock")))
        {
            Assert.Equal(1, await BackupCommands.RestoreAsync(archive, null, false, settings, s_now, output, null, Ct));
        }

        Assert.Contains("The site is running", output.ToString(), StringComparison.Ordinal);
    }

    // ---- The command line ----

    [Theory]
    [InlineData(null, true)]
    [InlineData("migrate", true)]
    [InlineData("restore", true)]
    [InlineData("--urls", true)]
    [InlineData("Logging:LogLevel:Default=Debug", true)]
    [InlineData("frobnicate", false)]
    [InlineData("backup-cat", false)]
    [InlineData("Backup", false)]
    public void Only_known_commands_and_options_run(string? first, bool known) =>
        Assert.Equal(known, Operations.IsKnownCommand(first));

    [Fact]
    public async Task An_unknown_command_is_refused_before_anything_starts()
    {
        var entry = typeof(Operations).Assembly.EntryPoint!;
        var result = entry.Invoke(null, [new[] { "frobnicate" }]);

        Assert.Equal(2, result is Task<int> task ? await task : (int)result!);
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("restore", "a.zip", "b.zip")]
    [InlineData("restore", "a.zip", "--without-keys")]
    [InlineData("restore", "a.zip", "--sha256", "nothex")]
    [InlineData("restore", "a.zip", "--sha256")]
    [InlineData("backup", "extra")]
    [InlineData("backup", "--with-keys")]
    [InlineData("backup-verify", "a.zip", "b.zip")]
    [InlineData("backup-verify", "--with-keys")]
    public async Task Wrong_arguments_exit_2_with_the_usage(string command, params string[] arguments)
    {
        using var output = new StringWriter();

        Assert.Equal(2, await BackupCommands.RunAsync(command, arguments, Settings(), s_now, output, null, Ct));
        Assert.NotEmpty(output.ToString());
    }

    // ---- Helpers ----

    private BackupSettings Settings() => new(Sources, Folder, 5, LockFile: null);

    private async Task<BackupResult> BackupAsync() => await SiteBackup.CreateAsync(Sources, Folder, keep: 10, s_now, Ct);

    private async Task<BackupReport> VerifyAsync(string archive, string? sha256 = null) =>
        await BackupVerifier.VerifyAsync(archive, Path.Combine(_directory, "verify-" + Guid.NewGuid().ToString("N")), sha256, Ct);

    private BackupSources NewTarget()
    {
        var root = Path.Combine(_directory, "restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new BackupSources(Path.Combine(root, "site.db"), Path.Combine(root, "files"), Path.Combine(root, "keys"));
    }

    private static byte[] ManifestWith(byte[] bytes, Func<BackupManifest, BackupManifest> change) =>
        JsonSerializer.SerializeToUtf8Bytes(change(JsonSerializer.Deserialize<BackupManifest>(bytes, JsonSerializerOptions.Web)!), JsonSerializerOptions.Web);

    /// <summary>The database with some SQL run on it behind the site's back.</summary>
    private byte[] Edited(byte[] bytes, string sql)
    {
        var file = Path.Combine(_directory, "edit-" + Guid.NewGuid().ToString("N") + ".db");
        File.WriteAllBytes(file, bytes);
        using (var connection = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        return File.ReadAllBytes(file);
    }
}

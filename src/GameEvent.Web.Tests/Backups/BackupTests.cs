using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Players;
using GameEvent.Infrastructure.Backups;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Site;
using GameEvent.Web.Backups;
using GameEvent.Web.Tests.Api;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GameEvent.Web.Tests.Backups;

/// <summary>
/// Backups (SPEC «Бэкапы», «Проверка восстановления», J3, D-210…D-212): one archive with the database, the uploaded files
/// and the session keys, consistent while the site writes; old archives pruned; the verify proves an archive restores and
/// fails loudly on any damage; the restore puts the data back and keeps what it replaced.
/// </summary>
public sealed class BackupTests : IAsyncLifetime
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 1, 3, 0, 0, TimeSpan.Zero);

    private readonly SiteFactory _site = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "game-event-tests", "backup-" + Guid.NewGuid().ToString("N"));

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

    // ---- Backup ----

    [Fact]
    public async Task A_backup_holds_the_database_the_files_and_the_keys_and_passes_its_check()
    {
        await PlayAsync();
        var fileId = await StoreFileAsync();
        await File.WriteAllTextAsync(Path.Combine(_site.FilesPath, "half.webp.part-abc"), "half", Ct);

        var result = await SiteBackup.CreateAsync(Sources, Folder, keep: 3, s_now, Ct);

        Assert.Equal("backup-20261001T030000000Z.zip", Path.GetFileName(result.ArchivePath));
        using (var zip = ZipFile.OpenRead(result.ArchivePath))
        {
            var names = zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(
                [
                    "files/" + FileNames.Thumbnail(fileId, FileNames.Webp),
                    "files/" + FileNames.Main(fileId, FileNames.Webp),
                    "game-event.db",
                    "keys/key-1.xml",
                    "manifest.json",
                ],
                names);
        }

        Assert.NotNull(result.Manifest.LastMigration);
        Assert.Equal([result.ArchivePath], SiteBackup.Archives(Folder));
        Assert.Empty(Directory.EnumerateFiles(Folder, ".*"));

        var report = await VerifyAsync(result.ArchivePath);
        Assert.True(report.IsSound, string.Join("\n", report.Problems));
        Assert.Contains(report.Notes, n => n.StartsWith($"Season {SiteFactory.SeasonId}: intact", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_backup_taken_while_the_queue_writes_is_a_consistent_snapshot()
    {
        using var stop = new CancellationTokenSource();
        var written = 0;
        var writer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await _site.SendAsync(new AdjustPlayer(_site.Players["vasya"], "Бонус", PointsDelta: 1));
                written++;
            }
        }, Ct);

        var archives = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            while (Volatile.Read(ref written) < (i + 1) * 5)
            {
                await Task.Delay(5, Ct);
            }

            archives.Add((await SiteBackup.CreateAsync(Sources, Folder, keep: 10, s_now.AddSeconds(i), Ct)).ArchivePath);
        }

        await stop.CancelAsync();
        await writer;

        // Every snapshot is one moment of the log: the replay of its events is exactly its stored state
        var points = new List<int>();
        foreach (var archive in archives)
        {
            var report = await VerifyAsync(archive);
            Assert.True(report.IsSound, string.Join("\n", report.Problems));
            await using var db = Open(report.CheckedDatabase!);
            points.Add(await db.SeasonPlayers.Where(p => p.Id == _site.Players["vasya"]).Select(p => p.Points).SingleAsync(Ct));
        }

        Assert.True(points[0] >= 5 && points[2] > points[0], string.Join(", ", points));
    }

    [Fact]
    public async Task Only_the_newest_backups_are_kept()
    {
        Directory.CreateDirectory(Folder);
        var stray = Path.Combine(Folder, "notes.txt");
        await File.WriteAllTextAsync(stray, "mine", Ct);

        var results = new List<BackupResult>();
        for (var i = 0; i < 5; i++)
        {
            results.Add(await SiteBackup.CreateAsync(Sources, Folder, keep: 3, s_now.AddHours(i), Ct));
        }

        Assert.Equal(results.Skip(2).Select(r => r.ArchivePath).Reverse(), SiteBackup.Archives(Folder));
        Assert.Equal([Path.GetFileName(results[1].ArchivePath)], results[4].Removed);
        Assert.True(File.Exists(stray));
        Assert.Equal(results[4].ArchivePath, SiteBackup.Latest(Folder));
    }

    [Fact]
    public async Task Two_backups_in_the_same_moment_do_not_overwrite_each_other()
    {
        var first = await SiteBackup.CreateAsync(Sources, Folder, keep: 5, s_now, Ct);
        var second = await SiteBackup.CreateAsync(Sources, Folder, keep: 5, s_now, Ct);

        Assert.NotEqual(first.ArchivePath, second.ArchivePath);
        Assert.Equal(2, SiteBackup.Archives(Folder).Count);
    }

    [Fact]
    public async Task A_missing_database_is_not_backed_up_as_an_empty_one()
    {
        var sources = Sources with { DatabasePath = Path.Combine(_directory, "typo.db") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => SiteBackup.CreateAsync(sources, Folder, keep: 3, s_now, Ct));
        Assert.False(File.Exists(sources.DatabasePath));
        Assert.Empty(SiteBackup.Archives(Folder));
    }

    [Fact]
    public async Task The_backup_command_reports_the_archive_and_refuses_keeping_none()
    {
        var settings = new BackupSettings(Sources, Folder, Keep: 2, LockFile: null);
        using var output = new StringWriter();

        Assert.Equal(0, await BackupCommands.BackupAsync(settings, s_now, output, Ct));
        Assert.Contains("backup-20261001T030000000Z.zip", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await BackupCommands.BackupAsync(settings with { Keep = 0 }, s_now, output, Ct));
    }

    // ---- Verify ----

    [Fact]
    public async Task A_damaged_database_entry_fails_the_check()
    {
        var archive = await BackupAsync();
        Rewrite(archive, (name, bytes) => name == SiteBackup.DatabaseEntry ? Flip(bytes, bytes.Length / 2) : bytes, fixManifest: false);

        var report = await VerifyAsync(archive);

        Assert.False(report.IsSound);
        Assert.Contains(report.Problems, p => p.StartsWith("game-event.db: damaged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_database_broken_with_a_matching_manifest_fails_the_sqlite_check()
    {
        var archive = await BackupAsync();

        // Garbage over the pages after the first one: the hashes agree, SQLite does not
        Rewrite(archive, (name, bytes) => name == SiteBackup.DatabaseEntry ? Garble(bytes) : bytes, fixManifest: true);

        var report = await VerifyAsync(archive);

        Assert.False(report.IsSound);
        Assert.Contains(report.Problems, p => p.StartsWith("SQLite integrity", StringComparison.Ordinal) || p.StartsWith("The database cannot be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_projection_that_disagrees_with_the_log_fails_the_season_check()
    {
        await PlayAsync();
        var archive = await BackupAsync();
        Rewrite(archive, (name, bytes) => name == SiteBackup.DatabaseEntry ? WithPointsChanged(bytes) : bytes, fixManifest: true);

        var report = await VerifyAsync(archive);

        Assert.False(report.IsSound);
        Assert.Contains(report.Problems, p => p.StartsWith($"Season {SiteFactory.SeasonId}: 1 difference", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_uploaded_file_missing_from_the_archive_fails_the_check()
    {
        var fileId = await StoreFileAsync();
        var archive = await BackupAsync();
        var thumbnail = "files/" + FileNames.Thumbnail(fileId, FileNames.Webp);
        Rewrite(archive, (name, bytes) => name == thumbnail ? null : bytes, fixManifest: true);

        var report = await VerifyAsync(archive);

        Assert.Equal([$"Uploaded file {FileNames.Thumbnail(fileId, FileNames.Webp)}: in the database, not in the archive."], report.Problems);
    }

    [Fact]
    public async Task Entries_the_manifest_does_not_know_or_misses_fail_the_check()
    {
        var archive = await BackupAsync();
        Rewrite(archive, (name, bytes) => name.StartsWith("keys/", StringComparison.Ordinal) ? null : bytes, fixManifest: false, extra: ("files/extra.webp", [1, 2, 3]));

        var report = await VerifyAsync(archive);

        Assert.Contains("files/extra.webp: in the archive, not in the manifest.", report.Problems);
        Assert.Contains("keys/key-1.xml: in the manifest, missing from the archive.", report.Problems);
    }

    [Fact]
    public async Task A_name_that_climbs_out_of_its_folder_fails_the_check()
    {
        var archive = await BackupAsync();
        Rewrite(archive, (_, bytes) => bytes, fixManifest: true, extra: ("files/../../evil.sh", [1]));

        var report = await VerifyAsync(archive);

        Assert.Contains("files/../../evil.sh: not a name a backup writes.", report.Problems);
    }

    [Theory]
    [InlineData("not a zip at all")]
    [InlineData("")]
    public async Task Something_that_is_not_an_archive_fails_the_check(string content)
    {
        var archive = Path.Combine(_directory, "backup-20260101T000000000Z.zip");
        await File.WriteAllTextAsync(archive, content, Ct);

        var report = await VerifyAsync(archive);

        Assert.StartsWith("Not a readable backup archive", Assert.Single(report.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_archive_without_a_manifest_fails_the_check()
    {
        var archive = await BackupAsync();
        Rewrite(archive, (name, bytes) => name == SiteBackup.ManifestEntry ? null : bytes, fixManifest: false);

        var report = await VerifyAsync(archive);

        Assert.StartsWith("No manifest.json", Assert.Single(report.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_verify_command_takes_the_newest_backup_and_fails_loudly()
    {
        var settings = new BackupSettings(Sources, Folder, Keep: 5, LockFile: null);
        using var output = new StringWriter();
        Assert.Equal(1, await BackupCommands.VerifyAsync(null, null, settings, output, Ct));

        await SiteBackup.CreateAsync(Sources, Folder, keep: 5, s_now, Ct);
        var newest = (await SiteBackup.CreateAsync(Sources, Folder, keep: 5, s_now.AddDays(1), Ct)).ArchivePath;
        Assert.Equal(0, await BackupCommands.VerifyAsync(null, null, settings, output, Ct));

        Rewrite(newest, (name, bytes) => name == SiteBackup.DatabaseEntry ? Flip(bytes, 100) : bytes, fixManifest: false);
        Assert.Equal(3, await BackupCommands.VerifyAsync(null, null, settings, output, Ct));
        Assert.Contains("NOT sound", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tool_behind_npm_run_backup_verify_fails_on_a_damaged_archive()
    {
        await PlayAsync();
        var archive = await BackupAsync();
        Assert.Equal(0, await RunToolAsync("backup-verify", archive));

        Rewrite(archive, (name, bytes) => name == SiteBackup.DatabaseEntry ? WithPointsChanged(bytes) : bytes, fixManifest: true);
        Assert.Equal(3, await RunToolAsync("backup-verify", archive));
        Assert.Equal(1, await RunToolAsync("backup-verify", Path.Combine(_directory, "nothing-here")));
    }

    // ---- Restore ----

    [Fact]
    public async Task A_restore_puts_the_data_back_and_keeps_what_it_replaced()
    {
        await PlayAsync();
        var fileId = await StoreFileAsync();
        var archive = await BackupAsync();
        var target = NewTarget();
        await File.WriteAllTextAsync(target.DatabasePath, "the broken database", Ct);
        Directory.CreateDirectory(target.FilesPath);
        await File.WriteAllTextAsync(Path.Combine(target.FilesPath, "old.webp"), "old", Ct);

        var result = await SiteRestore.RestoreAsync(archive, target, true, s_now, null, Ct);

        Assert.True(result.Report.IsSound, string.Join("\n", result.Report.Problems));
        Assert.Equal("the broken database", await File.ReadAllTextAsync(Path.Combine(result.ReplacedDataFolder!, "site.db"), Ct));
        Assert.True(File.Exists(Path.Combine(result.ReplacedDataFolder!, "files", "old.webp")));
        Assert.True(File.Exists(Path.Combine(target.FilesPath, FileNames.Main(fileId, FileNames.Webp))));
        Assert.False(File.Exists(Path.Combine(target.FilesPath, "old.webp")));
        Assert.True(File.Exists(Path.Combine(target.KeysPath!, "key-1.xml")));
        Assert.Equal(["before-restore-20261001T030000Z", "files", "keys", "site.db"], Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(target.DatabasePath)!).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        await using var restored = Open(target.DatabasePath);
        await using var original = _site.NewDb();
        Assert.Equal(await original.Events.CountAsync(Ct), await restored.Events.CountAsync(Ct));
        await using var connection = new SqliteConnection($"Data Source={target.DatabasePath};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", await mode.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task A_restore_without_keys_leaves_the_keys_alone()
    {
        var archive = await BackupAsync();
        var target = NewTarget();
        Directory.CreateDirectory(target.KeysPath!);
        await File.WriteAllTextAsync(Path.Combine(target.KeysPath!, "staging-key.xml"), "own", Ct);

        var result = await SiteRestore.RestoreAsync(archive, target, false, s_now, null, Ct);

        Assert.True(result.Report.IsSound);
        Assert.Equal(["staging-key.xml"], Directory.EnumerateFiles(target.KeysPath!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_damaged_backup_is_not_restored_and_nothing_is_touched()
    {
        var archive = await BackupAsync();
        Rewrite(archive, (name, bytes) => name == SiteBackup.DatabaseEntry ? Flip(bytes, 200) : bytes, fixManifest: false);
        var target = NewTarget();
        await File.WriteAllTextAsync(target.DatabasePath, "current", Ct);

        var result = await SiteRestore.RestoreAsync(archive, target, true, s_now, null, Ct);

        Assert.Null(result.ReplacedDataFolder);
        Assert.Equal("current", await File.ReadAllTextAsync(target.DatabasePath, Ct));
        Assert.Equal(["site.db"], Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(target.DatabasePath)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task The_restore_command_refuses_while_the_site_runs()
    {
        var archive = await BackupAsync();
        var target = NewTarget();
        var lockFile = Path.Combine(Path.GetDirectoryName(target.DatabasePath)!, "site.lock");
        var settings = new BackupSettings(target, Folder, Keep: 5, lockFile);
        using var output = new StringWriter();

        using (SiteLock.TryAcquire(lockFile))
        {
            Assert.Equal(1, await BackupCommands.RestoreAsync(archive, null, true, settings, s_now, output, null, Ct));
            Assert.False(File.Exists(target.DatabasePath));
        }

        Assert.Equal(0, await BackupCommands.RestoreAsync(archive, null, true, settings, s_now, output, null, Ct));
        Assert.True(File.Exists(target.DatabasePath));
    }

    [Fact]
    public void The_settings_follow_the_sites_configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Main"] = "Data Source=data/game-event.db",
            ["Files:Path"] = "data/files",
            ["DataProtection:KeysPath"] = "data/keys",
            ["Backup:Path"] = "backups",
            ["Backup:Keep"] = "7",
            ["Site:LockFile"] = "data/site.lock",
        }).Build();
        string At(string path) => Path.GetFullPath(Path.Combine(_directory, path));

        var settings = BackupSettings.From(configuration, _directory);

        Assert.Equal(new BackupSources(At("data/game-event.db"), At("data/files"), At("data/keys")), settings.Sources);
        Assert.Equal((At("backups"), 7, At("data/site.lock")), (settings.Folder, settings.Keep, Path.GetFullPath(settings.LockFile!)));

        var defaults = BackupSettings.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Main"] = "Data Source=var/game-event.db",
        }).Build(), _directory);
        // No Site:LockFile: the lock next to the database, as the site and the import tool take it (D-214)
        Assert.Equal((At("var/files"), null, At("var/backups"), BackupSettings.DefaultKeep, At("var/site.lock")), (defaults.Sources.FilesPath, defaults.Sources.KeysPath, defaults.Folder, defaults.Keep, defaults.LockFile));
    }

    // ---- Helpers ----

    private async Task<string> BackupAsync() => (await SiteBackup.CreateAsync(Sources, Folder, keep: 10, s_now, Ct)).ArchivePath;

    private async Task<BackupReport> VerifyAsync(string archive) =>
        await BackupVerifier.VerifyAsync(archive, Path.Combine(_directory, "verify-" + Guid.NewGuid().ToString("N")), null, Ct);

    private BackupSources NewTarget()
    {
        var root = Path.Combine(_directory, "restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new BackupSources(Path.Combine(root, "site.db"), Path.Combine(root, "files"), Path.Combine(root, "keys"));
    }

    private static Infrastructure.Database.GameEventDbContext Open(string database)
    {
        var options = new DbContextOptionsBuilder<Infrastructure.Database.GameEventDbContext>();
        Infrastructure.Database.SqliteDatabase.Configure(options, $"Data Source={database};Pooling=False");
        return new Infrastructure.Database.GameEventDbContext(options.Options);
    }

    /// <summary>A stored upload as the site leaves it: both files on disk, then the row.</summary>
    private async Task<Guid> StoreFileAsync()
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(_site.FilesPath);
        await File.WriteAllBytesAsync(Path.Combine(_site.FilesPath, FileNames.Main(id, FileNames.Webp)), [1, 2, 3, 4], Ct);
        await File.WriteAllBytesAsync(Path.Combine(_site.FilesPath, FileNames.Thumbnail(id, FileNames.Webp)), [5, 6], Ct);
        await using var db = _site.NewDb();
        db.Files.Add(new FileRecord { Id = id, OwnerId = _site.Users["vasya"], MediaType = FileNames.Webp, Bytes = 4, Width = 1, Height = 1, Frames = 1, CreatedAt = s_now });
        await db.SaveChangesAsync(Ct);
        return id;
    }

    private async Task PlayAsync()
    {
        var vasya = await _site.SignedInAsync("vasya");
        foreach (var (action, body) in new (string, object)[]
        {
            ("roll", new { commandId = Guid.NewGuid() }),
            ("start", new { commandId = Guid.NewGuid() }),
            ("complete", new { commandId = Guid.NewGuid(), difficulty = "normal" }),
        })
        {
            var response = await vasya.PostAsJsonAsync($"/api/seasons/{SiteFactory.SeasonId}/{action}", body, Ct);
            Assert.True(response.IsSuccessStatusCode, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        }
    }

    private static byte[] Flip(byte[] bytes, int at)
    {
        var copy = bytes.ToArray();
        copy[at] ^= 0xFF;
        return copy;
    }

    private static byte[] Garble(byte[] bytes)
    {
        var copy = bytes.ToArray();
        var pageSize = (copy[16] << 8) | copy[17];
        for (var i = pageSize; i < copy.Length; i += 7)
        {
            copy[i] = (byte)(i * 31);
        }

        return copy;
    }

    /// <summary>The database with Vasya's points changed behind the engine: sound SQLite, a projection the log disagrees with.</summary>
    private byte[] WithPointsChanged(byte[] bytes)
    {
        var file = Path.Combine(_directory, "edit-" + Guid.NewGuid().ToString("N") + ".db");
        File.WriteAllBytes(file, bytes);
        using (var connection = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE SeasonPlayer SET Points = Points + 100 WHERE lower(Id) = lower($id)";
            command.Parameters.AddWithValue("$id", _site.Players["vasya"].ToString());
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        return File.ReadAllBytes(file);
    }

    /// <summary>
    /// Writes the archive again with its entries changed (null drops one) and optionally an extra one; with
    /// <paramref name="fixManifest"/> the manifest is made to agree with the new contents, like a careful forger would.
    /// </summary>
    internal static void Rewrite(string archive, Func<string, byte[], byte[]?> change, bool fixManifest, (string Name, byte[] Bytes)? extra = null)
    {
        var entries = new List<(string Name, byte[] Bytes)>();
        using (var zip = ZipFile.OpenRead(archive))
        {
            foreach (var entry in zip.Entries)
            {
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                if (change(entry.FullName, buffer.ToArray()) is { } changed)
                {
                    entries.Add((entry.FullName, changed));
                }
            }
        }

        if (extra is { } added)
        {
            entries.Insert(0, added);
        }

        if (fixManifest && entries.FindIndex(e => e.Name == SiteBackup.ManifestEntry) is var at and >= 0)
        {
            var manifest = JsonSerializer.Deserialize<BackupManifest>(entries[at].Bytes, JsonSerializerOptions.Web)!;
            var listed = entries.Where(e => e.Name != SiteBackup.ManifestEntry)
                .Select(e => new BackupEntry(e.Name, e.Bytes.LongLength, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(e.Bytes))))
                .ToList();
            entries[at] = (SiteBackup.ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(manifest with { Entries = listed }, JsonSerializerOptions.Web));
        }

        File.Delete(archive);
        using var output = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            using var stream = output.CreateEntry(name).Open();
            stream.Write(bytes);
        }
    }

    /// <summary>The import tool's entry point, as <c>npm run backup:verify</c> runs it.</summary>
    private static async Task<int> RunToolAsync(params string[] args)
    {
        var entry = typeof(Tools.Import.XlsxPoolReader).Assembly.EntryPoint!;
        return await Task.Run(() => entry.Invoke(null, [args]) switch
        {
            int code => code,
            Task<int> task => task.GetAwaiter().GetResult(),
            var other => throw new InvalidOperationException($"Unexpected entry point result {other}"),
        }, Ct);
    }
}

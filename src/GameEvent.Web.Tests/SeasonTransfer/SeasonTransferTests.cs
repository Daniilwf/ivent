using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Web.Tests.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameEvent.Web.Tests.SeasonTransfer;

/// <summary>
/// The integrity check and the season archive (C12b, L4, L5, D-32, D-105): <c>GET /api/admin/seasons/{id}/integrity</c>
/// compares the log's state with the stored one; <c>GET /api/admin/seasons/{id}/export</c> gives the archive that
/// <c>SeasonTransfer.ImportAsync</c> loads into another database.
/// </summary>
public sealed class SeasonTransferTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();
    private readonly string _otherDirectory = Path.Combine(Path.GetTempPath(), "gameevent-import-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    private static string AdminUrl(string action, Guid? seasonId = null) => $"/api/admin/seasons/{seasonId ?? SiteFactory.SeasonId}/{action}";

    private string OtherConnection => $"Data Source={Path.Combine(_otherDirectory, "other.db")};Pooling=False";

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        Directory.CreateDirectory(_otherDirectory);
        await SqliteDatabase.MigrateAsync(OtherConnection);
    }

    public async ValueTask DisposeAsync()
    {
        await _site.DisposeAsync();
        try
        {
            Directory.Delete(_otherDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by the OS: the temp folder is cleaned later.
        }
    }

    // ---- Integrity ----

    [Fact]
    public async Task A_played_season_is_intact()
    {
        await PlayAsync();
        var admin = await _site.SignedInAsync("admin");

        using var doc = JsonDocument.Parse(await admin.GetStringAsync(AdminUrl("integrity"), Ct));

        Assert.True(doc.RootElement.GetProperty("isIntact").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("differences").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("lastSequence").GetInt64() > 0);
    }

    [Fact]
    public async Task A_row_changed_behind_the_engine_is_found()
    {
        await PlayAsync();
        await using (var db = _site.NewDb())
        {
            var player = await db.SeasonPlayers.FirstAsync(p => p.SeasonId == SiteFactory.SeasonId, Ct);
            player.Points += 1;
            player.CellId = "c9";
            await db.SaveChangesAsync(Ct);
        }

        await using var check = _site.NewDb();
        var report = await SeasonIntegrity.CheckAsync(check, SiteFactory.SeasonId, Ct);

        Assert.False(report!.IsIntact);
        var line = Assert.Single(report.Differences);
        Assert.StartsWith("player ", line, StringComparison.Ordinal);
        Assert.Contains("points", line, StringComparison.Ordinal);
        Assert.Contains("cellId", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_run_row_is_found()
    {
        await PlayAsync();
        await using (var db = _site.NewDb())
        {
            await db.Proofs.ExecuteDeleteAsync(Ct);
            await db.Reviews.ExecuteDeleteAsync(Ct);
            await db.ManualEffects.ExecuteDeleteAsync(Ct);
            var run = await db.Runs.FirstAsync(Ct);
            db.Runs.Remove(run);
            foreach (var p in await db.SeasonPlayers.Where(p => p.ActiveRunId == run.Id).ToListAsync(Ct))
            {
                p.ActiveRunId = null;
            }

            await db.SaveChangesAsync(Ct);
        }

        await using var check = _site.NewDb();
        var report = await SeasonIntegrity.CheckAsync(check, SiteFactory.SeasonId, Ct);

        Assert.Contains(report!.Differences, d => d.StartsWith("run ", StringComparison.Ordinal) && d.EndsWith("in the log, not stored", StringComparison.Ordinal));
    }

    // ---- Export and import ----

    [Fact]
    public async Task A_season_exported_and_imported_elsewhere_is_the_same_season()
    {
        await PlayAsync();
        var archive = await ExportAsync();

        await using var other = OpenOther();
        var result = await Infrastructure.Seasons.SeasonTransfer.ImportAsync(other, archive, _site.Clock.UtcNow, ct: Ct);

        Assert.True(result.Integrity.IsIntact);
        Assert.Equal(archive.Events.Count, result.Events);
        await using var source = _site.NewDb();
        var (original, _) = await EventLogReader.ReplaySeasonAsync(source, SiteFactory.SeasonId, Ct);
        var (imported, _) = await EventLogReader.ReplaySeasonAsync(other, SiteFactory.SeasonId, Ct);
        Assert.Equal(WithoutAccounts(original), WithoutAccounts(imported));

        // The undo survives with its mark
        Assert.Equal(
            await source.Events.CountAsync(e => e.UndoneByEventId != null, Ct),
            await other.Events.CountAsync(e => e.UndoneByEventId != null, Ct));
        Assert.True(await other.Events.AnyAsync(e => e.UndoneByEventId != null, Ct));
    }

    [Fact]
    public async Task Accounts_are_matched_by_login_and_missing_ones_become_deleted_placeholders()
    {
        await PlayAsync();
        var archive = await ExportAsync();
        await using var other = OpenOther();
        var localVasya = new UserRecord
        {
            Id = Guid.NewGuid(),
            Login = "Vasya",
            NormalizedLogin = "vasya",
            Name = "Вася здесь",
            PasswordHash = "x",
            SecurityStamp = "x",
            Role = Role.Player,
            CreatedAt = _site.Clock.UtcNow,
        };
        other.Users.Add(localVasya);
        await other.SaveChangesAsync(Ct);

        var result = await Infrastructure.Seasons.SeasonTransfer.ImportAsync(other, archive, _site.Clock.UtcNow, ct: Ct);

        Assert.DoesNotContain("vasya", result.CreatedUsers);
        Assert.Contains(await other.SeasonPlayers.Select(p => p.UserId).ToListAsync(Ct), id => id == localVasya.Id);
        var placeholders = await other.Users.Where(u => result.CreatedUsers.Contains(u.Login)).ToListAsync(Ct);
        Assert.NotEmpty(placeholders);
        Assert.All(placeholders, u => Assert.True(u.IsDeleted && u.MustChangePassword));
        Assert.Equal(archive.Games.Count, await other.Games.CountAsync(Ct));
    }

    [Fact]
    public async Task The_same_season_is_not_imported_twice()
    {
        await PlayAsync();
        var archive = await ExportAsync();
        await using var other = OpenOther();
        await Infrastructure.Seasons.SeasonTransfer.ImportAsync(other, archive, _site.Clock.UtcNow, ct: Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Infrastructure.Seasons.SeasonTransfer.ImportAsync(other, archive, _site.Clock.UtcNow, ct: Ct));
    }

    [Fact]
    public async Task A_broken_archive_leaves_nothing_behind()
    {
        await PlayAsync();
        var archive = await ExportAsync();
        var broken = archive with { Events = [.. archive.Events.Skip(1)] };
        await using var other = OpenOther();

        await Assert.ThrowsAnyAsync<Exception>(() => Infrastructure.Seasons.SeasonTransfer.ImportAsync(other, broken, _site.Clock.UtcNow, ct: Ct));

        await using var after = OpenOther();
        Assert.Empty(await after.Events.ToListAsync(Ct));
        Assert.Empty(await after.Users.ToListAsync(Ct));
        Assert.Empty(await after.Games.ToListAsync(Ct));
    }

    [Fact]
    public async Task An_archive_of_another_format_is_refused()
    {
        await PlayAsync();
        var archive = await ExportAsync() with { Format = 99 };
        using var zip = new MemoryStream();
        await Infrastructure.Seasons.SeasonTransfer.WriteZipAsync(archive, zip, Ct);
        zip.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => Infrastructure.Seasons.SeasonTransfer.ReadZipAsync(zip, Ct));
    }

    [Fact]
    public async Task The_export_is_a_zip_with_the_log()
    {
        await PlayAsync();
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.GetAsync(AdminUrl("export"), Ct);

        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(".zip", response.Content.Headers.ContentDisposition?.FileName ?? "", StringComparison.Ordinal);
        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        var archive = await Infrastructure.Seasons.SeasonTransfer.ReadZipAsync(stream, Ct);
        await using var db = _site.NewDb();
        Assert.Equal(await db.Events.CountAsync(e => e.SeasonId == SiteFactory.SeasonId, Ct), archive.Events.Count);
        Assert.Contains(archive.Users, u => u.Login == "vasya");
    }

    // ---- After the reviews of C12b ----

    [Fact]
    public async Task Accounts_in_undo_snapshots_are_this_databases_accounts()
    {
        await PlayAsync();
        var archive = await ExportAsync();
        await using var other = OpenOther();

        await Infrastructure.Seasons.SeasonTransfer.ImportAsync(other, archive, _site.Clock.UtcNow, ct: Ct);

        var local = (await other.Users.Select(u => u.Id).ToListAsync(Ct)).ToHashSet();
        var undos = (await EventLogReader.ReadSeasonAsync(other, SiteFactory.SeasonId, Ct)).OfType<Engine.Undo.CommandUndone>().ToList();
        Assert.NotEmpty(undos);
        Assert.All(undos.SelectMany(u => u.Players), p => Assert.Contains(p.UserId, local));
        Assert.All(await other.Events.Where(e => e.AuthorId != null).Select(e => e.AuthorId!.Value).ToListAsync(Ct), id => Assert.Contains(id, local));
    }

    [Fact]
    public async Task The_log_is_copied_as_stored_except_the_events_naming_accounts()
    {
        await PlayAsync();
        var archive = await ExportAsync();
        await using var other = OpenOther();

        await Infrastructure.Seasons.SeasonTransfer.ImportAsync(other, archive, _site.Clock.UtcNow, ct: Ct);

        var copied = await other.Events.Where(e => e.SeasonId == SiteFactory.SeasonId).OrderBy(e => e.Sequence).ToListAsync(Ct);
        foreach (var (original, copy) in archive.Events.Zip(copied))
        {
            Assert.Equal((original.Sequence, original.CommandId, original.CommandType, original.Type, original.OccurredAt), (copy.Sequence, copy.CommandId, copy.CommandType, copy.Type, copy.OccurredAt));
            if (original.Type is not ("season-player-added" or "command-undone"))
            {
                Assert.Equal((original.Version, original.Data), (copy.Version, copy.Data));
            }
        }
    }

    [Fact]
    public async Task A_projection_that_disagrees_with_the_log_refuses_the_import()
    {
        await PlayAsync();
        var archive = await ExportAsync();
        await using var other = OpenOther();

        await Assert.ThrowsAsync<InvalidDataException>(() => Infrastructure.Seasons.SeasonTransfer.ImportAsync(
            other, archive, _site.Clock.UtcNow, new ImportOptions(), async db =>
            {
                var player = await db.SeasonPlayers.FirstAsync(Ct);
                player.Points += 5;
                await db.SaveChangesAsync(Ct);
            },
            Ct));

        await using var after = OpenOther();
        Assert.Empty(await after.Events.ToListAsync(Ct));
        Assert.Empty(await after.SeasonPlayers.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_finished_season_travels_with_its_result_and_a_spoiled_result_is_found()
    {
        await PlayAsync();
        var admin = await _site.SignedInAsync("admin");
        await using (var db = _site.NewDb())
        {
            foreach (var run in await db.Runs.Where(r => r.Status == Engine.Runs.RunStatus.Completed).ToListAsync(Ct))
            {
                await PostOkAsync(admin, AdminUrl($"runs/{run.Id}/approve"), new { commandId = Guid.NewGuid(), comment = "Видел" });
            }
        }

        await PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "closing" });
        await PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "finished" });
        var archive = await ExportAsync();
        await using var other = OpenOther();

        var result = await Infrastructure.Seasons.SeasonTransfer.ImportAsync(other, archive, _site.Clock.UtcNow, ct: Ct);

        Assert.True(result.Integrity.IsIntact);
        Assert.NotEmpty(await other.SeasonResults.ToListAsync(Ct));
        var row = await other.SeasonResults.FirstAsync(Ct);
        row.Points += 10;
        await other.SaveChangesAsync(Ct);
        var report = await SeasonIntegrity.CheckAsync(other, SiteFactory.SeasonId, Ct);
        Assert.Contains("season.result", report!.Differences);
    }

    [Fact]
    public async Task A_missing_ruleset_version_row_is_found()
    {
        await PlayAsync();
        await using (var db = _site.NewDb())
        {
            await db.Rulesets.Where(r => r.SeasonId == SiteFactory.SeasonId).ExecuteDeleteAsync(Ct);
        }

        await using var check = _site.NewDb();
        var report = await SeasonIntegrity.CheckAsync(check, SiteFactory.SeasonId, Ct);

        Assert.Contains("ruleset v1: in the log, not stored", report!.Differences);
    }

    public static TheoryData<string> BrokenArchives() => ["notFirst", "gap", "sameLogin", "danglingUndo", "badTags", "unlistedAccount", "noEvents"];

    [Theory]
    [MemberData(nameof(BrokenArchives))]
    public async Task An_inconsistent_archive_is_refused_before_anything_is_written(string kind)
    {
        await PlayAsync();
        var archive = await ExportAsync();
        var events = archive.Events.ToList();
        SeasonArchive broken = kind switch
        {
            "notFirst" => archive with { Events = [.. events.Skip(1).Select((e, i) => e with { Sequence = i + 1, UndoneBySequence = null })] },
            "gap" => archive with { Events = [.. events.Take(2), .. events.Skip(3)] },
            "sameLogin" => archive with { Users = [.. archive.Users, archive.Users[0] with { Id = Guid.NewGuid(), Login = archive.Users[0].Login.ToUpperInvariant() }] },
            "danglingUndo" => archive with { Events = [.. events.Select(e => e with { UndoneBySequence = e.Sequence == 2 ? 999 : e.UndoneBySequence })] },
            "badTags" => archive with { Games = [.. archive.Games.Select((g, i) => i == 0 ? g with { TagsJson = "{" } : g)] },
            "unlistedAccount" => archive with { Users = [] },
            _ => archive with { Events = [] },
        };
        await using var other = OpenOther();

        await Assert.ThrowsAsync<InvalidDataException>(() => Infrastructure.Seasons.SeasonTransfer.ImportAsync(other, broken, _site.Clock.UtcNow, ct: Ct));

        await using var after = OpenOther();
        Assert.Empty(await after.Events.ToListAsync(Ct));
        Assert.Empty(await after.Users.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_matched_account_with_another_role_is_refused_unless_allowed()
    {
        await PlayAsync();
        var archive = await ExportAsync();
        await using var other = OpenOther();
        other.Users.Add(new UserRecord
        {
            Id = Guid.NewGuid(),
            Login = "vasya",
            NormalizedLogin = "vasya",
            Name = "Вася-админ",
            PasswordHash = "x",
            SecurityStamp = "x",
            Role = Role.Admin,
            CreatedAt = _site.Clock.UtcNow,
        });
        await other.SaveChangesAsync(Ct);

        await Assert.ThrowsAsync<InvalidDataException>(() => Infrastructure.Seasons.SeasonTransfer.ImportAsync(other, archive, _site.Clock.UtcNow, ct: Ct));

        var result = await Infrastructure.Seasons.SeasonTransfer.ImportAsync(other, archive, _site.Clock.UtcNow, new ImportOptions(AllowRoleMismatch: true), Ct);
        Assert.Contains(result.MatchedUsers, m => m is { Login: "vasya", LocalRole: Role.Admin, ArchiveRole: Role.Player });
    }

    [Fact]
    public async Task Placeholders_are_deleted_spectators_even_for_an_admin_of_the_archive()
    {
        await PlayAsync();
        var archive = await ExportAsync();
        Assert.Contains(archive.Users, u => u.Role == Role.Admin);
        await using var other = OpenOther();

        var result = await Infrastructure.Seasons.SeasonTransfer.ImportAsync(other, archive, _site.Clock.UtcNow, ct: Ct);

        var placeholders = await other.Users.Where(u => result.CreatedUsers.Contains(u.Login)).ToListAsync(Ct);
        Assert.Equal(archive.Users.Count, placeholders.Count);
        Assert.All(placeholders, u => Assert.Equal((Role.Spectator, true), (u.Role, u.IsDeleted)));
    }

    [Fact]
    public async Task The_live_pool_changes_only_when_asked()
    {
        await PlayAsync();
        var archive = await ExportAsync();

        await using (var quiet = OpenOther())
        {
            await Infrastructure.Seasons.SeasonTransfer.ImportAsync(quiet, archive, _site.Clock.UtcNow, ct: Ct);
            Assert.All(await quiet.Games.ToListAsync(Ct), g => Assert.True(g.IsDeleted));
            Assert.Empty(await quiet.Categories.ToListAsync(Ct));
        }

        var second = Path.Combine(_otherDirectory, "second.db");
        var connection = $"Data Source={second};Pooling=False";
        await SqliteDatabase.MigrateAsync(connection, Ct);
        var options = new DbContextOptionsBuilder<GameEventDbContext>();
        SqliteDatabase.Configure(options, connection);
        await using var withPool = new GameEventDbContext(options.Options);
        await Infrastructure.Seasons.SeasonTransfer.ImportAsync(withPool, archive, _site.Clock.UtcNow, new ImportOptions(WithPool: true), Ct);
        Assert.Contains(await withPool.Games.ToListAsync(Ct), g => !g.IsDeleted);
        Assert.Equal(archive.Categories.Count, await withPool.Categories.CountAsync(Ct));
    }

    [Fact]
    public async Task A_placeholder_account_fails_to_sign_in_cleanly()
    {
        await using (var db = _site.NewDb())
        {
            db.Users.Add(new UserRecord
            {
                Id = Guid.NewGuid(),
                Login = "ghost",
                NormalizedLogin = "ghost",
                Name = "Призрак",
                PasswordHash = Infrastructure.Seasons.SeasonTransfer.PlaceholderHashPrefix + "00",
                SecurityStamp = "x",
                Role = Role.Spectator,
                CreatedAt = _site.Clock.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var client = await _site.AnonymousAsync();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { login = "ghost", password = "anything" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Roles ----

    [Theory]
    [InlineData("vasya", "integrity")]
    [InlineData("zritel", "integrity")]
    [InlineData("vasya", "export")]
    [InlineData("zritel", "export")]
    public async Task Players_and_spectators_are_forbidden(string login, string action)
    {
        var client = await _site.SignedInAsync(login);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(AdminUrl(action), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("integrity")]
    [InlineData("export")]
    public async Task Anonymous_is_unauthorized(string action)
    {
        var client = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(AdminUrl(action), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("integrity")]
    [InlineData("export")]
    public async Task Unknown_season_is_not_found(string action)
    {
        var admin = await _site.SignedInAsync("admin");

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(AdminUrl(action, Guid.NewGuid()), Ct)).StatusCode);
    }

    // ---- Helpers ----

    // Вася completes a run with a proof and a review; Петя rolls; an admin bonus is undone
    private async Task PlayAsync()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(vasya, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal", review = new { rating = 8, text = "Хорошо" } });
        using (var me = JsonDocument.Parse(await vasya.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct)))
        {
            var runId = me.RootElement.GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetGuid();
            await PostOkAsync(vasya, Url($"runs/{runId}/proof"), new { commandId = Guid.NewGuid(), links = new[] { "https://imgur.com/a/credits" } });
        }

        await PostOkAsync(petya, Url("roll"), new { commandId = Guid.NewGuid() });
        var bonus = Guid.NewGuid();
        var sent = await _site.Services.GetRequiredService<Infrastructure.Queue.CommandBus>().SendAsync(new Infrastructure.Queue.CommandEnvelope(
            bonus, SiteFactory.SeasonId, new Engine.Players.AdjustPlayer(await PlayerIdAsync("petya"), "Бонус", PointsDelta: 3), AuthorId: null));
        Assert.True(sent.IsAccepted);
        await PostOkAsync(admin, AdminUrl("undo"), new { commandId = Guid.NewGuid(), targetCommandId = bonus, comment = "Ошибка" });
    }

    private async Task<Guid> PlayerIdAsync(string login)
    {
        await using var db = _site.NewDb();
        var user = await db.Users.SingleAsync(u => u.NormalizedLogin == login, Ct);
        return (await db.SeasonPlayers.SingleAsync(p => p.SeasonId == SiteFactory.SeasonId && p.UserId == user.Id, Ct)).Id;
    }

    private async Task<SeasonArchive> ExportAsync()
    {
        await using var db = _site.NewDb();
        return (await Infrastructure.Seasons.SeasonTransfer.ExportAsync(db, SiteFactory.SeasonId, _site.Clock.UtcNow, Ct))!;
    }

    private GameEventDbContext OpenOther()
    {
        var options = new DbContextOptionsBuilder<GameEventDbContext>();
        SqliteDatabase.Configure(options, OtherConnection);
        return new GameEventDbContext(options.Options);
    }

    // Accounts are this database's: the season is the same up to the players' user ids
    private static SeasonState WithoutAccounts(SeasonState state) =>
        state with { Players = state.Players.SetItems(state.Players.Select(p => KeyValuePair.Create(p.Key, p.Value with { UserId = Guid.Empty }))) };

    private static async Task PostOkAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }
}

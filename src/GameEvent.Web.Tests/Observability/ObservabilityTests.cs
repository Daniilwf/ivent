using System.Net;
using System.Text.Json;
using GameEvent.Web.Observability;
using GameEvent.Web.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Observability;

/// <summary>
/// Observability (SPEC «Наблюдаемость», A10, D-107): <c>/health</c> over the database, the disk and the queue; the
/// journal of unhandled exceptions for the admin's «Ошибки» page, without secrets; Serilog to a rotating file.
/// </summary>
public sealed class ObservabilityTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- /health ----

    [Fact]
    public async Task Health_is_green_for_anyone_and_names_only_statuses()
    {
        var client = await _site.AnonymousAsync();

        var response = await client.GetAsync("/health", Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("healthy", doc.RootElement.GetProperty("status").GetString());
        var checks = doc.RootElement.GetProperty("checks");
        Assert.Equal(["database", "disk", "queue"], checks.EnumerateObject().Select(c => c.Name).Order());
        Assert.All(checks.EnumerateObject(), c => Assert.Equal("healthy", c.Value.GetString()));
        Assert.DoesNotContain("exception", await response.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Health_is_red_when_the_database_has_no_schema()
    {
        await using (var db = _site.NewDb())
        {
            // Test code may break the database on purpose: the site must say so, not answer an empty season
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF; DROP TABLE \"Season\";", Ct);
        }

        var client = await _site.AnonymousAsync();
        var response = await client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("unhealthy", doc.RootElement.GetProperty("checks").GetProperty("database").GetString());
    }

    [Fact]
    public async Task Health_is_red_when_the_disk_is_short()
    {
        using var hungry = _site.WithWebHostBuilder(b => b.UseSetting("Health:MinFreeDiskMb", "999999999"));
        var client = hungry.CreateClient();

        var response = await client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("unhealthy", doc.RootElement.GetProperty("checks").GetProperty("disk").GetString());
    }

    // ---- The journal of errors ----

    [Fact]
    public async Task An_unhandled_exception_goes_to_the_journal_with_the_request_and_the_user()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var failed = await vasya.GetAsync("/api/test/fail?token=secret-query-value", Ct);

        // The client learns nothing about the inside
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        var body = await failed.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("test failure", body, StringComparison.Ordinal);

        var admin = await _site.SignedInAsync("admin");
        using var doc = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/errors", Ct));
        var entry = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(("GET", "/api/test/fail", "vasya"), (entry.GetProperty("method").GetString(), entry.GetProperty("path").GetString(), entry.GetProperty("userLogin").GetString()));
        Assert.Equal("System.InvalidOperationException", entry.GetProperty("exceptionType").GetString());
        Assert.Contains("test failure", entry.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(entry.GetProperty("stackTrace").GetString()));

        // No query, cookies or password in the entry
        var text = entry.GetRawText();
        Assert.DoesNotContain("secret-query-value", text, StringComparison.Ordinal);
        Assert.DoesNotContain(SiteFactory.Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain("ge.session", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_journal_keeps_the_latest_first()
    {
        var anonymous = await _site.AnonymousAsync();
        await anonymous.GetAsync("/api/test/fail", Ct);
        var vasya = await _site.SignedInAsync("vasya");
        await vasya.GetAsync("/api/test/fail", Ct);

        var admin = await _site.SignedInAsync("admin");
        using var doc = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/errors", Ct));

        Assert.Equal(["vasya", null], doc.RootElement.EnumerateArray().Select(e => e.GetProperty("userLogin").GetString()));
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Players_and_spectators_do_not_see_the_errors(string login)
    {
        var client = await _site.SignedInAsync(login);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/errors", Ct)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_does_not_see_the_errors()
    {
        var client = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/errors", Ct)).StatusCode);
    }

    // ---- The log file ----

    [Fact]
    public async Task Errors_are_written_to_the_log_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gameevent-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var logging = _site.WithWebHostBuilder(b => b
                .UseSetting("Logging:File:Enabled", "true")
                .UseSetting("Logging:File:Path", Path.Combine(folder, "game-event-.log"))))
            {
                var client = logging.CreateClient();
                await client.GetAsync("/api/test/fail", Ct);
            }

            // The sink may still be flushing while the host shuts down under a busy test run
            var text = "";
            for (var attempt = 0; attempt < 100 && !text.Contains("A test failure", StringComparison.Ordinal); attempt++)
            {
                text = Directory.Exists(folder)
                    ? string.Concat(Directory.GetFiles(folder, "game-event-*.log").Select(f => ReadShared(f)))
                    : "";
                if (!text.Contains("A test failure", StringComparison.Ordinal))
                {
                    await Task.Delay(100, Ct);
                }
            }

            Assert.Single(Directory.GetFiles(folder, "game-event-*.log"));
            Assert.Contains("Unhandled exception in GET /api/test/fail", text, StringComparison.Ordinal);
            Assert.Contains("A test failure", text, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A file still held: the temp folder is cleaned later
            }
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("Trace", Serilog.Events.LogEventLevel.Verbose)]
    [InlineData("Debug", Serilog.Events.LogEventLevel.Debug)]
    [InlineData("Information", Serilog.Events.LogEventLevel.Information)]
    [InlineData("Warning", Serilog.Events.LogEventLevel.Warning)]
    [InlineData("Error", Serilog.Events.LogEventLevel.Error)]
    [InlineData("Critical", Serilog.Events.LogEventLevel.Fatal)]
    [InlineData("None", Serilog.Events.LogEventLevel.Fatal + 1)]
    [InlineData("Loud", Serilog.Events.LogEventLevel.Warning)]
    [InlineData(null, Serilog.Events.LogEventLevel.Warning)]
    public void Log_levels_are_read_from_the_config_names(string? name, Serilog.Events.LogEventLevel expected)
    {
        Assert.Equal(expected, ObservabilitySetup.Level(name, Serilog.Events.LogEventLevel.Warning));
    }

    [Fact]
    public void A_path_cannot_forge_a_line_of_the_log()
    {
        Assert.Equal("/api/x\\u000d\\u000a[ERR] fake", ErrorJournal.Printable("/api/x\r\n[ERR] fake"));
        Assert.Equal("/api/seasons/1", ErrorJournal.Printable("/api/seasons/1"));
    }

    [Fact]
    public void A_failed_sign_in_logs_an_unknown_login_only_as_a_hash()
    {
        var logged = GameEvent.Web.Accounts.AccountEndpoints.TypedLogin(null, "vasya hunter2-pasted");

        Assert.StartsWith("unknown#", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", logged, StringComparison.Ordinal);
        Assert.Equal(logged, GameEvent.Web.Accounts.AccountEndpoints.TypedLogin(null, "vasya hunter2-pasted"));
    }

    [Fact]
    public async Task Production_has_no_failing_test_endpoint()
    {
        await using var production = new SiteFactory(loginAttemptsPerMinute: 1000, environment: "Production");
        await production.SeedAsync();
        var client = production.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        var response = await client.GetAsync("/api/test/fail", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

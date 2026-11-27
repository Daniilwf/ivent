using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using GameEvent.Web.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.PlayerPreview;

/// <summary>D-939: preview audit records describe the real administrator and the selected game scope, without private materials.</summary>
public sealed class PlayerPreviewAuditTests : IAsyncLifetime
{
    private const string ContextHeader = "X-GameEvent-Player-Preview";
    private const string PrivateAdminLogin = "private-admin-login-audit-c7d3";
    private const string PrivateProofUrl = "https://example.com/private-preview-proof-audit-f3a8";
    private const string PrivateProofNote = "private-preview-proof-note-audit-b6e1";
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Season => $"/api/seasons/{SiteFactory.SeasonId}";

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        await using var db = _site.NewDb();
        await db.Users.Where(user => user.Id == _site.Users["admin"])
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(user => user.Login, PrivateAdminLogin)
                .SetProperty(user => user.NormalizedLogin, PrivateAdminLogin), Ct);
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Opening_a_preview_audits_the_real_admin_session_and_target_without_account_or_proof_material()
    {
        // Given a selected participant with a private proof that the administrator is about to read.
        using var probe = new AuditProbe();
        using var host = WithPreviewAndAudit(probe);
        using var admin = await SignInAsync(host, PrivateAdminLogin);
        using var player = await SignInAsync(host, "vasya");
        var sessionId = await CurrentSessionIdAsync(admin);
        var runId = await CompleteAsync(player);
        await PostOkAsync(player, $"{Season}/runs/{runId}/proof", new
        {
            commandId = Guid.NewGuid(),
            links = new[] { PrivateProofUrl },
            note = PrivateProofNote,
        });

        // When the real admin reads the scoped private view.
        using var response = await ReadPreviewAsync(admin);
        var view = await OkAsync(response);
        Assert.Contains(PrivateProofUrl, view.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(PrivateProofNote, view.GetRawText(), StringComparison.Ordinal);

        // Then a single named audit event carries the effective security context, not the target's account identity.
        var entry = Assert.Single(probe.Entries, entry => entry.EventName == "PlayerPreviewRead");
        AssertContext(entry, sessionId);
        AssertNoPrivateMaterials(entry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reading_a_preview_picture_audits_its_game_scope_file_and_thumbnail_choice(bool thumbnail)
    {
        using var probe = new AuditProbe();
        using var host = WithPreviewAndAudit(probe);
        using var admin = await SignInAsync(host, PrivateAdminLogin);
        using var player = await SignInAsync(host, "vasya");
        var sessionId = await CurrentSessionIdAsync(admin);
        var runId = await CompleteAsync(player);
        var fileId = await EventSeason.UploadAsync(player);
        await PostOkAsync(player, $"{Season}/runs/{runId}/proof", new
        {
            commandId = Guid.NewGuid(),
            links = Array.Empty<string>(),
            files = new[] { fileId },
        });

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{PreviewPath()}/files/{fileId}{(thumbnail ? "/thumbnail" : "")}");
        request.Headers.Add(ContextHeader, "1");
        using var response = await admin.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));

        var entry = Assert.Single(probe.Entries, entry => entry.EventName == "PlayerPreviewFileRead");
        AssertContext(entry, sessionId);
        Assert.Equal(fileId, ReadGuid(entry, "FileId"));
        Assert.Equal(thumbnail, Assert.IsType<bool>(entry.Properties["Thumbnail"]));
        AssertNoPrivateMaterials(entry);
    }

    private void AssertContext(CapturedLog entry, Guid sessionId)
    {
        Assert.Equal(_site.Users["admin"], ReadGuid(entry, "ActorId"));
        Assert.Equal(sessionId, ReadGuid(entry, "SessionId"));
        Assert.Equal(_site.Players["vasya"], ReadGuid(entry, "PlayerId"));
        Assert.Equal(SiteFactory.SeasonId, ReadGuid(entry, "SeasonId"));
        Assert.NotEqual(_site.Users["vasya"], ReadGuid(entry, "ActorId"));
        Assert.NotEqual(_site.Users["vasya"], ReadGuid(entry, "PlayerId"));
    }

    private static Guid ReadGuid(CapturedLog entry, string property)
    {
        Assert.True(entry.Properties.TryGetValue(property, out var value), $"The audit event omits {property}.");
        Assert.True(Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var id), $"{property} is not a GUID.");
        return id;
    }

    private static void AssertNoPrivateMaterials(CapturedLog entry)
    {
        var text = entry.Message + "\n" + string.Join("\n", entry.Properties.Select(pair =>
            $"{pair.Key}={Convert.ToString(pair.Value, CultureInfo.InvariantCulture)}"));
        Assert.DoesNotContain(PrivateAdminLogin, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SiteFactory.Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateProofUrl, text, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateProofNote, text, StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> WithPreviewAndAudit(AuditProbe probe) =>
        _site.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Site:PlayerPreview", "true");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ILoggerFactory>();
                services.AddSingleton<ILoggerFactory>(_ => LoggerFactory.Create(logging =>
                    logging.SetMinimumLevel(LogLevel.Trace).AddProvider(probe)));
            });
        });

    private string PreviewPath() =>
        $"/api/admin/seasons/{SiteFactory.SeasonId}/players/{_site.Players["vasya"]}/preview";

    private async Task<HttpResponseMessage> ReadPreviewAsync(HttpClient admin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, PreviewPath());
        request.Headers.Add(ContextHeader, "1");
        return await admin.SendAsync(request, Ct);
    }

    private static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> host, string login)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(client);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { login, password = SiteFactory.Password }, Ct);
        response.EnsureSuccessStatusCode();
        await SiteFactory.RefreshCsrfAsync(client);
        return client;
    }

    private static async Task<Guid> CurrentSessionIdAsync(HttpClient admin)
    {
        using var response = await admin.GetAsync("/api/auth/sessions", Ct);
        var sessions = await OkAsync(response);
        return Assert.Single(sessions.EnumerateArray(), session => session.GetProperty("current").GetBoolean())
            .GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CompleteAsync(HttpClient player)
    {
        await PostOkAsync(player, $"{Season}/roll");
        await PostOkAsync(player, $"{Season}/start");
        await PostOkAsync(player, $"{Season}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });
        using var response = await player.GetAsync(Season, Ct);
        return (await OkAsync(response)).GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetGuid();
    }

    private sealed record CapturedLog(string? EventName, string Message, IReadOnlyDictionary<string, object?> Properties);

    private sealed class AuditProbe : ILoggerProvider, ISupportExternalScope
    {
        private readonly ConcurrentQueue<CapturedLog> _entries = new();
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public IReadOnlyList<CapturedLog> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName) => new ProbeLogger(this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        private static void CopyProperties(object? state, Dictionary<string, object?> properties)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                {
                    properties[key] = value;
                }
            }
        }

        private sealed class ProbeLogger(AuditProbe probe) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => probe._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
                probe._scopes.ForEachScope(static (scope, properties) => CopyProperties(scope, properties), properties);
                CopyProperties(state, properties);
                probe._entries.Enqueue(new CapturedLog(eventId.Name, formatter(state, exception), properties));
            }
        }
    }
}

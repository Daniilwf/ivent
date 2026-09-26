using System.Net.Http.Json;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The server's one-off commands (J1, D-127): the first admin of a new site with a temporary password — only while the
/// site has no admin — the container's health check, and the session keys kept with the data.
/// </summary>
public sealed class OperationsTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_new_site_gets_its_first_admin_with_a_temporary_password_once()
    {
        _ = _site.Server;

        var first = await Operations.CreateFirstAdminAsync(_site.Services, "boss", "Главный", Ct);
        var second = await Operations.CreateFirstAdminAsync(_site.Services, "boss2", "Второй", Ct);

        Assert.Equal((0, 1), (first, second));
        await using var db = _site.NewDb();
        var admin = await db.Users.SingleAsync(Ct);
        Assert.Equal(("boss", Role.Admin, true), (admin.Login, admin.Role, admin.MustChangePassword));
    }

    [Fact]
    public async Task A_site_with_an_admin_gets_no_other_from_the_command_line()
    {
        await _site.SeedAsync(withSeason: false);

        Assert.Equal(1, await Operations.CreateFirstAdminAsync(_site.Services, "boss", "Главный", Ct));
        await using var db = _site.NewDb();
        Assert.False(await db.Users.AnyAsync(u => u.Login == "boss", Ct));
    }

    [Fact]
    public async Task An_invalid_first_admin_is_refused()
    {
        _ = _site.Server;

        Assert.Equal(1, await Operations.CreateFirstAdminAsync(_site.Services, "no spaces allowed", "Главный", Ct));
    }

    [Fact]
    public async Task The_health_check_fails_when_the_site_does_not_answer()
    {
        Assert.Equal(1, await Operations.HealthCheckAsync("http://127.0.0.1:1/health"));
    }

    [Fact]
    public async Task The_session_keys_are_kept_where_the_configuration_says()
    {
        var keys = Path.Combine(Path.GetTempPath(), "game-event-tests", "keys-" + Guid.NewGuid().ToString("N"));
        using var site = _site.WithWebHostBuilder(b => b.UseSetting("DataProtection:KeysPath", keys));
        var client = site.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        // An antiforgery token needs a key: the first one is written there
        _ = await client.GetFromJsonAsync<Accounts.AntiforgeryToken>("/api/auth/antiforgery", Ct);

        Assert.NotEmpty(Directory.GetFiles(keys, "key-*.xml"));
        Directory.Delete(keys, recursive: true);
    }
}

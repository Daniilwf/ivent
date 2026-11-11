using System.Net;
using System.Net.Http.Json;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;

namespace GameEvent.Web.Tests.Api;

/// <summary>Sessions end when the account changes; per-login throttle; headers and limits (D-72).</summary>
public sealed class WebSecurityTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Roll => $"/api/seasons/{SiteFactory.SeasonId}/roll";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Deleted_account_loses_its_live_session()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await ChangeUserAsync("vasya", u => u.IsDeleted = true);

        Assert.Equal(HttpStatusCode.Unauthorized, (await vasya.PostAsJsonAsync(Roll, new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await vasya.GetAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Changed_role_ends_the_old_session()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await ChangeUserAsync("vasya", u => u.Role = Role.Spectator);

        Assert.Equal(HttpStatusCode.Unauthorized, (await vasya.PostAsJsonAsync(Roll, new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Rotated_security_stamp_ends_the_old_session()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await ChangeUserAsync("vasya", u => u.SecurityStamp = Guid.NewGuid().ToString("N"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await vasya.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Repeated_failures_for_one_login_throttle_it_even_with_the_right_password()
    {
        var client = await _site.AnonymousAsync();
        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", new { login = "petya", password = "guess-" + i }, Ct);
        }

        var response = await client.PostAsJsonAsync("/api/auth/login", new { login = "petya", password = SiteFactory.Password }, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        // The window is temporary: after it the right password works again
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(16);
        var later = await client.PostAsJsonAsync("/api/auth/login", new { login = "petya", password = SiteFactory.Password }, Ct);
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }

    [Fact]
    public async Task A_guess_spread_over_many_addresses_does_not_lock_out_a_browser_that_signed_in_before()
    {
        // SEC-2: the per-login ceiling is reached from elsewhere (the pair limit is out of the way here, so every attempt
        // counts towards the ceiling only); the owner's browser that signed in before still gets in
        using var site = _site.WithWebHostBuilder(b =>
        {
            b.UseSetting("Security:LoginFailuresPerLogin", "3");
            b.UseSetting("Security:LoginFailuresPerAddress", "100");
        });
        var owner = site.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(owner);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/auth/login", new { login = "petya", password = SiteFactory.Password }, Ct)).StatusCode);
        await SiteFactory.RefreshCsrfAsync(owner);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/auth/logout", null, Ct)).StatusCode);
        await SiteFactory.RefreshCsrfAsync(owner);

        var guesser = site.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(guesser);
        for (var i = 0; i < 3; i++)
        {
            await guesser.PostAsJsonAsync("/api/auth/login", new { login = "petya", password = "guess-" + i }, Ct);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await guesser.PostAsJsonAsync("/api/auth/login", new { login = "petya", password = SiteFactory.Password }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/auth/login", new { login = "petya", password = SiteFactory.Password }, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_browser_that_signed_in_before_a_password_change_is_held_by_the_ceiling_again()
    {
        // SEC-4: the device cookie is tied to the account's security stamp; once the password changes (or the account is
        // deleted and the login reused) a browser that got in with the old password is a stranger again
        using var site = _site.WithWebHostBuilder(b =>
        {
            b.UseSetting("Security:LoginFailuresPerLogin", "3");
            b.UseSetting("Security:LoginFailuresPerAddress", "100");
        });
        var former = await KnownBrowserAsync(site, "petya");
        await ChangeUserAsync("petya", u => u.SecurityStamp = Guid.NewGuid().ToString("N"));

        await ReachCeilingAsync(site, "petya");

        Assert.Equal(HttpStatusCode.TooManyRequests, (await former.PostAsJsonAsync("/api/auth/login", new { login = "petya", password = SiteFactory.Password }, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_device_cookie_past_its_lifetime_is_a_stranger()
    {
        using var site = _site.WithWebHostBuilder(b =>
        {
            b.UseSetting("Security:LoginFailuresPerLogin", "3");
            b.UseSetting("Security:LoginFailuresPerAddress", "100");
        });
        var owner = await KnownBrowserAsync(site, "petya");
        _site.Clock.UtcNow = _site.Clock.UtcNow.Add(LoginDevices.Lifetime).AddDays(1);

        await ReachCeilingAsync(site, "petya");

        Assert.Equal(HttpStatusCode.TooManyRequests, (await owner.PostAsJsonAsync("/api/auth/login", new { login = "petya", password = SiteFactory.Password }, Ct)).StatusCode);
    }

    private static async Task<HttpClient> KnownBrowserAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> site, string login)
    {
        var browser = site.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(browser);
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsJsonAsync("/api/auth/login", new { login, password = SiteFactory.Password }, Ct)).StatusCode);
        await SiteFactory.RefreshCsrfAsync(browser);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/api/auth/logout", null, Ct)).StatusCode);
        await SiteFactory.RefreshCsrfAsync(browser);
        return browser;
    }

    private static async Task ReachCeilingAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> site, string login)
    {
        var guesser = site.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(guesser);
        for (var i = 0; i < 3; i++)
        {
            await guesser.PostAsJsonAsync("/api/auth/login", new { login, password = "guess-" + i }, Ct);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await guesser.PostAsJsonAsync("/api/auth/login", new { login, password = SiteFactory.Password }, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_browser_known_for_one_login_is_a_stranger_to_another()
    {
        using var site = _site.WithWebHostBuilder(b =>
        {
            b.UseSetting("Security:LoginFailuresPerLogin", "3");
            b.UseSetting("Security:LoginFailuresPerAddress", "100");
        });
        var vasya = site.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(vasya);
        Assert.Equal(HttpStatusCode.OK, (await vasya.PostAsJsonAsync("/api/auth/login", new { login = "vasya", password = SiteFactory.Password }, Ct)).StatusCode);
        await SiteFactory.RefreshCsrfAsync(vasya);
        for (var i = 0; i < 3; i++)
        {
            await vasya.PostAsJsonAsync("/api/auth/login", new { login = "petya", password = "guess-" + i }, Ct);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await vasya.PostAsJsonAsync("/api/auth/login", new { login = "petya", password = SiteFactory.Password }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var client = await _site.AnonymousAsync();

        var response = await client.GetAsync("/health", Ct);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Single(response.Headers.GetValues("Referrer-Policy"));
    }

    [Fact]
    public async Task Oversized_api_body_is_refused()
    {
        var client = await _site.AnonymousAsync();
        var huge = new string('x', (int)WebSecurity.ApiBodyLimitBytes + 1);

        var response = await client.PostAsJsonAsync("/api/auth/login", new { login = "vasya", password = huge }, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public void Rate_limit_groups_ipv6_clients_by_their_64_prefix(string address, string key)
    {
        Assert.Equal(key, WebSecurity.ClientKey(IPAddress.Parse(address)));
    }

    private async Task ChangeUserAsync(string login, Action<UserRecord> change)
    {
        await using var db = _site.NewDb();
        var user = await db.Users.FindAsync([_site.Users[login]], Ct);
        change(user!);
        await db.SaveChangesAsync(Ct);
    }
}

/// <summary>Outside Development and Test the API description is not served.</summary>
public sealed class ProductionSurfaceTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new(loginAttemptsPerMinute: 10, environment: "Production");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task OpenApi_document_is_not_served_in_production()
    {
        var client = _site.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Production_cookies_are_secure_and_host_prefixed()
    {
        // Production runs behind Caddy over HTTPS
        var client = _site.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        var response = await client.GetAsync("/api/auth/antiforgery", TestContext.Current.CancellationToken);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-ge.csrf=", cookie, StringComparison.Ordinal);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }
}

using System.Net;
using System.Net.Http.Json;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Web.Accounts;

namespace GameEvent.Web.Tests.Api;

/// <summary>Sign-in (D-26): success, wrong credentials, CSRF, rate limit (TEST_MATRIX A1–A4 groundwork).</summary>
public sealed class AccountApiTests : IAsyncLifetime
{
    private static readonly System.Text.Json.JsonSerializerOptions s_json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
    };

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Login_with_the_right_password_signs_in()
    {
        var client = await _site.AnonymousAsync();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { login = "Vasya ", password = SiteFactory.Password }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await client.GetFromJsonAsync<CurrentUser>("/api/auth/me", s_json, Ct);
        Assert.Equal(_site.Users["vasya"], me!.Id);
        Assert.Equal(Role.Player, me.Role);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("ge.session=", StringComparison.Ordinal)
            && c.Contains("httponly", StringComparison.OrdinalIgnoreCase)
            && c.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("vasya", "wrong-password")]
    [InlineData("nobody", SiteFactory.Password)]
    public async Task Wrong_login_or_password_is_unauthorized(string login, string password)
    {
        var client = await _site.AnonymousAsync();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { login, password }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Empty_credentials_are_invalid_input()
    {
        var client = await _site.AnonymousAsync();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { login = "", password = "" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_without_the_antiforgery_token_is_refused()
    {
        var client = _site.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { login = "vasya", password = SiteFactory.Password }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("csrf.invalid", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleted_account_cannot_sign_in()
    {
        await using (var db = _site.NewDb())
        {
            var user = await db.Users.FindAsync([_site.Users["petya"]], Ct);
            user!.IsDeleted = true;
            await db.SaveChangesAsync(Ct);
        }

        var client = await _site.AnonymousAsync();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { login = "petya", password = SiteFactory.Password }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        var client = await _site.SignedInAsync("vasya");

        var response = await client.PostAsync("/api/auth/logout", null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }
}

/// <summary>Brute force: too many login attempts from one address are throttled.</summary>
public sealed class LoginRateLimitTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new(loginAttemptsPerMinute: 3);

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Fourth_attempt_within_a_minute_is_throttled()
    {
        var client = await _site.AnonymousAsync();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/auth/login", new { login = "vasya", password = "guess-" + i }, TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], statuses);
    }
}

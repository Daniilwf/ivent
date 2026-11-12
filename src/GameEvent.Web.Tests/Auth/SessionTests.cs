using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Web.Realtime;
using GameEvent.Web.Tests.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GameEvent.Web.Tests.Auth;

/// <summary>
/// Sessions (D-464 (1), D-466…D-469): every sign-in is a session on the server; signing out ends it, so a copy of the
/// cookie stops working; the owner sees their devices and ends one or the others; a password change keeps only this
/// device; the admin signs a player out everywhere; ended sessions close their hub connections; the scheduler drops
/// the rows of ended and expired ones; a cookie from before sessions signs in again.
/// </summary>
public sealed class SessionTests : IAsyncLifetime
{
    private static readonly Uri s_base = new("http://localhost/");
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync(withSeason: false);

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- Sign-in and sign-out ----

    [Fact]
    public async Task A_sign_in_creates_a_session_of_this_device()
    {
        var vasya = await DeviceAsync("vasya", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/130.0");

        var sessions = await SessionsAsync(vasya);
        var only = Assert.Single(sessions.EnumerateArray());
        Assert.True(only.GetProperty("current").GetBoolean());
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/130.0", only.GetProperty("userAgent").GetString());
        Assert.Equal(_site.Time.GetUtcNow(), only.GetProperty("createdAt").GetDateTimeOffset());

        await using var db = _site.NewDb();
        var row = await db.Sessions.SingleAsync(Ct);
        Assert.Equal(_site.Users["vasya"], row.UserId);
        Assert.Equal(_site.Time.GetUtcNow().AddDays(30), row.ExpiresAt);
        Assert.Null(row.RevokedAt);
    }

    [Fact]
    public async Task Each_sign_in_is_a_new_session_with_a_new_id()
    {
        var first = await DeviceAsync("vasya");
        var second = await DeviceAsync("vasya");

        var ids = (await SessionsAsync(second)).EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Equal(2, (await SessionsAsync(first)).GetArrayLength());
    }

    [Fact]
    public async Task Signing_in_again_in_the_same_browser_ends_its_previous_session()
    {
        // SES-1: the browser's old session would stay live, unseen, for 30 days
        var vasya = await DeviceAsync("vasya");
        var copy = CopyOf(vasya);

        var again = await vasya.Client.PostAsJsonAsync("/api/auth/login", new { login = "petya", password = SiteFactory.Password }, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await copy.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        await using var db = _site.NewDb();
        Assert.NotNull((await db.Sessions.SingleAsync(s => s.UserId == _site.Users["vasya"], Ct)).RevokedAt);
    }

    [Fact]
    public async Task Sign_out_ends_the_session_and_a_copy_of_the_cookie_made_before_gets_401()
    {
        var vasya = await DeviceAsync("vasya");
        var copy = CopyOf(vasya);
        Assert.Equal(HttpStatusCode.OK, (await copy.Client.GetAsync("/api/auth/me", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await vasya.Client.PostAsync("/api/auth/logout", null, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await copy.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await vasya.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        await using var db = _site.NewDb();
        Assert.Equal(_site.Time.GetUtcNow(), (await db.Sessions.SingleAsync(Ct)).RevokedAt);
    }

    [Fact]
    public async Task Signing_out_on_one_device_leaves_the_others_signed_in()
    {
        var phone = await DeviceAsync("vasya");
        var desktop = await DeviceAsync("vasya");

        await phone.Client.PostAsync("/api/auth/logout", null, Ct);

        Assert.Equal(HttpStatusCode.OK, (await desktop.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Single((await SessionsAsync(desktop)).EnumerateArray());
    }

    [Fact]
    public async Task The_log_records_the_start_and_the_end_of_a_session()
    {
        var vasya = await DeviceAsync("vasya");
        await vasya.Client.PostAsync("/api/auth/logout", null, Ct);

        await using var db = _site.NewDb();
        var types = await db.Events.Where(e => e.SeasonId == Guid.Empty).OrderBy(e => e.Sequence).Select(e => e.Type).ToListAsync(Ct);
        Assert.Equal(["session-started", "sessions-ended"], types);
    }

    [Fact]
    public async Task An_account_keeps_at_most_twenty_live_sessions_and_the_oldest_ends()
    {
        // SES-2: a script signing in without the cookie would pile up live rows for 30 days each
        var first = await DeviceAsync("vasya");
        var firstId = (await SessionsAsync(first))[0].GetProperty("id").GetGuid();
        _site.Time.Advance(TimeSpan.FromSeconds(1));
        var devices = new List<Device>();
        for (var i = 0; i < 20; i++)
        {
            devices.Add(await DeviceAsync("vasya"));
            _site.Time.Advance(TimeSpan.FromSeconds(1));
        }

        await using var db = _site.NewDb();
        var vasyaId = _site.Users["vasya"];
        Assert.Equal(20, await db.Sessions.CountAsync(s => s.UserId == vasyaId && s.RevokedAt == null, Ct));
        Assert.NotNull((await db.Sessions.SingleAsync(s => s.Id == firstId, Ct)).RevokedAt);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await devices[0].Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Contains(await db.Events.Where(e => e.Type == "sessions-ended").Select(e => e.Data).ToListAsync(Ct), d => d.Contains("\"limit\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sign_out_without_the_pages_token_ends_nothing()
    {
        var vasya = await DeviceAsync("vasya");
        var forged = CopyOf(vasya, withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, (await forged.Client.PostAsync("/api/auth/logout", null, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await vasya.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("reset", "accountChanged")]
    [InlineData("role", "accountChanged")]
    [InlineData("delete", "accountChanged")]
    [InlineData("ownPassword", "passwordChanged")]
    public async Task Account_changes_log_the_sessions_they_end(string action, string reason)
    {
        var phone = await DeviceAsync("vasya");
        var desktop = await DeviceAsync("vasya");
        var vasyaId = _site.Users["vasya"];
        if (action == "ownPassword")
        {
            (await desktop.Client.PostAsJsonAsync("/api/auth/password", new { commandId = Guid.NewGuid(), currentPassword = SiteFactory.Password, newPassword = "новый-пароль-1" }, Ct)).EnsureSuccessStatusCode();
        }
        else
        {
            var admin = await _site.SignedInAsync("admin");
            var path = action switch
            {
                "reset" => $"/api/admin/accounts/{vasyaId}/reset-password",
                "delete" => $"/api/admin/accounts/{vasyaId}/delete",
                _ => $"/api/admin/accounts/{vasyaId}",
            };
            (await admin.PostAsJsonAsync(path, new { commandId = Guid.NewGuid(), name = "vasya", role = "spectator" }, Ct)).EnsureSuccessStatusCode();
        }

        await using var db = _site.NewDb();
        var ended = await db.Events.Where(e => e.Type == "sessions-ended").Select(e => e.Data).ToListAsync(Ct);
        var data = Assert.Single(ended);
        Assert.Contains($"\"{reason}\"", data, StringComparison.Ordinal);
        var revoked = await db.Sessions.Where(s => s.UserId == vasyaId && s.RevokedAt != null).Select(s => s.Id).ToListAsync(Ct);
        Assert.Equal(action == "ownPassword" ? 1 : 2, revoked.Count);
        Assert.All(revoked, id => Assert.Contains(id.ToString(), data, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Signing_in_and_out_works_while_the_site_only_reads()
    {
        _site.Services.GetRequiredService<Infrastructure.Site.MaintenanceMode>().TurnOn();
        var vasya = await DeviceAsync("vasya");
        var copy = CopyOf(vasya);
        Assert.Equal(HttpStatusCode.OK, (await copy.Client.GetAsync("/api/auth/me", Ct)).StatusCode);

        await vasya.Client.PostAsync("/api/auth/logout", null, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, (await copy.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    // ---- The owner's list ----

    [Fact]
    public async Task Ending_the_other_devices_keeps_this_one()
    {
        var phone = await DeviceAsync("vasya");
        var laptop = await DeviceAsync("vasya");
        var desktop = await DeviceAsync("vasya");
        var petya = await DeviceAsync("petya");

        var response = await desktop.Client.PostAsJsonAsync("/api/auth/sessions/end-others", new { commandId = Guid.NewGuid() }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("ended").GetInt32());

        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await desktop.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await petya.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Ending_one_session_signs_out_only_that_device()
    {
        var phone = await DeviceAsync("vasya");
        var desktop = await DeviceAsync("vasya");
        var phoneId = (await SessionsAsync(phone)).EnumerateArray().Single(s => s.GetProperty("current").GetBoolean()).GetProperty("id").GetGuid();

        var response = await desktop.Client.PostAsJsonAsync($"/api/auth/sessions/{phoneId}/end", new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await desktop.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Ending_this_very_session_from_the_list_signs_out_here()
    {
        var vasya = await DeviceAsync("vasya");
        var id = (await SessionsAsync(vasya))[0].GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await vasya.Client.PostAsJsonAsync($"/api/auth/sessions/{id}/end", new { commandId = Guid.NewGuid() }, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await vasya.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Someone_elses_session_and_an_unknown_one_answer_404_and_stay()
    {
        var petya = await DeviceAsync("petya");
        var petyaId = (await SessionsAsync(petya))[0].GetProperty("id").GetGuid();
        var vasya = await DeviceAsync("vasya");

        var foreign = await vasya.Client.PostAsJsonAsync($"/api/auth/sessions/{petyaId}/end", new { commandId = Guid.NewGuid() }, Ct);
        var unknown = await vasya.Client.PostAsJsonAsync($"/api/auth/sessions/{Guid.NewGuid()}/end", new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await BodyAsync(foreign), await BodyAsync(unknown));
        Assert.Equal(HttpStatusCode.OK, (await petya.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_list_shows_only_my_live_sessions()
    {
        var phone = await DeviceAsync("vasya");
        var desktop = await DeviceAsync("vasya");
        await DeviceAsync("petya");
        await phone.Client.PostAsync("/api/auth/logout", null, Ct);

        var list = await SessionsAsync(desktop);
        Assert.Single(list.EnumerateArray());
        Assert.True(list[0].GetProperty("current").GetBoolean());
    }

    [Fact]
    public async Task Ending_sessions_needs_a_command_id_the_antiforgery_token_and_a_sign_in()
    {
        var vasya = await DeviceAsync("vasya");
        var anonymous = await _site.AnonymousAsync();
        var noToken = _site.CreateClient(new() { HandleCookies = true });

        Assert.Equal(HttpStatusCode.BadRequest, (await vasya.Client.PostAsJsonAsync("/api/auth/sessions/end-others", new { commandId = Guid.Empty }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await vasya.Client.PostAsJsonAsync($"/api/auth/sessions/{Guid.NewGuid()}/end", new { commandId = Guid.Empty }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/sessions", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/sessions/end-others", new { commandId = Guid.NewGuid() }, Ct)).StatusCode);

        // The cookie alone, without the page's token, ends nothing (CSRF)
        var copy = CopyOf(vasya, withToken: false);
        var forged = await copy.Client.PostAsJsonAsync("/api/auth/sessions/end-others", new { commandId = Guid.NewGuid() }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await noToken.GetAsync("/api/auth/sessions", Ct)).StatusCode);
    }

    // ---- Password and admin ----

    [Fact]
    public async Task A_password_change_keeps_this_device_and_ends_the_others()
    {
        var phone = await DeviceAsync("vasya");
        var desktop = await DeviceAsync("vasya");

        var changed = await desktop.Client.PostAsJsonAsync(
            "/api/auth/password", new { commandId = Guid.NewGuid(), currentPassword = SiteFactory.Password, newPassword = "новый-пароль-1" }, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await desktop.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Single((await SessionsAsync(desktop)).EnumerateArray());
    }

    [Fact]
    public async Task The_admin_signs_a_player_out_everywhere_and_sees_the_count()
    {
        var phone = await DeviceAsync("vasya");
        var desktop = await DeviceAsync("vasya");
        var petya = await DeviceAsync("petya");
        var admin = await _site.SignedInAsync("admin");
        var vasyaId = _site.Users["vasya"];
        Assert.Equal(2, await SessionCountAsync(admin, vasyaId));

        var response = await admin.PostAsJsonAsync($"/api/admin/accounts/{vasyaId}/end-sessions", new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("account").GetProperty("sessions").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await desktop.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await petya.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Equal(0, await SessionCountAsync(admin, vasyaId));

        // The password stays: the player signs in again
        Assert.Equal(HttpStatusCode.OK, (await (await DeviceAsync("vasya")).Client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Only_the_admin_signs_others_out_everywhere()
    {
        var petya = await DeviceAsync("petya");
        var vasya = await _site.SignedInAsync("vasya");
        var zritel = await _site.SignedInAsync("zritel");
        var anonymous = await _site.AnonymousAsync();
        var path = $"/api/admin/accounts/{_site.Users["petya"]}/end-sessions";

        Assert.Equal(HttpStatusCode.Forbidden, (await vasya.PostAsJsonAsync(path, new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await zritel.PostAsJsonAsync(path, new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path, new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await petya.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Ending_the_sessions_of_an_unknown_account_is_404_and_needs_a_command_id()
    {
        var admin = await _site.SignedInAsync("admin");

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/admin/accounts/{Guid.NewGuid()}/end-sessions", new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/admin/accounts/{_site.Users["vasya"]}/end-sessions", new { commandId = Guid.Empty }, Ct)).StatusCode);
    }

    public static TheoryData<string> AdminChanges() => ["reset", "role", "delete"];

    [Theory]
    [MemberData(nameof(AdminChanges))]
    public async Task A_reset_a_role_change_or_a_deletion_ends_every_session_of_the_account(string action)
    {
        await DeviceAsync("vasya");
        await DeviceAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        var vasyaId = _site.Users["vasya"];

        var path = action switch
        {
            "reset" => $"/api/admin/accounts/{vasyaId}/reset-password",
            "delete" => $"/api/admin/accounts/{vasyaId}/delete",
            _ => $"/api/admin/accounts/{vasyaId}",
        };
        var response = await admin.PostAsJsonAsync(path, new { commandId = Guid.NewGuid(), name = "vasya", role = "spectator" }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = _site.NewDb();
        Assert.All(await db.Sessions.Where(s => s.UserId == vasyaId).ToListAsync(Ct), s => Assert.Equal(_site.Time.GetUtcNow(), s.RevokedAt));
        Assert.Equal(0, await SessionCountAsync(admin, vasyaId));
    }

    // ---- Time ----

    [Fact]
    public async Task A_sign_in_unused_for_30_days_ends()
    {
        var vasya = await DeviceAsync("vasya");

        _site.Time.Advance(TimeSpan.FromDays(30).Add(TimeSpan.FromMinutes(1)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await vasya.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task An_expired_session_signs_out_even_with_a_valid_cookie()
    {
        var vasya = await DeviceAsync("vasya");
        await using (var db = _site.NewDb())
        {
            var row = await db.Sessions.SingleAsync(Ct);
            row.ExpiresAt = _site.Time.GetUtcNow().AddSeconds(-1);
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await vasya.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Moving_the_game_clock_by_months_keeps_everyone_signed_in()
    {
        // D-469: the test copy's clock moves the game, not the sessions
        var vasya = await DeviceAsync("vasya");

        _site.Clock.Advance(TimeSpan.FromDays(90));

        Assert.Equal(HttpStatusCode.OK, (await vasya.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Visits_move_the_end_of_the_session()
    {
        var vasya = await DeviceAsync("vasya");

        _site.Time.Advance(TimeSpan.FromDays(20));
        Assert.Equal(HttpStatusCode.OK, (await vasya.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        _site.Time.Advance(TimeSpan.FromDays(20));

        Assert.Equal(HttpStatusCode.OK, (await vasya.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
        await using var db = _site.NewDb();
        Assert.Equal(_site.Time.GetUtcNow().AddDays(30), (await db.Sessions.SingleAsync(Ct)).ExpiresAt);
    }

    [Fact]
    public async Task The_last_visit_is_written_at_most_every_five_minutes()
    {
        var vasya = await DeviceAsync("vasya");
        var signedInAt = _site.Time.GetUtcNow();

        _site.Time.Advance(TimeSpan.FromMinutes(4));
        await vasya.Client.GetAsync("/api/auth/me", Ct);
        Assert.Equal(signedInAt, await LastSeenAsync());

        _site.Time.Advance(TimeSpan.FromMinutes(2));
        await vasya.Client.GetAsync("/api/auth/me", Ct);
        Assert.Equal(_site.Time.GetUtcNow(), await LastSeenAsync());

        async Task<DateTimeOffset> LastSeenAsync()
        {
            await using var db = _site.NewDb();
            return (await db.Sessions.SingleAsync(Ct)).LastSeenAt;
        }
    }

    [Fact]
    public async Task The_scheduler_drops_ended_and_expired_sessions_and_keeps_live_ones()
    {
        var ended = await DeviceAsync("vasya");
        await ended.Client.PostAsync("/api/auth/logout", null, Ct);
        await DeviceAsync("petya");
        _site.Time.Advance(TimeSpan.FromDays(31));
        var live = await DeviceAsync("masha");

        await _site.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<DeadlineScheduler>().Single().TickAsync(Ct);

        await using var db = _site.NewDb();
        var left = await db.Sessions.ToListAsync(Ct);
        Assert.Equal(_site.Users["masha"], Assert.Single(left).UserId);
        Assert.Equal(HttpStatusCode.OK, (await live.Client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    // ---- Old cookies ----

    [Fact]
    public async Task A_cookie_from_before_sessions_is_signed_out()
    {
        var vasyaId = _site.Users["vasya"];
        string stamp;
        await using (var db = _site.NewDb())
        {
            stamp = (await db.Users.SingleAsync(u => u.Id == vasyaId, Ct)).SecurityStamp;
        }

        // Exactly what the site wrote before sessions: the account, its role and its stamp, no session
        var options = _site.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, vasyaId.ToString()),
                new Claim(ClaimTypes.Name, "vasya"),
                new Claim(ClaimTypes.Role, "Player"),
                new Claim(Hosting.WebSecurity.StampClaim, stamp),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties(), CookieAuthenticationDefaults.AuthenticationScheme);
        var client = _site.CreateClient(new() { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request, Ct)).StatusCode);
    }

    // ---- Hub ----

    [Fact]
    public async Task An_ended_session_closes_its_hub_connections_and_only_them()
    {
        var phone = await DeviceAsync("vasya");
        var desktop = await DeviceAsync("vasya");
        var hubs = _site.Services.GetRequiredService<HubSessions>();
        await using var phoneHub = await ConnectAsync(phone);
        await using var desktopHub = await ConnectAsync(desktop);
        var phoneClosed = Closed(phoneHub);
        var desktopClosed = Closed(desktopHub);
        await WaitAsync(() => hubs.CountFor(_site.Users["vasya"]) == 2);

        await desktop.Client.PostAsJsonAsync("/api/auth/sessions/end-others", new { commandId = Guid.NewGuid() }, Ct);

        await phoneClosed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(desktopClosed.IsCompleted);
        Assert.Equal(HubConnectionState.Connected, desktopHub.State);
    }

    // ---- Helpers ----

    private sealed record Device(HttpClient Client, CookieContainer Cookies);

    private async Task<Device> DeviceAsync(string login, string? userAgent = null)
    {
        var cookies = new CookieContainer();
        var client = _site.CreateDefaultClient(s_base, new CookieContainerHandler(cookies));
        if (userAgent is not null)
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }

        await SiteFactory.RefreshCsrfAsync(client);
        var response = await client.PostAsJsonAsync("/api/auth/login", new { login, password = SiteFactory.Password }, Ct);
        response.EnsureSuccessStatusCode();
        await SiteFactory.RefreshCsrfAsync(client);
        return new Device(client, cookies);
    }

    /// <summary>Another browser with the same cookies: what a stolen copy of the cookie would be.</summary>
    private Device CopyOf(Device device, bool withToken = true)
    {
        var cookies = new CookieContainer();
        foreach (Cookie cookie in device.Cookies.GetCookies(s_base))
        {
            if (withToken || cookie.Name == "ge.session")
            {
                cookies.Add(s_base, new Cookie(cookie.Name, cookie.Value, cookie.Path));
            }
        }

        var client = _site.CreateDefaultClient(s_base, new CookieContainerHandler(cookies));
        if (withToken)
        {
            foreach (var header in device.Client.DefaultRequestHeaders.Where(h => h.Key == Hosting.Csrf.HeaderName))
            {
                client.DefaultRequestHeaders.Add(header.Key, header.Value);
            }
        }

        return new Device(client, cookies);
    }

    private static async Task<JsonElement> SessionsAsync(Device device)
    {
        var response = await device.Client.GetAsync("/api/auth/sessions", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>A problem's body without its trace id, which differs per request.</summary>
    private static async Task<string> BodyAsync(HttpResponseMessage response)
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
        body.Remove("traceId");
        return body.ToJsonString();
    }

    private static async Task<int> SessionCountAsync(HttpClient admin, Guid userId)
    {
        var accounts = await admin.GetFromJsonAsync<JsonElement>("/api/admin/accounts", Ct);
        return accounts.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == userId).GetProperty("sessions").GetInt32();
    }

    private async Task<HubConnection> ConnectAsync(Device device)
    {
        var cookie = string.Join("; ", device.Cookies.GetCookies(s_base).Select(c => $"{c.Name}={c.Value}"));
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_site.Server.BaseAddress, SeasonHub.Path.TrimStart('/')), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => _site.Server.CreateHandler();
                o.Headers["Cookie"] = cookie;
            })
            .Build();
        await connection.StartAsync(Ct);
        return connection;
    }

    private static Task Closed(HubConnection connection)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        return closed.Task;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(condition());
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Web.Realtime;
using GameEvent.Web.Tests.Api;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameEvent.Web.Tests.Auth;

/// <summary>
/// Accounts (A1, A3, D-66, D-67, D-106): the admin creates them with a temporary password shown once; its owner must
/// change it before anything else; a reset, a role change or a deletion ends the account's sessions and hub
/// connections; the last admin stays; every change is in the global log without any password.
/// </summary>
public sealed class AccountManagementTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- Creation and the temporary password ----

    [Fact]
    public async Task A_new_account_signs_in_with_the_temporary_password_and_must_change_it_first()
    {
        var admin = await _site.SignedInAsync("admin");
        var created = await CreateAsync(admin, "lyosha", "Лёша", "player");
        var temporary = created.GetProperty("temporaryPassword").GetString()!;
        Assert.Equal(12, temporary.Length);
        Assert.True(created.GetProperty("account").GetProperty("mustChangePassword").GetBoolean());

        var lyosha = await SignInAsync("lyosha", temporary);
        using (var me = JsonDocument.Parse(await lyosha.GetStringAsync("/api/auth/me", Ct)))
        {
            Assert.True(me.RootElement.GetProperty("mustChangePassword").GetBoolean());
        }

        // Nothing else before the change
        var blocked = await lyosha.GetAsync("/api/seasons/current", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("account.mustChangePassword", await CodeAsync(blocked));

        var changed = await lyosha.PostAsJsonAsync("/api/auth/password", new { commandId = Guid.NewGuid(), currentPassword = temporary, newPassword = "свой-пароль-1" }, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        // The same session goes on with the new stamp; the temporary password is gone
        await SiteFactory.RefreshCsrfAsync(lyosha);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await lyosha.GetAsync("/api/seasons/current", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TrySignInAsync("lyosha", temporary)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TrySignInAsync("lyosha", "свой-пароль-1")).StatusCode);
    }

    [Fact]
    public async Task A_repeated_creation_names_the_same_account_without_the_password()
    {
        var admin = await _site.SignedInAsync("admin");
        var commandId = Guid.NewGuid();

        var first = await CreateAsync(admin, "lyosha", "Лёша", "spectator", commandId);
        var again = await CreateAsync(admin, "lyosha", "Лёша", "spectator", commandId);

        Assert.True(again.GetProperty("duplicate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("temporaryPassword").ValueKind);
        Assert.Equal(first.GetProperty("account").GetProperty("id").GetGuid(), again.GetProperty("account").GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("VASYA", "account.loginTaken")]
    [InlineData("ва", "account.loginInvalid")]
    [InlineData("a", "account.loginInvalid")]
    [InlineData("has space", "account.loginInvalid")]
    public async Task A_taken_or_malformed_login_is_refused(string login, string code)
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync("/api/admin/accounts", new { commandId = Guid.NewGuid(), login, name = "Кто-то", role = "player" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(code, await CodeAsync(response));
    }

    [Theory]
    [InlineData("{\"commandId\":\"{id}\",\"name\":\"X\",\"role\":\"player\"}")]
    [InlineData("{\"commandId\":\"{id}\",\"login\":\"x1\",\"role\":\"player\"}")]
    [InlineData("{\"commandId\":\"{id}\",\"login\":\"x1\",\"name\":\"X\",\"role\":\"king\"}")]
    [InlineData("{\"login\":\"x1\",\"name\":\"X\",\"role\":\"player\"}")]
    public async Task Missing_or_unknown_fields_are_a_bad_request(string body)
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsync(
            "/api/admin/accounts",
            new StringContent(body.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal), System.Text.Encoding.UTF8, "application/json"),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Reset, role change, deletion ----

    [Fact]
    public async Task A_reset_gives_a_new_temporary_password_and_ends_the_sessions()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");

        var reset = await PostOkAsync(admin, $"/api/admin/accounts/{_site.Users["vasya"]}/reset-password", new { commandId = Guid.NewGuid() });
        var temporary = reset.GetProperty("temporaryPassword").GetString()!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await vasya.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TrySignInAsync("vasya", SiteFactory.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TrySignInAsync("vasya", temporary)).StatusCode);
    }

    [Fact]
    public async Task A_role_change_ends_the_sessions_and_a_rename_does_not()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");

        await PostOkAsync(admin, $"/api/admin/accounts/{_site.Users["vasya"]}", new { commandId = Guid.NewGuid(), name = "Василий", role = "player" });
        Assert.Equal(HttpStatusCode.OK, (await vasya.GetAsync("/api/auth/me", Ct)).StatusCode);

        await PostOkAsync(admin, $"/api/admin/accounts/{_site.Users["vasya"]}", new { commandId = Guid.NewGuid(), name = "Василий", role = "spectator" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await vasya.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_deleted_account_cannot_sign_in_until_restored()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        var id = _site.Users["vasya"];

        await PostOkAsync(admin, $"/api/admin/accounts/{id}/delete", new { commandId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Unauthorized, (await vasya.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TrySignInAsync("vasya", SiteFactory.Password)).StatusCode);
        var again = await admin.PostAsJsonAsync($"/api/admin/accounts/{id}/delete", new { commandId = Guid.NewGuid() }, Ct);
        Assert.Equal("account.deleted", await CodeAsync(again));

        await PostOkAsync(admin, $"/api/admin/accounts/{id}/restore", new { commandId = Guid.NewGuid() });

        // The old password stays behind: only a reset lets the account in again (security review of D8)
        Assert.Equal(HttpStatusCode.Unauthorized, (await TrySignInAsync("vasya", SiteFactory.Password)).StatusCode);
        var reset = await PostOkAsync(admin, $"/api/admin/accounts/{id}/reset-password", new { commandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, (await TrySignInAsync("vasya", reset.GetProperty("temporaryPassword").GetString()!)).StatusCode);
    }

    [Fact]
    public async Task The_last_admin_is_neither_deleted_nor_demoted()
    {
        var admin = await _site.SignedInAsync("admin");
        var id = _site.Users["admin"];

        var delete = await admin.PostAsJsonAsync($"/api/admin/accounts/{id}/delete", new { commandId = Guid.NewGuid() }, Ct);
        var demote = await admin.PostAsJsonAsync($"/api/admin/accounts/{id}", new { commandId = Guid.NewGuid(), name = "admin", role = "player" }, Ct);

        Assert.Equal("account.lastAdmin", await CodeAsync(delete));
        Assert.Equal("account.lastAdmin", await CodeAsync(demote));

        // With a second admin the first may go
        await CreateAsync(admin, "admin2", "Второй админ", "admin");
        await PostOkAsync(admin, $"/api/admin/accounts/{id}", new { commandId = Guid.NewGuid(), name = "admin", role = "player" });
    }

    [Fact]
    public async Task The_admin_sees_every_account()
    {
        var admin = await _site.SignedInAsync("admin");

        using var doc = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/accounts", Ct));

        Assert.Equal(["admin", "masha", "petya", "vasya", "zritel"], doc.RootElement.EnumerateArray().Select(a => a.GetProperty("login").GetString()).Order());
    }

    // ---- The owner's own password ----

    [Theory]
    [InlineData("short1", "account.passwordInvalid")]
    [InlineData("vasya", "account.passwordInvalid")]
    [InlineData(SiteFactory.Password, "account.passwordInvalid")]
    public async Task A_new_password_follows_the_rules(string newPassword, string code)
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync("/api/auth/password", new { commandId = Guid.NewGuid(), currentPassword = SiteFactory.Password, newPassword }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(code, await CodeAsync(response));
    }

    [Fact]
    public async Task A_repeated_password_change_is_not_confirmed()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var commandId = Guid.NewGuid();
        await PostOkAsync(vasya, "/api/auth/password", new { commandId, currentPassword = SiteFactory.Password, newPassword = "новый-пароль-1" });
        await SiteFactory.RefreshCsrfAsync(vasya);

        var again = await vasya.PostAsJsonAsync("/api/auth/password", new { commandId, currentPassword = "новый-пароль-1", newPassword = "другой-пароль-2" }, Ct);

        Assert.Equal("account.repeat", await CodeAsync(again));
        Assert.Equal(HttpStatusCode.OK, (await TrySignInAsync("vasya", "новый-пароль-1")).StatusCode);
    }

    [Fact]
    public async Task A_temporary_password_does_not_open_the_hub()
    {
        var admin = await _site.SignedInAsync("admin");
        var created = await CreateAsync(admin, "lyosha", "Лёша", "player");
        var lyosha = await SignInAsync("lyosha", created.GetProperty("temporaryPassword").GetString()!);

        var negotiate = await lyosha.PostAsync($"{SeasonHub.Path}/negotiate?negotiateVersion=1", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, negotiate.StatusCode);
    }

    [Fact]
    public async Task A_wrong_current_password_is_refused()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync("/api/auth/password", new { commandId = Guid.NewGuid(), currentPassword = "не-тот", newPassword = "новый-пароль-9" }, Ct);

        Assert.Equal("account.currentPasswordWrong", await CodeAsync(response));
    }

    [Fact]
    public async Task No_password_reaches_the_log()
    {
        var admin = await _site.SignedInAsync("admin");
        var created = await CreateAsync(admin, "lyosha", "Лёша", "player");
        var temporary = created.GetProperty("temporaryPassword").GetString()!;
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, "/api/auth/password", new { commandId = Guid.NewGuid(), currentPassword = SiteFactory.Password, newPassword = "секретный-пароль" });

        await using var db = _site.NewDb();
        var global = await db.Events.Where(e => e.SeasonId == Guid.Empty).ToListAsync(Ct);
        Assert.Contains(global, e => e.Type == "account-created");
        Assert.Contains(global, e => e.Type == "account-password-changed");
        Assert.All(global, e =>
        {
            Assert.DoesNotContain(temporary, e.Data, StringComparison.Ordinal);
            Assert.DoesNotContain("секретный-пароль", e.Data, StringComparison.Ordinal);
            Assert.DoesNotContain("passwordHash", e.Data, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ---- Roles and input ----

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Players_and_spectators_cannot_manage_accounts(string login)
    {
        var client = await _site.SignedInAsync(login);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/accounts", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/accounts", new { commandId = Guid.NewGuid(), login = "x1", name = "X", role = "admin" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/admin/accounts/{_site.Users["admin"]}/reset-password", new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_is_unauthorized()
    {
        var client = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/accounts", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/password", new { commandId = Guid.NewGuid(), currentPassword = "x", newPassword = "yyyyyyyy" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Post_without_the_antiforgery_token_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");
        admin.DefaultRequestHeaders.Remove(Hosting.Csrf.HeaderName);

        var response = await admin.PostAsJsonAsync("/api/admin/accounts", new { commandId = Guid.NewGuid(), login = "x1", name = "X", role = "player" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_account_is_not_found()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync($"/api/admin/accounts/{Guid.NewGuid()}/reset-password", new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Hub connections ----

    public static TheoryData<string> StampChanges() => ["reset", "role", "delete", "ownPassword"];

    [Theory]
    [MemberData(nameof(StampChanges))]
    public async Task A_new_stamp_closes_the_accounts_hub_connections(string action)
    {
        var cookie = await SessionCookieAsync("petya");
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_site.Server.BaseAddress, SeasonHub.Path.TrimStart('/')), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => _site.Server.CreateHandler();
                o.Headers["Cookie"] = cookie;
            })
            .Build();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await connection.StartAsync(Ct);
        var sessions = _site.Services.GetRequiredService<HubSessions>();
        var petya = _site.Users["petya"];

        // The client's start may return before the server has run OnConnectedAsync: wait for the session to be known
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sessions.CountFor(petya) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(1, sessions.CountFor(petya));

        var admin = await _site.SignedInAsync("admin");
        switch (action)
        {
            case "reset":
                await PostOkAsync(admin, $"/api/admin/accounts/{petya}/reset-password", new { commandId = Guid.NewGuid() });
                break;
            case "role":
                await PostOkAsync(admin, $"/api/admin/accounts/{petya}", new { commandId = Guid.NewGuid(), name = "petya", role = "spectator" });
                break;
            case "delete":
                await PostOkAsync(admin, $"/api/admin/accounts/{petya}/delete", new { commandId = Guid.NewGuid() });
                break;
            default:
                var own = await _site.SignedInAsync("petya");
                await PostOkAsync(own, "/api/auth/password", new { commandId = Guid.NewGuid(), currentPassword = SiteFactory.Password, newPassword = "новый-пароль-1" });
                break;
        }

        // The server aborts at once: no waiting for the next long poll, which would notice the stamp by itself
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(0, sessions.CountFor(petya));
    }

    [Fact]
    public async Task Changing_the_own_password_ends_the_accounts_other_sessions()
    {
        var phone = await _site.SignedInAsync("vasya");
        var laptop = await _site.SignedInAsync("vasya");

        await PostOkAsync(laptop, "/api/auth/password", new { commandId = Guid.NewGuid(), currentPassword = SiteFactory.Password, newPassword = "новый-пароль-1" });

        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/auth/me", Ct)).StatusCode);
        await SiteFactory.RefreshCsrfAsync(laptop);
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Players_and_spectators_cannot_change_delete_or_restore(string login)
    {
        var client = await _site.SignedInAsync(login);
        var target = _site.Users["masha"];

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/admin/accounts/{target}", new { commandId = Guid.NewGuid(), name = "X", role = "admin" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/admin/accounts/{target}/delete", new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/admin/accounts/{target}/restore", new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
    }

    public static TheoryData<string, string> BadRequests() => new()
    {
        { "/api/admin/accounts/{id}", "{\"commandId\":\"{cmd}\",\"role\":\"player\"}" },
        { "/api/admin/accounts/{id}", "{\"commandId\":\"{cmd}\",\"name\":\"X\",\"role\":\"king\"}" },
        { "/api/admin/accounts/{id}/reset-password", "{\"commandId\":\"00000000-0000-0000-0000-000000000000\"}" },
        { "/api/admin/accounts/{id}/delete", "{\"commandId\":\"00000000-0000-0000-0000-000000000000\"}" },
        { "/api/admin/accounts/{id}/restore", "{\"commandId\":\"00000000-0000-0000-0000-000000000000\"}" },
        { "/api/auth/password", "{\"commandId\":\"{cmd}\"}" },
        { "/api/auth/password", "{\"currentPassword\":\"x\",\"newPassword\":\"yyyyyyyy\"}" },
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task Missing_or_malformed_input_is_a_bad_request(string url, string body)
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsync(
            url.Replace("{id}", _site.Users["masha"].ToString(), StringComparison.Ordinal),
            new StringContent(body.Replace("{cmd}", Guid.NewGuid().ToString(), StringComparison.Ordinal), System.Text.Encoding.UTF8, "application/json"),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(" ", "player", "account.nameInvalid")]
    [InlineData("masha", "player", "account.nothingToChange")]
    public async Task A_change_that_changes_nothing_or_breaks_the_name_is_refused(string name, string role, string code)
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync($"/api/admin/accounts/{_site.Users["masha"]}", new { commandId = Guid.NewGuid(), name, role }, Ct);

        Assert.Equal(code, await CodeAsync(response));
    }

    [Fact]
    public async Task A_name_longer_than_64_characters_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync("/api/admin/accounts", new { commandId = Guid.NewGuid(), login = "long1", name = new string('я', 65), role = "player" }, Ct);

        Assert.Equal("account.nameInvalid", await CodeAsync(response));
    }

    [Fact]
    public async Task Restoring_an_account_that_is_not_deleted_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync($"/api/admin/accounts/{_site.Users["masha"]}/restore", new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal("account.notDeleted", await CodeAsync(response));
    }

    [Fact]
    public async Task A_repeated_reset_names_the_account_without_a_password()
    {
        var admin = await _site.SignedInAsync("admin");
        var commandId = Guid.NewGuid();
        var url = $"/api/admin/accounts/{_site.Users["masha"]}/reset-password";

        var first = await PostOkAsync(admin, url, new { commandId });
        var again = await PostOkAsync(admin, url, new { commandId });

        Assert.NotEqual(JsonValueKind.Null, first.GetProperty("temporaryPassword").ValueKind);
        Assert.True(again.GetProperty("duplicate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("temporaryPassword").ValueKind);
    }

    [Fact]
    public async Task A_temporary_password_refuses_actions_too()
    {
        var admin = await _site.SignedInAsync("admin");
        var reset = await PostOkAsync(admin, $"/api/admin/accounts/{_site.Users["vasya"]}/reset-password", new { commandId = Guid.NewGuid() });
        var vasya = await SignInAsync("vasya", reset.GetProperty("temporaryPassword").GetString()!);

        var roll = await vasya.PostAsJsonAsync($"/api/seasons/{SiteFactory.SeasonId}/roll", new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, roll.StatusCode);
        Assert.Equal("account.mustChangePassword", await CodeAsync(roll));
    }

    // ---- Helpers ----

    private static async Task<JsonElement> CreateAsync(HttpClient admin, string login, string name, string role, Guid? commandId = null) =>
        await PostOkAsync(admin, "/api/admin/accounts", new { commandId = commandId ?? Guid.NewGuid(), login, name, role });

    private static async Task<JsonElement> PostOkAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.Clone();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task<HttpClient> SignInAsync(string login, string password)
    {
        var client = _site.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(client);
        var response = await client.PostAsJsonAsync("/api/auth/login", new { login, password }, Ct);
        response.EnsureSuccessStatusCode();
        await SiteFactory.RefreshCsrfAsync(client);
        return client;
    }

    private async Task<HttpResponseMessage> TrySignInAsync(string login, string password)
    {
        var client = await _site.AnonymousAsync();
        return await client.PostAsJsonAsync("/api/auth/login", new { login, password }, Ct);
    }

    private async Task<string> SessionCookieAsync(string login)
    {
        var client = _site.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var tokenResponse = await client.GetAsync("/api/auth/antiforgery", Ct);
        var csrfCookie = tokenResponse.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var token = await tokenResponse.Content.ReadFromJsonAsync<Accounts.AntiforgeryToken>(Ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { login, password = SiteFactory.Password }) };
        request.Headers.Add("Cookie", csrfCookie);
        request.Headers.Add(token!.HeaderName, token.Token);
        var response = await client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ge.session=", StringComparison.Ordinal)).Split(';')[0];
    }
}

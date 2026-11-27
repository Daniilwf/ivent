using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Web.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.PlayerPreview;

/// <summary>SPEC «Просмотр глазами игрока», D-939: a scoped game read never impersonates its target or performs a game action.</summary>
public sealed partial class PlayerPreviewApiTests : IAsyncLifetime
{
    private const string ContextHeader = "X-GameEvent-Player-Preview";
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Season => $"/api/seasons/{SiteFactory.SeasonId}";
    private static string AdminSeason => $"/api/admin/seasons/{SiteFactory.SeasonId}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Admin_reads_the_selected_offer_as_the_player_without_changing_the_game_or_admin_identity()
    {
        // Given an offer belonging to one selected participant, not to the administrator or another player.
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        await PostOkAsync(vasya, $"{Season}/roll");
        var beforeGame = await ReadJsonAsync(vasya, Season);
        var beforeIdentity = await ReadJsonAsync(admin, "/api/auth/me");

        // When that administrator opens the scoped view.
        using var response = await SendScopedAsync(admin, HttpMethod.Get, Preview(_site.Players["vasya"]));
        var preview = await OkAsync(response);

        // Then every private turn field agrees with the real player's view, without a login/session switch.
        Assert.Equal(_site.Players["vasya"], preview.GetProperty("playerId").GetGuid());
        Assert.Equal("vasya", preview.GetProperty("playerName").GetString());
        Assert.Equal(beforeGame.GetProperty("me").GetRawText(), preview.GetProperty("season").GetProperty("me").GetRawText());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        AssertNoAccountProperties(preview);
        Assert.Equal(beforeGame.GetRawText(), (await ReadJsonAsync(vasya, Season)).GetRawText());
        Assert.Equal(beforeIdentity.GetRawText(), (await ReadJsonAsync(admin, "/api/auth/me")).GetRawText());
        Assert.Equal(_site.Users["admin"], beforeIdentity.GetProperty("id").GetGuid());
        Assert.Equal("admin", beforeIdentity.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Preview_is_off_by_default()
    {
        using var admin = await _site.SignedInAsync("admin");

        using var response = await SendScopedAsync(admin, HttpMethod.Get, Preview(_site.Players["vasya"]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("vasya", HttpStatusCode.Forbidden)]
    [InlineData("zritel", HttpStatusCode.Forbidden)]
    public async Task A_preview_context_never_grants_another_role_access(string? login, HttpStatusCode expected)
    {
        using var host = WithPreview();
        using var client = login is null ? host.CreateClient() : await SignInAsync(host, login);

        using var response = await SendScopedAsync(client, HttpMethod.Get, Preview(_site.Players["petya"]));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task A_scoped_player_request_cannot_roll_but_the_ordinary_player_still_can()
    {
        using var host = WithPreview();
        using var vasya = await SignInAsync(host, "vasya");
        var before = await ReadJsonAsync(vasya, Season);

        using var refused = await SendScopedAsync(vasya, HttpMethod.Post, $"{Season}/roll", new { commandId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("playerPreview.readOnly", (await ReadBodyAsync(refused)).GetProperty("code").GetString());
        Assert.Equal(before.GetRawText(), (await ReadJsonAsync(vasya, Season)).GetRawText());
        await PostOkAsync(vasya, $"{Season}/roll");
        Assert.Equal(JsonValueKind.Object, (await ReadJsonAsync(vasya, Season)).GetProperty("me").GetProperty("offer").ValueKind);
    }

    [Fact]
    public async Task Read_only_context_blocks_admin_mutations_without_locking_the_normal_admin_tab()
    {
        // One cookie jar represents two tabs: only the preview request carries the extra context.
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        var before = await ReadJsonAsync(vasya, Season);
        var adjustment = new { commandId = Guid.NewGuid(), pointsDelta = 7, comment = "Ordinary admin correction" };
        var url = $"{AdminSeason}/players/{_site.Players["vasya"]}/adjust";

        using var refused = await SendScopedAsync(admin, HttpMethod.Post, url, adjustment);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("playerPreview.readOnly", (await ReadBodyAsync(refused)).GetProperty("code").GetString());
        Assert.Equal(before.GetRawText(), (await ReadJsonAsync(vasya, Season)).GetRawText());
        await PostOkAsync(admin, url, adjustment);
        var changed = await ReadJsonAsync(vasya, Season);
        Assert.Equal(7, changed.GetProperty("players").EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == _site.Players["vasya"]).GetProperty("points").GetInt32());
        Assert.Equal(_site.Users["admin"], (await ReadJsonAsync(admin, "/api/auth/me")).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_scoped_read_cannot_fall_back_to_account_data_outside_the_preview_namespace()
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");

        using var response = await SendScopedAsync(admin, HttpMethod.Get, "/api/auth/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(_site.Users["admin"].ToString(), await response.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(_site.Users["admin"], (await ReadJsonAsync(admin, "/api/auth/me")).GetProperty("id").GetGuid());
    }

    private static string Preview(Guid playerId) => $"{AdminSeason}/players/{playerId}/preview";

    private WebApplicationFactory<Program> WithPreview() =>
        _site.WithWebHostBuilder(builder => builder.UseSetting("Site:PlayerPreview", "true"));

    private static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> host, string login)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(client);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { login, password = SiteFactory.Password }, Ct);
        response.EnsureSuccessStatusCode();
        await SiteFactory.RefreshCsrfAsync(client);
        return client;
    }

    private static async Task<HttpResponseMessage> SendScopedAsync(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(ContextHeader, "1");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Ct);
        return await OkAsync(response);
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    private static void AssertNoAccountProperties(JsonElement element)
    {
        string[] forbidden = ["login", "password", "passwordHash", "securityStamp", "sessions", "sessionId", "mustChangePassword", "email"];
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, forbidden, StringComparer.OrdinalIgnoreCase);
                AssertNoAccountProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray())
            {
                AssertNoAccountProperties(value);
            }
        }
    }
}

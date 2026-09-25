using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Pool.Metadata;
using GameEvent.Web.Pool;
using GameEvent.Web.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetVips;

namespace GameEvent.Web.Tests.Pool;

/// <summary>
/// The admin's lookup of a game (D6, D-118): hours, year, Steam app and the cover stored on our server; any service that
/// fails or answers nothing leaves its part empty and names itself — the lookup itself never fails (SPEC: the site works
/// without HowLongToBeat; «Тесты: заглушки HTTP — сбой провайдера не ломает добавление игры»).
/// </summary>
public sealed class GameLookupApiTests : IAsyncLifetime
{
    private const string Url = "/api/admin/pool/lookup";
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Everything_found_comes_back_with_the_cover_stored_on_our_server()
    {
        using var site = With(
            hours: new FixedHours(12.5m),
            covers: [new FixedCover("steam", new CoverCandidate("https://shared.akamai.steamstatic.com/a/library_600x900.jpg", 2001, "2310", "steam"))],
            picture: _ => Picture());
        var admin = await SignedInAsync(site, "admin");

        var view = await OkAsync(await admin.PostAsJsonAsync(Url, new { commandId = Guid.NewGuid(), title = "Silent Hill 2" }, Ct));

        Assert.Equal((12.5m, 2001, "2310", "steam"), (view.GetProperty("hours").GetDecimal(), view.GetProperty("year").GetInt32(), view.GetProperty("steamAppId").GetString(), view.GetProperty("coverSource").GetString()));
        Assert.Empty(view.GetProperty("unavailable").EnumerateArray());
        var cover = view.GetProperty("cover");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(cover.GetProperty("url").GetString(), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_failing_hours_service_leaves_the_hours_empty_and_the_rest_found()
    {
        using var site = With(
            hours: new FailingHours(),
            covers: [new FixedCover("steam", new CoverCandidate("https://shared.akamai.steamstatic.com/a.jpg", 2001, "2310", "steam"))],
            picture: _ => Picture());
        var admin = await SignedInAsync(site, "admin");

        var view = await OkAsync(await admin.PostAsJsonAsync(Url, new { commandId = Guid.NewGuid(), title = "Silent Hill 2" }, Ct));

        Assert.Equal(JsonValueKind.Null, view.GetProperty("hours").ValueKind);
        Assert.Equal(["hltb"], view.GetProperty("unavailable").EnumerateArray().Select(u => u.GetString()));
        Assert.NotEqual(JsonValueKind.Null, view.GetProperty("cover").ValueKind);
    }

    [Fact]
    public async Task A_failing_cover_service_hands_over_to_the_next_one()
    {
        using var site = With(
            hours: new FixedHours(null),
            covers: [new FailingCover("steam"), new FixedCover("igdb", new CoverCandidate("https://images.igdb.com/igdb/image/upload/t_cover_big/co1.jpg", 2001, null, "igdb"))],
            picture: _ => Picture());
        var admin = await SignedInAsync(site, "admin");

        var view = await OkAsync(await admin.PostAsJsonAsync(Url, new { commandId = Guid.NewGuid(), title = "Silent Hill 2" }, Ct));

        Assert.Equal("igdb", view.GetProperty("coverSource").GetString());
        Assert.Equal(["steam"], view.GetProperty("unavailable").EnumerateArray().Select(u => u.GetString()));
    }

    [Fact]
    public async Task With_every_service_down_the_lookup_still_answers_with_nothing()
    {
        using var site = With(hours: new FailingHours(), covers: [new FailingCover("steam"), new FailingCover("igdb")], picture: _ => Picture());
        var admin = await SignedInAsync(site, "admin");

        var view = await OkAsync(await admin.PostAsJsonAsync(Url, new { commandId = Guid.NewGuid(), title = "Silent Hill 2" }, Ct));

        Assert.Equal(JsonValueKind.Null, view.GetProperty("cover").ValueKind);
        Assert.Equal(["hltb", "igdb", "steam"], view.GetProperty("unavailable").EnumerateArray().Select(u => u.GetString()).Order());
    }

    [Fact]
    public async Task A_cover_that_cannot_be_downloaded_is_no_cover()
    {
        using var site = With(
            hours: new FixedHours(null),
            covers: [new FixedCover("steam", new CoverCandidate("https://shared.akamai.steamstatic.com/a.jpg", 2001, "2310", "steam"))],
            picture: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var admin = await SignedInAsync(site, "admin");

        var view = await OkAsync(await admin.PostAsJsonAsync(Url, new { commandId = Guid.NewGuid(), title = "Silent Hill 2" }, Ct));

        Assert.Equal(JsonValueKind.Null, view.GetProperty("cover").ValueKind);
        Assert.Equal(2001, view.GetProperty("year").GetInt32());
        Assert.Contains("steam-cover", view.GetProperty("unavailable").EnumerateArray().Select(u => u.GetString()));
    }

    [Fact]
    public async Task A_cover_from_a_host_outside_the_list_is_not_downloaded()
    {
        var asked = 0;
        using var site = With(
            hours: new FixedHours(null),
            covers: [new FixedCover("steam", new CoverCandidate("https://evil.example/a.jpg", null, "1", "steam"))],
            picture: _ =>
            {
                asked++;
                return Picture();
            });
        var admin = await SignedInAsync(site, "admin");

        var view = await OkAsync(await admin.PostAsJsonAsync(Url, new { commandId = Guid.NewGuid(), title = "Silent Hill 2" }, Ct));

        Assert.Equal(JsonValueKind.Null, view.GetProperty("cover").ValueKind);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task Nothing_found_is_not_a_failure()
    {
        using var site = With(hours: new FixedHours(null), covers: [new FixedCover("steam", null)], picture: _ => Picture());
        var admin = await SignedInAsync(site, "admin");

        var view = await OkAsync(await admin.PostAsJsonAsync(Url, new { commandId = Guid.NewGuid(), title = "Unknown Indie" }, Ct));

        Assert.Empty(view.GetProperty("unavailable").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("cover").ValueKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_lookup_needs_a_title(string title)
    {
        var admin = await _site.SignedInAsync("admin");

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Url, new { commandId = Guid.NewGuid(), title }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Url, new { commandId = Guid.Empty, title = "Portal" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Url, new { commandId = Guid.NewGuid(), title = new string('a', GameLookupEndpoints.MaxTitleLength + 1) }, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Only_the_admin_looks_up(string login)
    {
        var client = await _site.SignedInAsync(login);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Url, new { commandId = Guid.NewGuid(), title = "Portal" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_and_a_request_without_the_token_are_refused()
    {
        var anonymous = await _site.AnonymousAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Url, new { commandId = Guid.NewGuid(), title = "Portal" }, Ct)).StatusCode);

        var admin = await _site.SignedInAsync("admin");
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Url, new { commandId = Guid.NewGuid(), title = "Portal" }, Ct)).StatusCode);
    }

    // ---- Helpers ----

    private WebApplicationFactory<Program> With(IHoursProvider hours, ICoverProvider[] covers, Func<HttpRequestMessage, HttpResponseMessage> picture) =>
        _site.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton(hours);
            services.RemoveAll<ICoverProvider>();
            foreach (var cover in covers)
            {
                services.AddSingleton(cover);
            }

            services.AddSingleton(sp => new CoverDownloader(new SafeDownloader(
                new DownloadSettings { AllowedHosts = ["*.steamstatic.com", "*.igdb.com"] }, sp.GetRequiredService<FileLimits>(), new Answering(picture))));
        }));

    private static HttpResponseMessage Picture()
    {
        using var grey = Image.Black(60, 90, bands: 3) + 80;
        using var picture = grey.Cast(Enums.BandFormat.Uchar);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(picture.JpegsaveBuffer()) };
    }

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> site, string login)
    {
        var client = site.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = SiteFactory.Password }, Ct)).EnsureSuccessStatusCode();
        await SiteFactory.RefreshCsrfAsync(client);
        return client;
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private sealed class FixedHours(decimal? hours) : IHoursProvider
    {
        public Task<decimal?> FindHoursAsync(string title, CancellationToken ct) => Task.FromResult(hours);
    }

    private sealed class FailingHours : IHoursProvider
    {
        public Task<decimal?> FindHoursAsync(string title, CancellationToken ct) => throw new HttpRequestException("HowLongToBeat is down");
    }

    private sealed class FixedCover(string name, CoverCandidate? candidate) : ICoverProvider
    {
        public string Name => name;

        public Task<CoverCandidate?> FindCoverAsync(string title, CancellationToken ct) => Task.FromResult(candidate);
    }

    private sealed class FailingCover(string name) : ICoverProvider
    {
        public string Name => name;

        public Task<CoverCandidate?> FindCoverAsync(string title, CancellationToken ct) => throw new TaskCanceledException("timed out");
    }

    private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }
}

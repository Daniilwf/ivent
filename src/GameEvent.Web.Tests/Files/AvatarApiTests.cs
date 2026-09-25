using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Infrastructure.Files;
using GameEvent.Web.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NetVips;

namespace GameEvent.Web.Tests.Files;

/// <summary>
/// Avatars (SPEC «Аватарки», A5, D-117): a picture by link is downloaded safely and stored like an upload; everyone sets
/// their own avatar from their own uploads, the admin anyone's; the season view shows the players' avatars.
/// </summary>
public sealed class AvatarApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- A picture by link ----

    [Fact]
    public async Task A_gif_by_link_is_downloaded_and_stored_like_an_upload()
    {
        using var site = WithInternet(_ => Picture(Gif()));
        var vasya = await SignedInAsync(site, "vasya");

        var response = await vasya.PostAsJsonAsync("/api/files/from-url", new { commandId = Guid.NewGuid(), url = "https://media.tenor.com/abc/cat.gif" }, Ct);

        var view = await OkAsync(response);
        Assert.Equal("image/gif", view.GetProperty("mediaType").GetString());
        Assert.Equal(Gif(), await vasya.GetByteArrayAsync(view.GetProperty("url").GetString(), Ct));
    }

    [Theory]
    [InlineData("http://media.tenor.com/cat.gif", "file.urlInvalid")]
    [InlineData("https://example.com/cat.gif", "file.hostNotAllowed")]
    [InlineData("https://169.254.169.254/latest/meta-data", "file.hostNotAllowed")]
    public async Task A_link_outside_the_rules_is_refused_without_asking_it(string url, string code)
    {
        var asked = 0;
        using var site = WithInternet(_ =>
        {
            asked++;
            return Picture(Gif());
        });
        var vasya = await SignedInAsync(site, "vasya");

        var response = await vasya.PostAsJsonAsync("/api/files/from-url", new { commandId = Guid.NewGuid(), url }, Ct);

        await RefusedAsync(response, HttpStatusCode.UnprocessableEntity, code);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task Not_a_picture_behind_a_good_link_is_refused_by_its_content()
    {
        using var site = WithInternet(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><script>alert(1)</script></html>") });
        var vasya = await SignedInAsync(site, "vasya");

        var response = await vasya.PostAsJsonAsync("/api/files/from-url", new { commandId = Guid.NewGuid(), url = "https://media.tenor.com/cat.gif" }, Ct);

        await RefusedAsync(response, HttpStatusCode.UnprocessableEntity, "file.typeInvalid");
    }

    [Fact]
    public async Task A_retry_by_link_with_the_same_command_id_downloads_once()
    {
        var asked = 0;
        using var site = WithInternet(_ =>
        {
            asked++;
            return Picture(Gif());
        });
        var vasya = await SignedInAsync(site, "vasya");
        var commandId = Guid.NewGuid();

        var first = await OkAsync(await vasya.PostAsJsonAsync("/api/files/from-url", new { commandId, url = "https://media.tenor.com/cat.gif" }, Ct));
        var again = await OkAsync(await vasya.PostAsJsonAsync("/api/files/from-url", new { commandId, url = "https://media.tenor.com/cat.gif" }, Ct));

        Assert.Equal(first.GetProperty("id").GetGuid(), again.GetProperty("id").GetGuid());
        Assert.True(again.GetProperty("duplicate").GetBoolean());
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task A_link_needs_a_command_id_and_a_url()
    {
        var vasya = await _site.SignedInAsync("vasya");

        Assert.Equal(HttpStatusCode.BadRequest, (await vasya.PostAsJsonAsync("/api/files/from-url", new { commandId = Guid.Empty, url = "https://media.tenor.com/cat.gif" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await vasya.PostAsJsonAsync("/api/files/from-url", new { commandId = Guid.NewGuid(), url = " " }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_and_a_request_without_the_token_cannot_download()
    {
        var anonymous = await _site.AnonymousAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/files/from-url", new { commandId = Guid.NewGuid(), url = "https://media.tenor.com/cat.gif" }, Ct)).StatusCode);

        var vasya = await _site.SignedInAsync("vasya");
        vasya.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await vasya.PostAsJsonAsync("/api/files/from-url", new { commandId = Guid.NewGuid(), url = "https://media.tenor.com/cat.gif" }, Ct)).StatusCode);
    }

    // ---- The avatar ----

    [Fact]
    public async Task A_player_sets_his_own_upload_as_the_avatar_and_everyone_sees_it()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var fileId = await UploadAsync(vasya);

        var set = await OkAsync(await vasya.PutAsJsonAsync("/api/auth/me/avatar", new { commandId = Guid.NewGuid(), fileId }, Ct));

        Assert.Equal(fileId, set.GetProperty("avatar").GetProperty("id").GetGuid());
        using var me = JsonDocument.Parse(await vasya.GetStringAsync("/api/auth/me", Ct));
        Assert.Equal($"/api/files/{fileId}/thumbnail", me.RootElement.GetProperty("avatar").GetProperty("thumbnailUrl").GetString());

        var petya = await _site.SignedInAsync("petya");
        using var season = JsonDocument.Parse(await petya.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        var players = season.RootElement.GetProperty("players").EnumerateArray().ToList();
        Assert.Equal(fileId, players.Single(p => p.GetProperty("id").GetGuid() == _site.Players["vasya"]).GetProperty("avatar").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, players.Single(p => p.GetProperty("id").GetGuid() == _site.Players["petya"]).GetProperty("avatar").ValueKind);
    }

    [Fact]
    public async Task The_avatar_is_removed_with_no_file()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var fileId = await UploadAsync(vasya);
        await OkAsync(await vasya.PutAsJsonAsync("/api/auth/me/avatar", new { commandId = Guid.NewGuid(), fileId }, Ct));

        var removed = await OkAsync(await vasya.PutAsJsonAsync("/api/auth/me/avatar", new { commandId = Guid.NewGuid(), fileId = (Guid?)null }, Ct));

        Assert.Equal(JsonValueKind.Null, removed.GetProperty("avatar").ValueKind);
    }

    [Fact]
    public async Task Someone_elses_upload_is_not_an_avatar()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        var petyasFile = await UploadAsync(petya);

        var response = await vasya.PutAsJsonAsync("/api/auth/me/avatar", new { commandId = Guid.NewGuid(), fileId = petyasFile }, Ct);

        await RefusedAsync(response, HttpStatusCode.Conflict, "account.avatarNotYours");
    }

    [Fact]
    public async Task The_same_avatar_again_changes_nothing()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var fileId = await UploadAsync(vasya);
        await OkAsync(await vasya.PutAsJsonAsync("/api/auth/me/avatar", new { commandId = Guid.NewGuid(), fileId }, Ct));

        var response = await vasya.PutAsJsonAsync("/api/auth/me/avatar", new { commandId = Guid.NewGuid(), fileId }, Ct);

        await RefusedAsync(response, HttpStatusCode.Conflict, "account.nothingToChange");
    }

    [Fact]
    public async Task A_spectator_has_an_avatar_too()
    {
        var zritel = await _site.SignedInAsync("zritel");
        var fileId = await UploadAsync(zritel);

        await OkAsync(await zritel.PutAsJsonAsync("/api/auth/me/avatar", new { commandId = Guid.NewGuid(), fileId }, Ct));
    }

    [Fact]
    public async Task The_admin_sets_a_players_avatar_from_the_players_or_his_own_upload()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        var petya = await _site.SignedInAsync("petya");
        var vasyasFile = await UploadAsync(vasya);
        var adminsFile = await UploadAsync(admin);
        var petyasFile = await UploadAsync(petya);
        var url = $"/api/admin/accounts/{_site.Users["vasya"]}/avatar";

        await OkAsync(await admin.PutAsJsonAsync(url, new { commandId = Guid.NewGuid(), fileId = vasyasFile }, Ct));
        await OkAsync(await admin.PutAsJsonAsync(url, new { commandId = Guid.NewGuid(), fileId = adminsFile }, Ct));
        await RefusedAsync(await admin.PutAsJsonAsync(url, new { commandId = Guid.NewGuid(), fileId = petyasFile }, Ct), HttpStatusCode.Conflict, "account.avatarNotYours");
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Players_and_spectators_do_not_set_others_avatars(string login)
    {
        var client = await _site.SignedInAsync(login);

        var response = await client.PutAsJsonAsync($"/api/admin/accounts/{_site.Users["petya"]}/avatar", new { commandId = Guid.NewGuid(), fileId = (Guid?)null }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_account_is_not_found()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PutAsJsonAsync($"/api/admin/accounts/{Guid.NewGuid()}/avatar", new { commandId = Guid.NewGuid(), fileId = (Guid?)null }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_has_no_avatar_to_set_and_a_command_id_is_required()
    {
        var anonymous = await _site.AnonymousAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/auth/me/avatar", new { commandId = Guid.NewGuid(), fileId = (Guid?)null }, Ct)).StatusCode);

        var vasya = await _site.SignedInAsync("vasya");
        Assert.Equal(HttpStatusCode.BadRequest, (await vasya.PutAsJsonAsync("/api/auth/me/avatar", new { commandId = Guid.Empty, fileId = (Guid?)null }, Ct)).StatusCode);
    }

    // ---- Helpers ----

    /// <summary>The site with the downloader's network replaced: the link rules stay, the answer is the test's.</summary>
    private WebApplicationFactory<Program> WithInternet(Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        _site.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton(sp => new SafeDownloader(new DownloadSettings(), sp.GetRequiredService<FileLimits>(), new Internet(answer)));
        }));

    private sealed class Internet(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }

    private static HttpResponseMessage Picture(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static byte[] Gif()
    {
        using var black = Image.Black(20, 20) + 60;
        using var grey = black.Cast(Enums.BandFormat.Uchar);
        return grey.GifsaveBuffer();
    }

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> site, string login)
    {
        var client = site.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = SiteFactory.Password }, Ct)).EnsureSuccessStatusCode();
        await SiteFactory.RefreshCsrfAsync(client);
        return client;
    }

    private static async Task<Guid> UploadAsync(HttpClient client)
    {
        using var grey = Image.Black(30, 30, bands: 3) + 90;
        using var picture = grey.Cast(Enums.BandFormat.Uchar);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Guid.NewGuid().ToString()), "commandId");
        var file = new ByteArrayContent(picture.PngsaveBuffer());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "me.png");
        return (await OkAsync(await client.PostAsync("/api/files", form, Ct))).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task RefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(code, doc.RootElement.GetProperty("code").GetString());
    }
}

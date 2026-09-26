using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Web.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using NetVips;

namespace GameEvent.Web.Tests.BugReports;

/// <summary>
/// A bug report's screenshot (A9, D-121): a still picture of its own kind, outside the upload limit and with its own, that
/// nothing on the site shows — not an avatar, not a cover, not a proof. Every write of the reports checks the CSRF token.
/// </summary>
public sealed class BugReportScreenshotTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_screenshot_is_not_an_avatar_a_cover_or_a_proof()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var screenshot = await ScreenshotAsync(vasya);
        var admin = await _site.SignedInAsync("admin");
        var adminShot = await ScreenshotAsync(admin);

        var avatar = await vasya.PutAsJsonAsync("/api/auth/me/avatar", new { commandId = Guid.NewGuid(), fileId = screenshot }, Ct);
        var cover = await admin.PostAsJsonAsync("/api/pool", new { commandId = Guid.NewGuid(), title = "Dead Space", tags = new[] { "Horror" }, coverFileId = adminShot }, Ct);
        await PostOkAsync(vasya, Url("roll"));
        await PostOkAsync(vasya, Url("start"));
        await PostOkAsync(vasya, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        var runId = await LastRunAsync(vasya);
        var proof = await vasya.PostAsJsonAsync(Url($"runs/{runId}/proof"), new { commandId = Guid.NewGuid(), links = Array.Empty<string>(), files = new[] { screenshot } }, Ct);

        Assert.False(avatar.IsSuccessStatusCode, await avatar.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Conflict, cover.StatusCode);
        Assert.False(proof.IsSuccessStatusCode, await proof.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Screenshots_do_not_use_up_the_upload_limit_and_have_their_own()
    {
        using var strict = _site.WithWebHostBuilder(b => b.UseSetting("Files:UploadsPerDay", "1"));
        var vasya = await SignedInAsync(strict, "vasya");

        Assert.Equal(HttpStatusCode.OK, (await vasya.PostAsync("/api/bug-reports/screenshot", Form(Png(64, 48)), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await vasya.PostAsync("/api/bug-reports/screenshot", Form(Png(64, 48)), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await vasya.PostAsync("/api/files", Form(Png(64, 48)), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await vasya.PostAsync("/api/files", Form(Png(64, 48)), Ct)).StatusCode);
    }

    [Fact]
    public async Task An_animation_is_not_a_screenshot()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var answer = await vasya.PostAsync("/api/bug-reports/screenshot", Form(AnimatedGif(), "a.gif", "image/gif"), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, answer.StatusCode);
        Assert.Equal("file.notStill", await CodeAsync(answer));
    }

    [Fact]
    public async Task A_screenshot_is_at_most_three_megabytes()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var answer = await vasya.PostAsync("/api/bug-reports/screenshot", Form(new byte[(3 * 1024 * 1024) + 1]), Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, answer.StatusCode);
    }

    [Fact]
    public async Task A_spectator_sends_a_screenshot_and_anonymous_does_not()
    {
        var zritel = await _site.SignedInAsync("zritel");
        var anonymous = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.OK, (await zritel.PostAsync("/api/bug-reports/screenshot", Form(Png(64, 48)), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/bug-reports/screenshot", Form(Png(64, 48)), Ct)).StatusCode);
    }

    [Fact]
    public async Task Every_write_of_the_reports_and_the_maintenance_toggle_needs_the_csrf_token()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        foreach (var client in new[] { vasya, admin })
        {
            foreach (var header in client.DefaultRequestHeaders.Where(h => h.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase)).Select(h => h.Key).ToList())
            {
                client.DefaultRequestHeaders.Remove(header);
            }
        }

        var report = await vasya.PostAsJsonAsync("/api/bug-reports", new { commandId = Guid.NewGuid(), page = "/", text = "без токена" }, Ct);
        var screenshot = await vasya.PostAsync("/api/bug-reports/screenshot", Form(Png(64, 48)), Ct);
        var status = await admin.PutAsJsonAsync($"/api/admin/bug-reports/{Guid.NewGuid()}/status", new { commandId = Guid.NewGuid(), status = "closed" }, Ct);
        var maintenance = await admin.PutAsJsonAsync("/api/admin/maintenance", new { on = true }, Ct);

        Assert.All(new[] { report, screenshot, status, maintenance }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
    }

    // ---- Helpers ----

    private static async Task<Guid> ScreenshotAsync(HttpClient client)
    {
        var answer = await client.PostAsync("/api/bug-reports/screenshot", Form(Png(64, 48)), Ct);
        var body = await answer.Content.ReadAsStringAsync(Ct);
        Assert.True(answer.StatusCode == HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> LastRunAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        return doc.RootElement.GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetGuid();
    }

    private static async Task PostOkAsync(HttpClient client, string url, object? body = null)
    {
        var response = await client.PostAsJsonAsync(url, body ?? new { commandId = Guid.NewGuid() }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static MultipartFormDataContent Form(byte[] content, string name = "screenshot.png", string type = "image/png")
    {
        var form = new MultipartFormDataContent { { new StringContent(Guid.NewGuid().ToString()), "commandId" } };
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", name);
        return form;
    }

    private static byte[] Png(int width, int height)
    {
        using var grey = Image.Black(width, height, bands: 3) + 70;
        using var picture = grey.Cast(Enums.BandFormat.Uchar);
        return picture.PngsaveBuffer();
    }

    private static byte[] AnimatedGif()
    {
        var pages = Enumerable.Range(0, 3).Select(i => (Image.Black(20, 20) + (i * 60)).Cast(Enums.BandFormat.Uchar)).ToArray();
        using var strip = Image.Arrayjoin(pages, across: 1);
        using var paged = strip.Mutate(m =>
        {
            m.Set(GValue.GIntType, "page-height", 20);
            m.Set(GValue.ArrayIntType, "delay", Enumerable.Repeat(100, 3).ToArray());
        });
        var gif = paged.GifsaveBuffer();
        foreach (var page in pages)
        {
            page.Dispose();
        }

        return gif;
    }

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> site, string login)
    {
        var client = site.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = SiteFactory.Password }, Ct)).EnsureSuccessStatusCode();
        await SiteFactory.RefreshCsrfAsync(client);
        return client;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}

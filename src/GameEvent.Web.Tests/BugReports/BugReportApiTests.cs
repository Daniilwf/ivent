using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Web.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using NetVips;

namespace GameEvent.Web.Tests.BugReports;

/// <summary>
/// The «Сообщить о баге» button (SPEC, A9, D-121): any signed-in user sends a report with the page, a description, the
/// context the page gathered and their own screenshot; the admin reads the reports, moves them through their statuses
/// and exports them to a file.
/// </summary>
public sealed class BugReportApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_player_reports_with_context_and_a_screenshot_and_the_admin_reads_it()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var screenshot = await ScreenshotAsync(vasya);

        var id = await ReportOkAsync(vasya, Report("Кнопка «Бросить» не нажимается", screenshot));

        var report = Assert.Single(await ListAsync(await _site.SignedInAsync("admin")));
        Assert.Equal(id, report.GetProperty("id").GetGuid());
        Assert.Equal("vasya", report.GetProperty("author").GetString());
        Assert.Equal("/season", report.GetProperty("page").GetString());
        Assert.Equal("Кнопка «Бросить» не нажимается", report.GetProperty("text").GetString());
        Assert.Equal("new", report.GetProperty("status").GetString());
        Assert.Equal(_site.Clock.UtcNow, report.GetProperty("createdAt").GetDateTimeOffset());
        var context = report.GetProperty("context");
        Assert.Equal("390x844", context.GetProperty("viewport").GetString());
        Assert.Equal("POST /api/seasons/…/roll → 409", context.GetProperty("actions")[1].GetProperty("text").GetString());
        Assert.Equal("TypeError: x is undefined", context.GetProperty("errors")[0].GetProperty("text").GetString());
        Assert.Equal($"/api/files/{screenshot}", report.GetProperty("screenshot").GetProperty("url").GetString());
    }

    [Fact]
    public async Task A_spectator_and_the_admin_report_too_and_a_report_needs_no_screenshot()
    {
        await ReportOkAsync(await _site.SignedInAsync("zritel"), Report("Лента пустая"));
        await ReportOkAsync(await _site.SignedInAsync("admin"), Report("Опечатка в правилах"));

        var reports = await ListAsync(await _site.SignedInAsync("admin"));
        Assert.Equal(2, reports.Count);
        Assert.All(reports, r => Assert.Equal(JsonValueKind.Null, r.GetProperty("screenshot").ValueKind));
    }

    [Fact]
    public async Task Anonymous_does_not_report()
    {
        var anonymous = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/bug-reports", Report("Нет входа"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Someone_elses_screenshot_is_refused()
    {
        var petya = await _site.SignedInAsync("petya");
        var his = await ScreenshotAsync(petya);
        var vasya = await _site.SignedInAsync("vasya");

        var answer = await vasya.PostAsJsonAsync("/api/bug-reports", Report("Чужой скрин", his), Ct);

        Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
        Assert.Equal("bugReport.screenshotUnknown", await CodeAsync(answer));
        Assert.Empty(await ListAsync(await _site.SignedInAsync("admin")));
    }

    public static TheoryData<string> InvalidReports() =>
    [
        """{"page":"/","text":"нет id"}""",
        """{"commandId":"COMMAND","page":"/","text":"   "}""",
        """{"commandId":"COMMAND","page":"/"}""",
        """{"commandId":"COMMAND","page":"","text":"пустая страница"}""",
        """{"commandId":"COMMAND","page":"/","text":"длинная страница","context":{"viewport":"VIEWPORT"}}""",
        """{"commandId":"COMMAND","page":"/","text":"строка без текста","context":{"actions":[{"at":null}]}}""",
        """{"commandId":"COMMAND","page":"/","text":"слишком много","context":{"errors":ERRORS}}""",
    ];

    [Theory]
    [MemberData(nameof(InvalidReports))]
    public async Task An_invalid_report_is_400_and_nothing_is_stored(string body)
    {
        var vasya = await _site.SignedInAsync("vasya");
        var errors = JsonSerializer.Serialize(Enumerable.Range(0, 21).Select(i => new { at = (DateTimeOffset?)null, text = $"e{i}" }));
        var json = body.Replace("COMMAND", Guid.NewGuid().ToString(), StringComparison.Ordinal)
            .Replace("VIEWPORT", new string('x', 51), StringComparison.Ordinal)
            .Replace("ERRORS", errors, StringComparison.Ordinal);

        var answer = await vasya.PostAsync("/api/bug-reports", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Empty(await ListAsync(await _site.SignedInAsync("admin")));
    }

    [Fact]
    public async Task A_repeated_request_is_the_same_report()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var request = Report("Дважды отправил");

        var first = await ReportOkAsync(vasya, request);
        var second = await ReportOkAsync(vasya, request);

        Assert.Equal(first, second);
        Assert.Single(await ListAsync(await _site.SignedInAsync("admin")));
    }

    [Fact]
    public async Task The_admin_moves_a_report_through_its_statuses()
    {
        var id = await ReportOkAsync(await _site.SignedInAsync("vasya"), Report("Сломалось"));
        var admin = await _site.SignedInAsync("admin");

        var inWork = await admin.PutAsJsonAsync($"/api/admin/bug-reports/{id}/status", new { commandId = Guid.NewGuid(), status = "inWork" }, Ct);
        var again = await admin.PutAsJsonAsync($"/api/admin/bug-reports/{id}/status", new { commandId = Guid.NewGuid(), status = "inWork" }, Ct);
        var closed = await admin.PutAsJsonAsync($"/api/admin/bug-reports/{id}/status", new { commandId = Guid.NewGuid(), status = "closed" }, Ct);

        Assert.Equal(HttpStatusCode.OK, inWork.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("bugReport.nothingToChange", await CodeAsync(again));
        Assert.Equal("closed", (await JsonAsync(closed)).GetProperty("status").GetString());
        Assert.Single(await ListAsync(admin, "?status=closed"));
        Assert.Empty(await ListAsync(admin, "?status=new"));
    }

    [Fact]
    public async Task A_status_change_of_an_unknown_report_or_with_a_bad_status_is_refused()
    {
        var id = await ReportOkAsync(await _site.SignedInAsync("vasya"), Report("Сломалось"));
        var admin = await _site.SignedInAsync("admin");

        var unknown = await admin.PutAsJsonAsync($"/api/admin/bug-reports/{Guid.NewGuid()}/status", new { commandId = Guid.NewGuid(), status = "closed" }, Ct);
        var bad = await admin.PutAsync(
            $"/api/admin/bug-reports/{id}/status",
            new StringContent($$"""{"commandId":"{{Guid.NewGuid()}}","status":"fixed"}""", System.Text.Encoding.UTF8, "application/json"),
            Ct);
        var noId = await admin.PutAsJsonAsync($"/api/admin/bug-reports/{id}/status", new { status = "closed" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/bug-reports?status=fixed", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/bug-reports/export?status=1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noId.StatusCode);
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Only_the_admin_reads_changes_and_exports_reports(string login)
    {
        var id = await ReportOkAsync(await _site.SignedInAsync("vasya"), Report("Сломалось"));
        var client = await _site.SignedInAsync(login);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/bug-reports", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/bug-reports/export", Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"/api/admin/bug-reports/{id}/status", new { commandId = Guid.NewGuid(), status = "closed" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task The_export_is_a_file_of_the_open_reports_or_of_all()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var open = await ReportOkAsync(vasya, Report("Открытый"));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(1);
        var done = await ReportOkAsync(vasya, Report("Закрытый"));
        var admin = await _site.SignedInAsync("admin");
        await admin.PutAsJsonAsync($"/api/admin/bug-reports/{done}/status", new { commandId = Guid.NewGuid(), status = "closed" }, Ct);

        var export = await admin.GetAsync("/api/admin/bug-reports/export", Ct);
        var everything = await JsonAsync(await admin.GetAsync("/api/admin/bug-reports/export?all=true", Ct));

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("attachment", export.Content.Headers.ContentDisposition?.DispositionType);
        Assert.StartsWith("bug-reports-", export.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);
        var body = await JsonAsync(export);
        Assert.Equal(_site.Clock.UtcNow, body.GetProperty("exportedAt").GetDateTimeOffset());
        Assert.Equal([open], body.GetProperty("reports").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
        Assert.Equal([done, open], everything.GetProperty("reports").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task A_screenshot_is_stored_even_at_the_daily_upload_limit()
    {
        using var strict = _site.WithWebHostBuilder(b => b.UseSetting("Files:UploadsPerDay", "0"));
        var vasya = await SignedInAsync(strict, "vasya");

        var upload = await vasya.PostAsync("/api/files", Form(), Ct);
        var screenshot = await vasya.PostAsync("/api/bug-reports/screenshot", Form(), Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, upload.StatusCode);
        Assert.Equal(HttpStatusCode.OK, screenshot.StatusCode);
        Assert.Equal("image/webp", (await JsonAsync(screenshot)).GetProperty("mediaType").GetString());
    }

    [Fact]
    public async Task A_screenshot_that_is_not_a_picture_is_refused()
    {
        var vasya = await _site.SignedInAsync("vasya");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Guid.NewGuid().ToString()), "commandId" },
            { new ByteArrayContent("<svg onload=alert(1)>"u8.ToArray()), "file", "a.png" },
        };

        var answer = await vasya.PostAsync("/api/bug-reports/screenshot", form, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, answer.StatusCode);
    }

    [Fact]
    public async Task Reports_are_limited_per_hour()
    {
        var vasya = await _site.SignedInAsync("vasya");
        for (var i = 0; i < 10; i++)
        {
            await ReportOkAsync(vasya, Report($"Баг {i}"));
        }

        var eleventh = await vasya.PostAsJsonAsync("/api/bug-reports", Report("Ещё один"), Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
    }

    // ---- Helpers ----

    private static object Report(string text, Guid? screenshot = null) => new
    {
        commandId = Guid.NewGuid(),
        page = "/season",
        text,
        context = new
        {
            userAgent = "Mozilla/5.0 (test)",
            viewport = "390x844",
            actions = new[]
            {
                new { at = (DateTimeOffset?)new DateTimeOffset(2026, 10, 1, 11, 59, 0, TimeSpan.Zero), text = "click button[data-testid=roll] «Бросить»" },
                new { at = (DateTimeOffset?)null, text = "POST /api/seasons/…/roll → 409" },
            },
            errors = new[] { new { at = (DateTimeOffset?)null, text = "TypeError: x is undefined" } },
        },
        screenshotFileId = screenshot,
    };

    private static async Task<Guid> ReportOkAsync(HttpClient client, object report)
    {
        var answer = await client.PostAsJsonAsync("/api/bug-reports", report, Ct);
        var body = await answer.Content.ReadAsStringAsync(Ct);
        Assert.True(answer.StatusCode == HttpStatusCode.OK, $"{answer.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient admin, string query = "")
    {
        using var doc = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/bug-reports" + query, Ct));
        return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    private static async Task<Guid> ScreenshotAsync(HttpClient client)
    {
        var answer = await client.PostAsync("/api/bug-reports/screenshot", Form(), Ct);
        var body = await JsonAsync(answer);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        return body.GetProperty("id").GetGuid();
    }

    private static MultipartFormDataContent Form()
    {
        using var grey = Image.Black(64, 48, bands: 3) + 70;
        using var picture = grey.Cast(Enums.BandFormat.Uchar);
        var form = new MultipartFormDataContent { { new StringContent(Guid.NewGuid().ToString()), "commandId" } };
        var file = new ByteArrayContent(picture.PngsaveBuffer());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "screenshot.png");
        return form;
    }

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> site, string login)
    {
        var client = site.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = SiteFactory.Password }, Ct)).EnsureSuccessStatusCode();
        await SiteFactory.RefreshCsrfAsync(client);
        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.Clone();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).TryGetProperty("code", out var code) ? code.GetString() : null;
}

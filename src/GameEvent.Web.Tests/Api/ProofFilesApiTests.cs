using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NetVips;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Screenshots in a proof (D-116): the player uploads a picture (<c>POST /api/files</c>) and sends its id with the proof;
/// only his own uploads are accepted; the season view and the admin's queue show them with their links.
/// </summary>
public sealed class ProofFilesApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_screenshot_alone_proves_the_run_and_shows_in_the_view_and_the_queue()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        var fileId = await UploadAsync(vasya);

        await PostOkAsync(vasya, Url($"runs/{runId}/proof"), new { commandId = Guid.NewGuid(), links = Array.Empty<string>(), files = new[] { fileId } });

        var proof = (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("proof");
        var file = Assert.Single(proof.GetProperty("files").EnumerateArray());
        Assert.Equal((fileId, $"/api/files/{fileId}", $"/api/files/{fileId}/thumbnail"), (file.GetProperty("id").GetGuid(), file.GetProperty("url").GetString(), file.GetProperty("thumbnailUrl").GetString()));

        var admin = await _site.SignedInAsync("admin");
        using var queue = JsonDocument.Parse(await admin.GetStringAsync($"/api/admin/seasons/{SiteFactory.SeasonId}/proofs", Ct));
        var item = Assert.Single(queue.RootElement.EnumerateArray());
        Assert.Equal(fileId, Assert.Single(item.GetProperty("files").EnumerateArray()).GetProperty("id").GetGuid());

        // The admin opens the screenshot
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(file.GetProperty("url").GetString(), Ct)).StatusCode);
    }

    [Fact]
    public async Task Someone_elses_screenshot_is_refused()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        var runId = await CompletedRunAsync(vasya);
        var petyasFile = await UploadAsync(petya);

        var response = await vasya.PostAsJsonAsync(Url($"runs/{runId}/proof"), new { commandId = Guid.NewGuid(), links = Array.Empty<string>(), files = new[] { petyasFile } }, Ct);

        await AssertConflictAsync(response, "proof.invalidFile");
        Assert.Equal(JsonValueKind.Null, (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("proof").ValueKind);
    }

    [Fact]
    public async Task An_unknown_screenshot_is_refused()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        var response = await vasya.PostAsJsonAsync(Url($"runs/{runId}/proof"), new { commandId = Guid.NewGuid(), links = Array.Empty<string>(), files = new[] { Guid.NewGuid() } }, Ct);

        await AssertConflictAsync(response, "proof.invalidFile");
    }

    [Fact]
    public async Task The_same_screenshot_twice_is_refused_by_the_engine()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        var fileId = await UploadAsync(vasya);

        var response = await vasya.PostAsJsonAsync(Url($"runs/{runId}/proof"), new { commandId = Guid.NewGuid(), links = Array.Empty<string>(), files = new[] { fileId, fileId } }, Ct);

        await AssertConflictAsync(response, "proof.invalidFile");
    }

    [Fact]
    public async Task More_than_five_screenshots_are_invalid()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        var response = await vasya.PostAsJsonAsync(
            Url($"runs/{runId}/proof"), new { commandId = Guid.NewGuid(), links = Array.Empty<string>(), files = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_proof_without_files_still_works_and_shows_none()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        await PostOkAsync(vasya, Url($"runs/{runId}/proof"), new { commandId = Guid.NewGuid(), links = new[] { "https://imgur.com/a/credits" } });

        Assert.Empty((await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("proof").GetProperty("files").EnumerateArray());
    }

    // ---- Helpers ----

    private static async Task<Guid> UploadAsync(HttpClient client)
    {
        using var grey = Image.Black(40, 30, bands: 3) + 70;
        using var picture = grey.Cast(Enums.BandFormat.Uchar);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Guid.NewGuid().ToString()), "commandId");
        var file = new ByteArrayContent(picture.PngsaveBuffer());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "credits.png");
        var response = await client.PostAsync("/api/files", form, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CompletedRunAsync(HttpClient client)
    {
        await PostOkAsync(client, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(client, Url("start"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(client, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        return Guid.Parse((await MeJsonAsync(client)).GetProperty("lastCompleted").GetProperty("id").GetString()!);
    }

    private static async Task PostOkAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, body);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(code, doc.RootElement.GetProperty("code").GetString());
    }

    private static async Task<JsonElement> MeJsonAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        return doc.RootElement.GetProperty("me").Clone();
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Files;
using GameEvent.Infrastructure.Database;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetVips;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The map's background picture (SPEC «Редактор»: загрузка фона; «Карта — свободный граф клеток поверх фоновой
/// картинки»; D-277, D-278): the admin uploads it in the editor — the same checks as every upload, a still picture of its
/// own kind reduced to its own long side — puts it in the draft and publishes it with the map; players get it with the
/// season's map, a missing file is no background.
/// </summary>
public sealed class MapBackgroundApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Season => $"/api/seasons/{SiteFactory.SeasonId}";

    private static string Admin => $"/api/admin/seasons/{SiteFactory.SeasonId}/map";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- Upload ----

    [Fact]
    public async Task The_admin_uploads_a_background_reduced_to_its_long_side_as_a_picture_of_its_own_kind()
    {
        var admin = await _site.SignedInAsync("admin");

        var view = await ApiCalls.OkAsync(await admin.PostAsync($"{Admin}/background", Form(Png(4000, 1000)), Ct));

        // Files:MapBackgroundMaxLongSide is 3200 by default: a map is bigger than a proof's screenshot
        Assert.Equal(("image/webp", 3200, 800, 1), (view.GetProperty("mediaType").GetString(), view.GetProperty("width").GetInt32(), view.GetProperty("height").GetInt32(), view.GetProperty("frames").GetInt32()));
        using var file = await admin.GetAsync(view.GetProperty("url").GetString(), Ct);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("image/webp", file.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", file.Headers.CacheControl?.ToString() ?? "", StringComparison.Ordinal);
        using (var image = Image.NewFromBuffer(await file.Content.ReadAsByteArrayAsync(Ct)))
        {
            Assert.Equal((3200, 800), (image.Width, image.Height));
        }

        var id = view.GetProperty("id").GetGuid();
        using var scope = _site.Services.CreateScope();
        var record = await scope.ServiceProvider.GetRequiredService<GameEventDbContext>().Files.SingleAsync(f => f.Id == id, Ct);
        Assert.Equal(FileKind.MapBackground, record.Kind);
    }

    [Fact]
    public async Task A_background_is_not_an_avatar()
    {
        var admin = await _site.SignedInAsync("admin");
        var id = (await UploadAsync(admin, Png(300, 200))).Id;

        var avatar = await admin.PutAsJsonAsync("/api/auth/me/avatar", new { commandId = Guid.NewGuid(), fileId = id }, Ct);

        Assert.False(avatar.IsSuccessStatusCode, await avatar.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Only_the_admin_uploads_a_background(string login)
    {
        var other = await _site.SignedInAsync(login);

        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync($"{Admin}/background", Form(Png(300, 200)), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_signed_out_visitor_gets_401()
    {
        var anonymous = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"{Admin}/background", Form(Png(300, 200)), Ct)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_season_is_404()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsync($"/api/admin/seasons/{Guid.NewGuid()}/map/background", Form(Png(300, 200)), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_upload_without_a_command_id_or_a_file_is_400_and_not_a_form_is_415()
    {
        var admin = await _site.SignedInAsync("admin");
        using var noCommand = new MultipartFormDataContent { { new ByteArrayContent(Png(30, 20)), "file", "map.png" } };
        using var noFile = new MultipartFormDataContent { { new StringContent(Guid.NewGuid().ToString()), "commandId" } };
        using var json = JsonContent.Create(new { commandId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"{Admin}/background", noCommand, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"{Admin}/background", noFile, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await admin.PostAsync($"{Admin}/background", json, Ct)).StatusCode);
    }

    [Fact]
    public async Task Not_a_picture_or_an_animation_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");

        var text = await admin.PostAsync($"{Admin}/background", Form("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray(), "map.svg", "image/svg+xml"), Ct);
        var gif = await admin.PostAsync($"{Admin}/background", Form(AnimatedGif(), "map.gif", "image/gif"), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, text.StatusCode);
        Assert.Equal("file.typeInvalid", await CodeAsync(text));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, gif.StatusCode);
        Assert.Equal("file.notStill", await CodeAsync(gif));
    }

    [Fact]
    public async Task A_background_past_its_size_limit_is_413()
    {
        using var strict = _site.WithWebHostBuilder(b => b.UseSetting("Files:MapBackgroundMaxBytes", "100000"));
        var admin = await SignedInAsync(strict, "admin");

        var response = await admin.PostAsync($"{Admin}/background", Form(new byte[100_001]), Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("file.tooLarge", await CodeAsync(response));
    }

    [Fact]
    public async Task An_upload_without_the_csrf_token_is_400()
    {
        var admin = await _site.SignedInAsync("admin");
        foreach (var header in admin.DefaultRequestHeaders.Where(h => h.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase)).Select(h => h.Key).ToList())
        {
            admin.DefaultRequestHeaders.Remove(header);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"{Admin}/background", Form(Png(30, 20)), Ct)).StatusCode);
    }

    // ---- Publication ----

    [Fact]
    public async Task A_background_is_published_with_the_map_and_the_players_get_it()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchToGraphAsync(admin);
        var picture = await UploadAsync(admin, Png(1200, 800));
        var map = LineMap(Background(picture));

        var check = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{Admin}/check", map, Ct));
        Assert.True(check.GetProperty("canPublish").GetBoolean(), check.GetRawText());
        await ApiCalls.PostOkAsync(admin, $"{Admin}/publish", new { commandId = Guid.NewGuid(), map, comment = "Фон карты" });

        var season = await ApiCalls.OkAsync(await vasya.GetAsync(Season, Ct));
        var background = season.GetProperty("background");
        Assert.Equal(picture.Id, background.GetProperty("fileId").GetGuid());
        Assert.Equal((1200, 800), (background.GetProperty("width").GetInt32(), background.GetProperty("height").GetInt32()));
        Assert.Equal($"/api/files/{picture.Id}", background.GetProperty("url").GetString());
        using var file = await vasya.GetAsync(background.GetProperty("url").GetString(), Ct);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);

        // The editor reads it back as part of the map in force
        var editor = await ApiCalls.OkAsync(await admin.GetAsync(Admin, Ct));
        Assert.Equal(picture.Id, editor.GetProperty("map").GetProperty("background").GetProperty("fileId").GetGuid());
    }

    [Fact]
    public async Task A_map_without_a_background_has_none_and_removing_it_is_a_new_version()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchToGraphAsync(admin);
        var picture = await UploadAsync(admin, Png(1200, 800));
        await ApiCalls.PostOkAsync(admin, $"{Admin}/publish", new { commandId = Guid.NewGuid(), map = LineMap(Background(picture)), comment = "Фон" });

        await ApiCalls.PostOkAsync(admin, $"{Admin}/publish", new { commandId = Guid.NewGuid(), map = LineMap(), comment = "Без фона" });

        var season = await ApiCalls.OkAsync(await vasya.GetAsync(Season, Ct));
        Assert.Equal(JsonValueKind.Null, season.GetProperty("background").ValueKind);
        var editor = await ApiCalls.OkAsync(await admin.GetAsync(Admin, Ct));
        Assert.Equal(JsonValueKind.Null, editor.GetProperty("map").GetProperty("background").ValueKind);
    }

    [Fact]
    public async Task A_background_that_is_not_an_uploaded_background_of_its_size_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchToGraphAsync(admin);
        var picture = await UploadAsync(admin, Png(1200, 800));
        var upload = await ApiCalls.OkAsync(await vasya.PostAsync("/api/files", Form(Png(1200, 800)), Ct));
        var cases = new[]
        {
            Background(picture, width: 1000),
            Background((Guid.NewGuid(), 1200, 800)),
            Background((upload.GetProperty("id").GetGuid(), 1200, 800)),
        };

        foreach (var background in cases)
        {
            var check = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{Admin}/check", LineMap(background), Ct));
            Assert.False(check.GetProperty("canPublish").GetBoolean());
            var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
            Assert.Equal(("map.backgroundUnknown", "background"), (problem.GetProperty("code").GetString(), problem.GetProperty("subject").GetString()));

            var publish = await admin.PostAsJsonAsync($"{Admin}/publish", new { commandId = Guid.NewGuid(), map = LineMap(background), comment = "Фон" }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
            Assert.Contains("map.background", await publish.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_background_of_a_bad_size_is_the_engines_problem()
    {
        var admin = await _site.SignedInAsync("admin");
        await SwitchToGraphAsync(admin);

        var check = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{Admin}/check", LineMap(Background((Guid.NewGuid(), 0, 800))), Ct));

        Assert.Contains(check.GetProperty("problems").EnumerateArray(), p => p.GetProperty("code").GetString() == "map.backgroundInvalid");
    }

    [Fact]
    public async Task A_background_whose_file_is_gone_is_no_background()
    {
        // A season copied by an archive carries no files (D-105): the map keeps the reference, the screens draw the zones
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchToGraphAsync(admin);
        var picture = await UploadAsync(admin, Png(1200, 800));
        await ApiCalls.PostOkAsync(admin, $"{Admin}/publish", new { commandId = Guid.NewGuid(), map = LineMap(Background(picture)), comment = "Фон" });
        using (var scope = _site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameEventDbContext>();
            var record = await db.Files.SingleAsync(f => f.Id == picture.Id, Ct);
            record.IsDeleted = true;
            await db.SaveChangesAsync(Ct);
        }

        var season = await ApiCalls.OkAsync(await vasya.GetAsync(Season, Ct));

        Assert.Equal(JsonValueKind.Null, season.GetProperty("background").ValueKind);
    }

    // ---- Helpers ----

    private static JsonObject Background((Guid Id, int Width, int Height) picture, int? width = null) =>
        new() { ["fileId"] = picture.Id, ["width"] = width ?? picture.Width, ["height"] = picture.Height };

    /// <summary>start → c1 → finish, placed over a picture of 1200 × 800.</summary>
    private static JsonObject LineMap(JsonObject? background = null)
    {
        var map = new JsonObject
        {
            ["cells"] = new JsonArray(
                new JsonObject { ["id"] = "start", ["type"] = "start", ["x"] = 100, ["y"] = 400 },
                new JsonObject { ["id"] = "c1", ["type"] = "empty", ["x"] = 600, ["y"] = 400 },
                new JsonObject { ["id"] = "finish", ["type"] = "finish", ["x"] = 1100, ["y"] = 400 }),
            ["edges"] = new JsonArray(
                new JsonObject { ["from"] = "start", ["to"] = "c1", ["isDefaultForward"] = true, ["isPrimaryBackward"] = true },
                new JsonObject { ["from"] = "c1", ["to"] = "finish", ["isDefaultForward"] = true, ["isPrimaryBackward"] = true }),
            ["zones"] = new JsonArray(),
        };
        if (background is not null)
        {
            map["background"] = background.DeepClone();
        }

        return map;
    }

    private async Task<(Guid Id, int Width, int Height)> UploadAsync(HttpClient admin, byte[] png)
    {
        var view = await ApiCalls.OkAsync(await admin.PostAsync($"{Admin}/background", Form(png), Ct));
        return (view.GetProperty("id").GetGuid(), view.GetProperty("width").GetInt32(), view.GetProperty("height").GetInt32());
    }

    private static async Task SwitchToGraphAsync(HttpClient admin)
    {
        using var current = JsonDocument.Parse(await admin.GetStringAsync($"{Season}/rules", Ct));
        var ruleset = JsonNode.Parse(current.RootElement.GetProperty("ruleset").GetRawText())!;
        ruleset["features"]!["mapMode"] = "graph";
        var response = await admin.PutAsJsonAsync(
            $"/api/admin/seasons/{SiteFactory.SeasonId}/rules",
            new { commandId = Guid.NewGuid(), expectedVersion = current.RootElement.GetProperty("version").GetInt32(), ruleset },
            Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static MultipartFormDataContent Form(byte[] content, string name = "map.png", string type = "image/png")
    {
        var form = new MultipartFormDataContent { { new StringContent(Guid.NewGuid().ToString()), "commandId" } };
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", name);
        return form;
    }

    private static byte[] Png(int width, int height)
    {
        using var grey = Image.Black(width, height, bands: 3) + 90;
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

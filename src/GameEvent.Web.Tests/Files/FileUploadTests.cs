using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameEvent.Web.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using NetVips;

namespace GameEvent.Web.Tests.Files;

/// <summary>
/// Uploads (SPEC «Файлы», «Трудности реализации» — загрузка файлов, D-25, D-108): the type by the content, size and pixel
/// limits, pictures re-encoded to WebP without metadata, GIFs kept as they are with an animated thumbnail, served only
/// as pictures and only to signed-in users; a retry stores nothing twice; a daily limit per user.
/// </summary>
public sealed class FileUploadTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync(withSeason: false);

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- What is stored ----

    [Fact]
    public async Task A_png_is_stored_as_webp_no_larger_than_the_long_side_with_a_small_thumbnail()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await UploadAsync(vasya, Png(4000, 1000), "screen.png");

        var view = await StoredAsync(response);
        Assert.Equal(("image/webp", 2560, 640, 1), (view.GetProperty("mediaType").GetString(), view.GetProperty("width").GetInt32(), view.GetProperty("height").GetInt32(), view.GetProperty("frames").GetInt32()));
        using var file = await vasya.GetAsync(view.GetProperty("url").GetString(), Ct);
        Assert.Equal("image/webp", file.Content.Headers.ContentType?.MediaType);
        using (var image = Image.NewFromBuffer(await file.Content.ReadAsByteArrayAsync(Ct)))
        {
            Assert.Equal((2560, 640), (image.Width, image.Height));
            Assert.StartsWith("webpload", (string)image.Get("vips-loader"), StringComparison.Ordinal);
        }

        using var thumbnail = await vasya.GetAsync(view.GetProperty("thumbnailUrl").GetString(), Ct);
        using var small = Image.NewFromBuffer(await thumbnail.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal((256, 64), (small.Width, small.Height));
    }

    [Fact]
    public async Task A_small_picture_is_not_enlarged()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var view = await StoredAsync(await UploadAsync(vasya, Png(120, 80), "small.png"));

        Assert.Equal((120, 80), (view.GetProperty("width").GetInt32(), view.GetProperty("height").GetInt32()));
    }

    [Fact]
    public async Task A_photo_loses_its_metadata()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var photo = JpegWithDescription("Home address, 55.75 N 37.61 E");

        var view = await StoredAsync(await UploadAsync(vasya, photo, "photo.jpg"));

        var stored = await vasya.GetByteArrayAsync(view.GetProperty("url").GetString(), Ct);
        using var image = Image.NewFromBuffer(stored);
        Assert.DoesNotContain(image.GetFields(), f => f.StartsWith("exif", StringComparison.Ordinal) || f is "xmp-data" or "iptc-data");
        Assert.DoesNotContain("Home address", Encoding.Latin1.GetString(stored), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_gif_is_kept_byte_for_byte_with_an_animated_thumbnail()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var gif = AnimatedGif(40, 30, frames: 3);

        var view = await StoredAsync(await UploadAsync(vasya, gif, "dance.gif"));

        Assert.Equal(("image/gif", 40, 30, 3), (view.GetProperty("mediaType").GetString(), view.GetProperty("width").GetInt32(), view.GetProperty("height").GetInt32(), view.GetProperty("frames").GetInt32()));
        Assert.Equal(gif, await vasya.GetByteArrayAsync(view.GetProperty("url").GetString(), Ct));
        using var thumbnail = Image.NewFromBuffer(await vasya.GetByteArrayAsync(view.GetProperty("thumbnailUrl").GetString(), Ct), "[n=-1]");
        Assert.Equal(3, (int)thumbnail.Get("n-pages"));
    }

    [Fact]
    public async Task A_webp_is_accepted_and_re_encoded()
    {
        var vasya = await _site.SignedInAsync("vasya");
        using var grey = Image.Black(300, 200, bands: 3) + 60;
        using var source = grey.Cast(Enums.BandFormat.Uchar);

        var view = await StoredAsync(await UploadAsync(vasya, source.WebpsaveBuffer(), "shot.webp"));

        Assert.Equal(("image/webp", 300, 200), (view.GetProperty("mediaType").GetString(), view.GetProperty("width").GetInt32(), view.GetProperty("height").GetInt32()));
    }

    [Fact]
    public async Task A_photo_is_turned_by_its_orientation()
    {
        var vasya = await _site.SignedInAsync("vasya");
        using var grey = Image.Black(60, 40, bands: 3) + 100;
        using var plain = grey.Cast(Enums.BandFormat.Uchar);
        using var sideways = plain.Mutate(m => m.Set(GValue.GIntType, "orientation", 6));
        var jpeg = sideways.JpegsaveBuffer();
        using (var check = Image.NewFromBuffer(jpeg))
        {
            Assert.Equal(6, (int)check.Get("orientation"));
        }

        var view = await StoredAsync(await UploadAsync(vasya, jpeg, "phone.jpg"));

        Assert.Equal((40, 60), (view.GetProperty("width").GetInt32(), view.GetProperty("height").GetInt32()));
    }

    [Fact]
    public async Task The_type_comes_from_the_content_not_the_name_or_the_header()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var view = await StoredAsync(await UploadAsync(vasya, Png(50, 50), "notes.txt", "text/plain"));

        Assert.Equal("image/webp", view.GetProperty("mediaType").GetString());
    }

    // ---- What is refused ----

    [Theory]
    [InlineData("hello, this is text", "photo.png")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>", "cat.svg")]
    [InlineData("<html><script>alert(1)</script></html>", "page.gif")]
    [InlineData("%PDF-1.7\n1 0 obj", "doc.png")]
    public async Task Anything_but_a_picture_is_refused(string content, string name)
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await UploadAsync(vasya, Encoding.UTF8.GetBytes(content), name, "image/png");

        await AssertRefusedAsync(response, HttpStatusCode.UnprocessableEntity, "file.typeInvalid");
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task A_gif_header_in_front_of_a_script_is_refused()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await UploadAsync(vasya, Encoding.ASCII.GetBytes("GIF89a<script>alert(1)</script>"), "polyglot.gif");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(await CodeAsync(response), new[] { "file.typeInvalid", "file.broken" });
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task A_cut_off_picture_is_refused()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var jpeg = JpegWithDescription("x");

        var response = await UploadAsync(vasya, jpeg[..(jpeg.Length / 2)], "cut.jpg");

        await AssertRefusedAsync(response, HttpStatusCode.UnprocessableEntity, "file.broken");
    }

    [Fact]
    public async Task A_cut_off_gif_is_refused_and_nothing_is_kept()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var gif = AnimatedGif(64, 64, frames: 6, noise: true);

        var response = await UploadAsync(vasya, gif[..(gif.Length * 2 / 3)], "cut.gif");

        await AssertRefusedAsync(response, HttpStatusCode.UnprocessableEntity, "file.broken");
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task An_interlaced_png_or_a_progressive_jpeg_has_a_lower_pixel_limit()
    {
        using var strict = _site.WithWebHostBuilder(b => b.UseSetting("Files:MaxInterlacedPixels", "10000"));
        var vasya = await SignedInAsync(strict, "vasya");
        using var grey = Image.Black(200, 200, bands: 3) + 80;
        using var picture = grey.Cast(Enums.BandFormat.Uchar);

        // Decoded whole in memory, not line by line: refused above the lower limit
        await AssertRefusedAsync(await UploadAsync(vasya, picture.PngsaveBuffer(interlace: true), "adam7.png"), HttpStatusCode.UnprocessableEntity, "file.tooManyPixels");
        await AssertRefusedAsync(await UploadAsync(vasya, picture.JpegsaveBuffer(interlace: true), "progressive.jpg"), HttpStatusCode.UnprocessableEntity, "file.tooManyPixels");

        // The same pictures read line by line keep the usual limit
        await StoredAsync(await UploadAsync(vasya, picture.PngsaveBuffer(), "plain.png"));
        await StoredAsync(await UploadAsync(vasya, picture.JpegsaveBuffer(), "baseline.jpg"));
    }

    [Fact]
    public async Task A_gif_with_too_many_frames_is_refused()
    {
        using var strict = _site.WithWebHostBuilder(b => b.UseSetting("Files:MaxFrames", "2"));
        var vasya = await SignedInAsync(strict, "vasya");

        await AssertRefusedAsync(await UploadAsync(vasya, AnimatedGif(20, 20, frames: 3), "long.gif"), HttpStatusCode.UnprocessableEntity, "file.tooManyFrames");
        await StoredAsync(await UploadAsync(vasya, AnimatedGif(20, 20, frames: 2), "short.gif"));
    }

    [Fact]
    public async Task A_small_file_that_unpacks_into_too_many_pixels_is_refused()
    {
        using var strict = _site.WithWebHostBuilder(b => b.UseSetting("Files:MaxPixels", "10000"));
        var vasya = await SignedInAsync(strict, "vasya");

        await AssertRefusedAsync(await UploadAsync(vasya, Png(200, 200), "bomb.png"), HttpStatusCode.UnprocessableEntity, "file.tooManyPixels");
        await AssertRefusedAsync(await UploadAsync(vasya, AnimatedGif(40, 30, frames: 10), "long.gif"), HttpStatusCode.UnprocessableEntity, "file.tooManyPixels");
    }

    [Fact]
    public async Task A_gif_over_its_own_limit_is_refused()
    {
        using var strict = _site.WithWebHostBuilder(b => b.UseSetting("Files:MaxGifBytes", "200"));
        var vasya = await SignedInAsync(strict, "vasya");

        var response = await UploadAsync(vasya, AnimatedGif(64, 64, frames: 4, noise: true), "big.gif");

        await AssertRefusedAsync(response, HttpStatusCode.RequestEntityTooLarge, "file.tooLarge");
    }

    [Fact]
    public async Task A_file_over_the_upload_limit_is_refused()
    {
        using var strict = _site.WithWebHostBuilder(b => b.UseSetting("Files:MaxUploadBytes", "1000"));
        var vasya = await SignedInAsync(strict, "vasya");

        // Over the file limit, within the room left for the form: the handler answers with the code
        var picture = Png(100, 100, noise: true);
        Assert.InRange(picture.Length, 1001, 60 * 1024);
        var response = await UploadAsync(vasya, picture, "big.png");

        await AssertRefusedAsync(response, HttpStatusCode.RequestEntityTooLarge, "file.tooLarge");
    }

    [Fact]
    public async Task A_body_far_over_the_limit_is_cut_before_it_is_read()
    {
        using var strict = _site.WithWebHostBuilder(b => b.UseSetting("Files:MaxUploadBytes", "1000"));
        var vasya = await SignedInAsync(strict, "vasya");

        var response = await UploadAsync(vasya, new byte[300 * 1024], "huge.png");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Every_other_api_body_keeps_its_small_limit()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsync("/api/auth/password", new ByteArrayContent(new byte[200 * 1024]) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task An_upload_needs_one_file_and_a_command_id()
    {
        var vasya = await _site.SignedInAsync("vasya");

        using var noId = new MultipartFormDataContent { { new ByteArrayContent(Png(10, 10)), "file", "a.png" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await vasya.PostAsync("/api/files", noId, Ct)).StatusCode);

        using var noFile = new MultipartFormDataContent { { new StringContent(Guid.NewGuid().ToString()), "commandId" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await vasya.PostAsync("/api/files", noFile, Ct)).StatusCode);

        using var two = new MultipartFormDataContent
        {
            { new StringContent(Guid.NewGuid().ToString()), "commandId" },
            { new ByteArrayContent(Png(10, 10)), "file", "a.png" },
            { new ByteArrayContent(Png(10, 10)), "file", "b.png" },
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await vasya.PostAsync("/api/files", two, Ct)).StatusCode);

        // Not a form at all: the endpoint takes only multipart/form-data
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await vasya.PostAsJsonAsync("/api/files", new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Empty(StoredFiles());
    }

    // ---- Who ----

    [Fact]
    public async Task Anonymous_can_neither_upload_nor_see()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var view = await StoredAsync(await UploadAsync(vasya, Png(10, 10), "a.png"));
        var anonymous = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await UploadAsync(anonymous, Png(10, 10), "a.png")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(view.GetProperty("url").GetString(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(view.GetProperty("thumbnailUrl").GetString(), Ct)).StatusCode);
    }

    [Fact]
    public async Task Another_signed_in_user_sees_the_picture()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var view = await StoredAsync(await UploadAsync(vasya, Png(10, 10), "a.png"));
        var zritel = await _site.SignedInAsync("zritel");

        Assert.Equal(HttpStatusCode.OK, (await zritel.GetAsync(view.GetProperty("url").GetString(), Ct)).StatusCode);
    }

    [Fact]
    public async Task An_upload_without_the_antiforgery_token_is_refused()
    {
        var vasya = await _site.SignedInAsync("vasya");
        vasya.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");

        var response = await UploadAsync(vasya, Png(10, 10), "a.png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("csrf.invalid", await CodeAsync(response));
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task An_unknown_file_is_not_found()
    {
        var vasya = await _site.SignedInAsync("vasya");

        Assert.Equal(HttpStatusCode.NotFound, (await vasya.GetAsync($"/api/files/{Guid.NewGuid()}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await vasya.GetAsync($"/api/files/{Guid.NewGuid()}/thumbnail", Ct)).StatusCode);
    }

    // ---- How it is served ----

    [Fact]
    public async Task A_file_is_served_only_as_a_picture()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var view = await StoredAsync(await UploadAsync(vasya, Png(10, 10), "a.png"));

        foreach (var url in new[] { view.GetProperty("url").GetString(), view.GetProperty("thumbnailUrl").GetString() })
        {
            using var response = await vasya.GetAsync(url, Ct);
            Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Contains("sandbox", Assert.Single(response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
            Assert.Contains("default-src 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
            Assert.True(response.Headers.CacheControl?.Private);
            Assert.Contains("immutable", response.Headers.CacheControl!.Extensions.Select(e => e.Name));
            Assert.Contains("frame-ancestors 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
            Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Cross-Origin-Resource-Policy")));
            Assert.Null(response.Content.Headers.ContentDisposition?.FileName);
        }
    }

    // ---- Retries and limits ----

    [Fact]
    public async Task A_retry_with_the_same_command_id_stores_nothing_twice()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var commandId = Guid.NewGuid();

        var first = await StoredAsync(await UploadAsync(vasya, Png(10, 10), "a.png", commandId: commandId));
        var again = await StoredAsync(await UploadAsync(vasya, Png(10, 10), "a.png", commandId: commandId));

        Assert.Equal(first.GetProperty("id").GetGuid(), again.GetProperty("id").GetGuid());
        Assert.False(first.GetProperty("duplicate").GetBoolean());
        Assert.True(again.GetProperty("duplicate").GetBoolean());
        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Files.CountAsync(Ct));
        Assert.Equal(2, StoredFiles().Length); // the file and its thumbnail
    }

    [Fact]
    public async Task Two_uploads_with_the_same_command_id_at_once_give_one_file()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var commandId = Guid.NewGuid();
        var picture = Png(400, 300, noise: true);

        var answers = await Task.WhenAll(
            UploadAsync(vasya, picture, "a.png", commandId: commandId),
            UploadAsync(vasya, picture, "a.png", commandId: commandId));

        var views = await Task.WhenAll(answers.Select(StoredAsync));
        Assert.Single(views.Select(v => v.GetProperty("id").GetGuid()).Distinct());
        Assert.Single(views, v => !v.GetProperty("duplicate").GetBoolean());
        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Files.CountAsync(Ct));
        Assert.Equal(2, StoredFiles().Length);
    }

    [Fact]
    public async Task An_upload_to_the_path_with_a_trailing_slash_keeps_the_upload_limit()
    {
        var vasya = await _site.SignedInAsync("vasya");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Guid.NewGuid().ToString()), "commandId" },
            { new ByteArrayContent(Png(300, 300, noise: true)), "file", "a.png" },
        };

        var response = await vasya.PostAsync("/api/files/", form, Ct);

        await StoredAsync(response);
    }

    [Fact]
    public async Task Uploads_per_minute_are_limited()
    {
        using var strict = _site.WithWebHostBuilder(b => b.UseSetting("Files:UploadsPerMinute", "2"));
        var vasya = await SignedInAsync(strict, "vasya");
        await StoredAsync(await UploadAsync(vasya, Png(10, 10), "a.png"));
        await StoredAsync(await UploadAsync(vasya, Png(10, 10), "b.png"));

        Assert.Equal(HttpStatusCode.TooManyRequests, (await UploadAsync(vasya, Png(10, 10), "c.png")).StatusCode);
    }

    [Fact]
    public async Task Another_users_command_id_is_refused()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        var commandId = Guid.NewGuid();
        await StoredAsync(await UploadAsync(vasya, Png(10, 10), "a.png", commandId: commandId));

        var response = await UploadAsync(petya, Png(10, 10), "a.png", commandId: commandId);

        await AssertRefusedAsync(response, HttpStatusCode.Conflict, "command.idReused");
        Assert.Equal(2, StoredFiles().Length);
    }

    [Fact]
    public async Task The_daily_limit_refuses_one_more_and_leaves_nothing_on_disk()
    {
        using var strict = _site.WithWebHostBuilder(b => b.UseSetting("Files:UploadsPerDay", "2"));
        var vasya = await SignedInAsync(strict, "vasya");
        var petya = await SignedInAsync(strict, "petya");
        await StoredAsync(await UploadAsync(vasya, Png(10, 10), "a.png"));
        await StoredAsync(await UploadAsync(vasya, Png(10, 10), "b.png"));

        await AssertRefusedAsync(await UploadAsync(vasya, Png(10, 10), "c.png"), HttpStatusCode.TooManyRequests, "file.dailyLimit");
        Assert.Equal(4, StoredFiles().Length);

        // Another user has a limit of their own; a day later the first one uploads again
        await StoredAsync(await UploadAsync(petya, Png(10, 10), "d.png"));
        _site.Clock.UtcNow += TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1);
        await StoredAsync(await UploadAsync(vasya, Png(10, 10), "e.png"));
    }

    [Fact]
    public async Task A_stored_file_is_in_the_global_log_with_its_owner()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var view = await StoredAsync(await UploadAsync(vasya, Png(10, 10), "a.png"));

        await using var db = _site.NewDb();
        var logged = await db.Events.AsNoTracking().SingleAsync(e => e.Type == "file-stored", Ct);
        Assert.Equal((Guid.Empty, _site.Users["vasya"]), (logged.SeasonId, logged.AuthorId!.Value));
        Assert.Contains(view.GetProperty("id").GetGuid().ToString(), logged.Data, StringComparison.Ordinal);
        var record = await db.Files.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(_site.Users["vasya"], record.OwnerId);
    }

    // ---- Helpers ----

    private string[] StoredFiles() => Directory.Exists(_site.FilesPath) ? Directory.GetFiles(_site.FilesPath) : [];

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> site, string login)
    {
        var client = site.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = SiteFactory.Password }, Ct)).EnsureSuccessStatusCode();
        await SiteFactory.RefreshCsrfAsync(client);
        return client;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] content, string name, string mediaType = "application/octet-stream", Guid? commandId = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent((commandId ?? Guid.NewGuid()).ToString()), "commandId");
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", name);
        return await client.PostAsync("/api/files", form, Ct);
    }

    private static async Task<JsonElement> StoredAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        Assert.Equal(code, await CodeAsync(response));
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static byte[] Png(int width, int height, bool noise = false)
    {
        using var source = noise ? Image.Gaussnoise(width, height) : Image.Black(width, height, bands: 3) + 90;
        using var image = source.Cast(Enums.BandFormat.Uchar);
        return image.PngsaveBuffer();
    }

    private static byte[] JpegWithDescription(string description)
    {
        using var grey = Image.Black(64, 48, bands: 3) + 120;
        using var plain = grey.Cast(Enums.BandFormat.Uchar);
        using var tagged = plain.Mutate(m => m.Set(GValue.GStrType, "exif-ifd0-ImageDescription", description));
        var jpeg = tagged.JpegsaveBuffer();

        // The fixture itself must carry the metadata, or the test proves nothing
        using var check = Image.NewFromBuffer(jpeg);
        Assert.Contains(check.GetFields(), f => f.StartsWith("exif-ifd0-ImageDescription", StringComparison.Ordinal));
        return jpeg;
    }

    private static byte[] AnimatedGif(int width, int height, int frames, bool noise = false)
    {
        // Every frame differs, so the encoder keeps them all
        var pages = Enumerable.Range(0, frames)
            .Select(i => (noise ? Image.Gaussnoise(width, height) : Image.Black(width, height) + (i * 40 % 250)).Cast(Enums.BandFormat.Uchar))
            .ToArray();
        using var strip = Image.Arrayjoin(pages, across: 1);
        using var paged = strip.Mutate(m =>
        {
            m.Set(GValue.GIntType, "page-height", height);
            m.Set(GValue.ArrayIntType, "delay", Enumerable.Repeat(100, frames).ToArray());
        });
        var gif = paged.GifsaveBuffer();
        foreach (var page in pages)
        {
            page.Dispose();
        }

        return gif;
    }
}

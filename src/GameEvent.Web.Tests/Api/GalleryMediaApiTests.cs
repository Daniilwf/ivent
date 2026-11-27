using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetVips;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

public sealed class GalleryMediaApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Season => $"/api/seasons/{SiteFactory.SeasonId}";
    private static string Gallery => $"{Season}/gallery";

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        await RulesAsync(_site, rules => rules with { Features = rules.Features with { Gallery = true } });
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_single_frame_gif_is_served_as_webp_without_its_gif_comment()
    {
        using var player = await _site.SignedInAsync("vasya");
        using var spectator = await _site.SignedInAsync("zritel");
        const string Comment = "Gallery photo metadata must stay private";
        var id = Guid.NewGuid();
        var gif = GifWithComment(Comment);
        using var uploaded = await UploadAsync(player, id, gif);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

        foreach (var variant in new[] { "image", "thumbnail" })
        {
            using var response = await spectator.GetAsync($"{Gallery}/{id}/{variant}", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
            Assert.True(response.Headers.CacheControl?.Private);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var served = await response.Content.ReadAsByteArrayAsync(Ct);
            Assert.True(served.AsSpan().StartsWith("RIFF"u8));
            Assert.True(served.AsSpan(8).StartsWith("WEBP"u8));
            Assert.Equal(-1, served.AsSpan().IndexOf(Encoding.ASCII.GetBytes(Comment)));
            using var image = Image.NewFromBuffer(served);
            Assert.Equal(32, image.Width);
            Assert.Equal(24, image.Height);
        }
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("webp")]
    public async Task An_animated_image_is_refused_without_creating_a_gallery_file_or_moment(string format)
    {
        using var player = await _site.SignedInAsync("vasya");
        using var light = Image.Black(32, 24, bands: 3);
        using var dark = (light + 200).Cast(Enums.BandFormat.Uchar);
        using var strip = Image.Arrayjoin([light, dark], across: 1);
        using var frames = strip.Mutate(image =>
        {
            image.Set(GValue.GIntType, "page-height", 24);
            image.Set(GValue.ArrayIntType, "delay", new[] { 100, 100 });
        });

        var bytes = format == "gif" ? frames.GifsaveBuffer() : frames.WebpsaveBuffer();
        using var uploaded = await UploadAsync(player, Guid.NewGuid(), bytes, format);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, uploaded.StatusCode);
        using var problem = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync(Ct));
        Assert.Equal("file.notStill", problem.RootElement.GetProperty("code").GetString());
        await using var db = _site.NewDb();
        Assert.Equal(0, await db.Files.CountAsync(Ct));
        Assert.Equal(0, await db.GalleryMoments.CountAsync(Ct));
    }

    [Fact]
    public async Task Gallery_media_share_a_finite_budget_separate_from_the_feed_and_gallery_list()
    {
        using var player = await _site.SignedInAsync("vasya");
        using var spectator = await _site.SignedInAsync("zritel");
        var id = Guid.NewGuid();
        using var uploaded = await UploadAsync(player, id, GifWithComment("test"));
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

        var image = $"{Gallery}/{id}/image";
        var thumbnail = $"{Gallery}/{id}/thumbnail";
        for (var i = 0; i < 120; i++)
        {
            using var served = await spectator.GetAsync(i % 2 == 0 ? thumbnail : image, Ct);
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        }

        using var limitedImage = await spectator.GetAsync(image, Ct);
        using var limitedThumbnail = await spectator.GetAsync(thumbnail, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedImage.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedThumbnail.StatusCode);

        using var feed = await spectator.GetAsync($"{Season}/feed", Ct);
        using var list = await spectator.GetAsync(Gallery, Ct);
        using var otherAccount = await player.GetAsync(thumbnail, Ct);
        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherAccount.StatusCode);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient player, Guid id, byte[] bytes, string format = "gif")
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Guid.NewGuid().ToString()), "commandId");
        form.Add(new StringContent(id.ToString()), "itemId");
        form.Add(new StringContent("An in-game screenshot"), "caption");
        var upload = new ByteArrayContent(bytes);
        upload.Headers.ContentType = new MediaTypeHeaderValue($"image/{format}");
        form.Add(upload, "file", $"moment.{format}");
        return await player.PostAsync(Gallery, form, Ct);
    }

    private static byte[] GifWithComment(string comment)
    {
        using var picture = Image.Black(32, 24, bands: 3);
        var gif = picture.GifsaveBuffer();
        var text = Encoding.ASCII.GetBytes(comment);
        Assert.Equal(0x3B, gif[^1]);
        // Add a legal GIF89a comment extension immediately before the trailer.
        return [.. gif[..^1], 0x21, 0xFE, checked((byte)text.Length), .. text, 0x00, 0x3B];
    }
}

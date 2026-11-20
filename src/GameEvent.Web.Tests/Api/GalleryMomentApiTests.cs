using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameEvent.Engine.Gallery;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetVips;
using static GameEvent.Web.Tests.Api.ApiCalls;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>Standalone gallery screenshots (SPEC «Лента и общение» and «Спам в галерее и комментариях», D-108).</summary>
public sealed partial class GalleryMomentApiTests : IAsyncLifetime
{
    private const string Caption = "Момент на финише";
    private readonly SiteFactory _site = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Guid SeasonId => SiteFactory.SeasonId;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();
    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_spectator_can_open_a_moments_images_until_an_admin_hides_it()
    {
        await EnableGalleryAsync();
        using var player = await _site.SignedInAsync("vasya");
        using var spectator = await _site.SignedInAsync("zritel");
        using var admin = await _site.SignedInAsync("admin");
        var id = Guid.NewGuid();

        using var created = await UploadAsync(player, SeasonId, id);
        var item = await ReadItemAsync(created, HttpStatusCode.Created);
        Assert.Equal(new Moment(id, _site.Players["vasya"], Caption, Media(SeasonId, id, "image"), Media(SeasonId, id, "thumbnail")), item);
        Assert.Equal(item, Assert.Single(await ItemsAsync(spectator, SeasonId)));
        await AssertImageAsync(spectator, item.ImageUrl);
        await AssertImageAsync(spectator, item.ThumbnailUrl);

        using var forbidden = await HideAsync(player, SeasonId, id);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(item, Assert.Single(await ItemsAsync(spectator, SeasonId)));

        using var hidden = await HideAsync(admin, SeasonId, id);
        Assert.Equal(HttpStatusCode.NoContent, hidden.StatusCode);
        Assert.Empty(await ItemsAsync(spectator, SeasonId));
        await AssertMissingAsync(spectator, item.ImageUrl);
        await AssertMissingAsync(spectator, item.ThumbnailUrl);
        await AssertMissingAsync(player, item.ImageUrl);
    }

    [Fact]
    public async Task The_same_image_cannot_be_read_as_a_generic_file_or_from_another_season()
    {
        await EnableGalleryAsync();
        var otherSeason = await CreateOtherGallerySeasonAsync();
        using var player = await _site.SignedInAsync("vasya");
        using var spectator = await _site.SignedInAsync("zritel");
        using var admin = await _site.SignedInAsync("admin");
        var id = Guid.NewGuid();

        using var created = await UploadAsync(player, SeasonId, id);
        var item = await ReadItemAsync(created, HttpStatusCode.Created);
        await using var db = _site.NewDb();
        var fileId = await db.Files.AsNoTracking().Select(file => file.Id).SingleAsync(Ct);

        foreach (var reader in new[] { spectator, player })
        {
            await AssertMissingAsync(reader, $"/api/files/{fileId}");
            await AssertMissingAsync(reader, $"/api/files/{fileId}/thumbnail");
            await AssertMissingAsync(reader, Media(otherSeason, id, "image"));
            await AssertMissingAsync(reader, Media(otherSeason, id, "thumbnail"));
        }

        using var wrongHide = await HideAsync(admin, otherSeason, id);
        Assert.Equal(HttpStatusCode.NotFound, wrongHide.StatusCode);
        Assert.Empty(await ItemsAsync(spectator, otherSeason));
        await AssertImageAsync(spectator, item.ImageUrl);
    }

    [Fact]
    public async Task A_gallery_image_cannot_be_attached_to_a_bug_report_by_its_owner()
    {
        await EnableGalleryAsync();
        using var player = await _site.SignedInAsync("vasya");
        using var uploaded = await UploadAsync(player, SeasonId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        await using var db = _site.NewDb();
        var fileId = await db.Files.AsNoTracking().Select(file => file.Id).SingleAsync(Ct);

        using var report = await player.PostAsJsonAsync("/api/bug-reports", new
        {
            commandId = Guid.NewGuid(),
            page = "/",
            text = "A test report",
            context = (object?)null,
            screenshotFileId = fileId,
        }, Ct);
        await AssertProblemAsync(report, HttpStatusCode.Conflict, "bugReport.screenshotUnknown");
        Assert.Empty(await db.BugReports.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task The_gallery_filters_visible_moments_by_player_in_the_selected_season()
    {
        await EnableGalleryAsync();
        using var first = await _site.SignedInAsync("vasya");
        using var second = await _site.SignedInAsync("petya");
        using var spectator = await _site.SignedInAsync("zritel");

        using var firstUpload = await UploadAsync(first, SeasonId, Guid.NewGuid());
        var firstMoment = await ReadItemAsync(firstUpload, HttpStatusCode.Created);
        using var secondUpload = await UploadAsync(second, SeasonId, Guid.NewGuid());
        var secondMoment = await ReadItemAsync(secondUpload, HttpStatusCode.Created);

        Assert.Equal(2, (await ItemsAsync(spectator, SeasonId)).Length);
        Assert.Equal(firstMoment, Assert.Single(await ItemsAsync(spectator, SeasonId, _site.Players["vasya"])));
        Assert.Equal(secondMoment, Assert.Single(await ItemsAsync(spectator, SeasonId, _site.Players["petya"])));
        using var one = await spectator.GetAsync($"{Gallery(SeasonId)}?itemId={firstMoment.Id}", Ct);
        var matching = await OkAsync(one);
        Assert.Equal(firstMoment, ToMoment(Assert.Single(matching.GetProperty("items").EnumerateArray())));
        Assert.Equal(JsonValueKind.Null, matching.GetProperty("nextBefore").ValueKind);
        using var wrongPlayer = await spectator.GetAsync(
            $"{Gallery(SeasonId)}?itemId={firstMoment.Id}&playerId={_site.Players["petya"]}", Ct);
        Assert.Empty((await OkAsync(wrongPlayer)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Gallery_navigation_and_publication_capability_follow_rules_membership_and_role()
    {
        using var player = await _site.SignedInAsync("vasya");
        using var spectator = await _site.SignedInAsync("zritel");
        using var admin = await _site.SignedInAsync("admin");

        async Task<JsonElement> ReadAsync(HttpClient client, string path)
        {
            using var response = await client.GetAsync(path, Ct);
            return await OkAsync(response);
        }

        async Task<bool> ListedAsync()
        {
            var seasons = await ReadAsync(spectator, "/api/seasons/gallery");
            return seasons.EnumerateArray().Any(row => row.GetProperty("id").GetGuid() == SeasonId);
        }

        var seasonPath = $"/api/seasons/{SeasonId}";
        Assert.False((await ReadAsync(spectator, $"{seasonPath}/navigation")).TryGetProperty("galleryEnabled", out _));
        Assert.False(await ListedAsync());
        await EnableGalleryAsync();

        Assert.True((await ReadAsync(spectator, $"{seasonPath}/navigation")).GetProperty("galleryEnabled").GetBoolean());
        Assert.True((await ReadAsync(spectator, seasonPath)).GetProperty("galleryEnabled").GetBoolean());
        Assert.True(await ListedAsync());
        var playerGallery = await ReadAsync(player, Gallery(SeasonId));
        Assert.True(playerGallery.GetProperty("canPublish").GetBoolean());
        Assert.Contains(playerGallery.GetProperty("players").EnumerateArray(),
            author => author.GetProperty("id").GetGuid() == _site.Players["vasya"]);
        foreach (var reader in new[] { spectator, admin })
        {
            Assert.False((await ReadAsync(reader, Gallery(SeasonId))).GetProperty("canPublish").GetBoolean());
        }

        using var inactive = await admin.PostAsJsonAsync($"/api/admin/seasons/{SeasonId}/players/{_site.Players["vasya"]}/inactive",
            new { commandId = Guid.NewGuid(), isInactive = true }, Ct);
        await OkAsync(inactive);
        Assert.False((await ReadAsync(player, Gallery(SeasonId))).GetProperty("canPublish").GetBoolean());
    }

    [Fact]
    public async Task Gallery_reads_and_writes_require_an_authenticated_session()
    {
        using var anonymous = await _site.AnonymousAsync();
        var id = Guid.NewGuid();
        using var seasons = await anonymous.GetAsync("/api/seasons/gallery", Ct);
        using var list = await anonymous.GetAsync(Gallery(SeasonId), Ct);
        using var image = await anonymous.GetAsync(Media(SeasonId, id, "image"), Ct);
        using var thumbnail = await anonymous.GetAsync(Media(SeasonId, id, "thumbnail"), Ct);
        using var upload = await UploadAsync(anonymous, SeasonId, id);
        using var hide = await HideAsync(anonymous, SeasonId, id);

        foreach (var response in new[] { seasons, list, image, thumbnail, upload, hide })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Publication_requires_membership_and_writes_require_anti_forgery()
    {
        await EnableGalleryAsync();
        using var player = await _site.SignedInAsync("vasya");
        using var nonParticipant = await _site.SignedInAsync("masha");
        using var spectator = await _site.SignedInAsync("zritel");
        using var admin = await _site.SignedInAsync("admin");
        foreach (var client in new[] { nonParticipant, spectator, admin })
        {
            using var denied = await UploadAsync(client, SeasonId, Guid.NewGuid());
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        player.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var rejectedUpload = await UploadAsync(player, SeasonId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, rejectedUpload.StatusCode);

        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var rejectedHide = await HideAsync(admin, SeasonId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, rejectedHide.StatusCode);

        Assert.Empty(await ItemsAsync(spectator, SeasonId));
        await using var db = _site.NewDb();
        Assert.Equal(0, await db.Files.CountAsync(Ct));
    }

    [Fact]
    public async Task The_default_flag_disables_the_gallery_and_its_writes()
    {
        using var player = await _site.SignedInAsync("vasya");
        using var spectator = await _site.SignedInAsync("zritel");
        using var list = await spectator.GetAsync(Gallery(SeasonId), Ct);
        using var upload = await UploadAsync(player, SeasonId, Guid.NewGuid());

        await AssertProblemAsync(list, HttpStatusCode.Conflict, "feature.disabled");
        await AssertProblemAsync(upload, HttpStatusCode.Conflict, "feature.disabled");
        await using var db = _site.NewDb();
        Assert.Equal(0, await db.Files.CountAsync(Ct));
    }

    [Fact]
    public async Task Rejecting_a_non_image_neither_publishes_it_nor_spends_the_daily_slot()
    {
        await EnableGalleryAsync(limit: 1);
        using var player = await _site.SignedInAsync("vasya");
        using var spectator = await _site.SignedInAsync("zritel");
        using var invalid = await UploadAsync(player, SeasonId, Guid.NewGuid(), bytes: Encoding.UTF8.GetBytes("not an image"));

        await AssertProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, "file.typeInvalid");
        Assert.Empty(await ItemsAsync(spectator, SeasonId));
        await using var db = _site.NewDb();
        Assert.Equal(0, await db.Files.CountAsync(Ct));

        var id = Guid.NewGuid();
        using var valid = await UploadAsync(player, SeasonId, id);
        Assert.Equal(id, (await ReadItemAsync(valid, HttpStatusCode.Created)).Id);
        Assert.Equal(1, await db.Files.CountAsync(Ct));
    }

    [Fact]
    public async Task A_retry_spends_one_slot_and_hiding_does_not_refund_it_before_the_seasons_midnight()
    {
        await EnableGalleryAsync(limit: 1, timezone: "UTC");
        using var player = await _site.SignedInAsync("vasya");
        using var other = await _site.SignedInAsync("petya");
        using var admin = await _site.SignedInAsync("admin");
        using var spectator = await _site.SignedInAsync("zritel");
        _site.Clock.UtcNow = new DateTimeOffset(2026, 10, 1, 23, 59, 30, TimeSpan.Zero);
        var id = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var screenshot = Png();

        using var first = await UploadAsync(player, SeasonId, id, commandId, screenshot);
        var moment = await ReadItemAsync(first, HttpStatusCode.Created);
        using var repeat = await UploadAsync(player, SeasonId, id, commandId, screenshot);
        Assert.True(repeat.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"{repeat.StatusCode}: {await repeat.Content.ReadAsStringAsync(Ct)}");
        Assert.Equal(moment, await ReadItemAsync(repeat, repeat.StatusCode));
        Assert.Equal(moment, Assert.Single(await ItemsAsync(spectator, SeasonId)));
        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Files.CountAsync(Ct));

        using var hide = await HideAsync(admin, SeasonId, id);
        Assert.Equal(HttpStatusCode.NoContent, hide.StatusCode);
        using var exhausted = await UploadAsync(player, SeasonId, Guid.NewGuid());
        await AssertProblemAsync(exhausted, HttpStatusCode.TooManyRequests, "gallery.dailyLimit");
        Assert.Empty(await ItemsAsync(spectator, SeasonId));
        Assert.Equal(1, await db.Files.CountAsync(Ct));

        var otherId = Guid.NewGuid();
        using var otherUpload = await UploadAsync(other, SeasonId, otherId);
        Assert.Equal(otherId, (await ReadItemAsync(otherUpload, HttpStatusCode.Created)).Id);

        _site.Clock.Advance(TimeSpan.FromSeconds(31));
        var nextId = Guid.NewGuid();
        using var nextDay = await UploadAsync(player, SeasonId, nextId);
        Assert.Equal(nextId, (await ReadItemAsync(nextDay, HttpStatusCode.Created)).Id);
        var visible = await ItemsAsync(spectator, SeasonId);
        Assert.Equal(new[] { nextId, otherId }.Order(), visible.Select(item => item.Id).Order());
    }

    [Fact]
    public async Task Two_publications_from_the_same_account_compete_for_one_remaining_daily_slot()
    {
        await EnableGalleryAsync(limit: 1);
        using var firstTab = await _site.SignedInAsync("vasya");
        using var secondTab = await _site.SignedInAsync("vasya");
        using var spectator = await _site.SignedInAsync("zritel");
        var requests = await Task.WhenAll(
            UploadAsync(firstTab, SeasonId, Guid.NewGuid()),
            UploadAsync(secondTab, SeasonId, Guid.NewGuid()));

        using var accepted = Assert.Single(requests, response => response.StatusCode == HttpStatusCode.Created);
        using var rejected = Assert.Single(requests, response => response.StatusCode == HttpStatusCode.TooManyRequests);
        await AssertProblemAsync(rejected, HttpStatusCode.TooManyRequests, "gallery.dailyLimit");
        Assert.Equal(await ReadItemAsync(accepted, HttpStatusCode.Created), Assert.Single(await ItemsAsync(spectator, SeasonId)));
        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Files.CountAsync(Ct));
    }

    [Fact]
    public async Task A_retry_uses_the_winning_event_and_rejects_different_caption_or_bytes_before_decoding()
    {
        await EnableGalleryAsync(limit: 1);
        using var player = await _site.SignedInAsync("vasya");
        using var otherTab = await _site.SignedInAsync("vasya");
        var itemId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var bytes = Png();
        var attempts = await Task.WhenAll(UploadAsync(player, SeasonId, itemId, commandId, bytes),
            UploadAsync(otherTab, SeasonId, itemId, commandId, bytes, $" {Caption} "));
        using var created = Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.Created);
        using var retried = Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(await created.Content.ReadAsStringAsync(Ct), await retried.Content.ReadAsStringAsync(Ct));

        using var changedCaption = await UploadAsync(player, SeasonId, itemId, commandId, bytes, "Different caption");
        await AssertProblemAsync(changedCaption, HttpStatusCode.Conflict, "command.idReused");
        using var changedBytes = await UploadAsync(player, SeasonId, itemId, commandId, "Not an image"u8.ToArray());
        await AssertProblemAsync(changedBytes, HttpStatusCode.Conflict, "command.idReused");

        await using var db = _site.NewDb();
        var published = await db.GalleryMoments.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(commandId, await db.Events.Where(row => row.Type == "gallery-moment-published")
            .Select(row => row.CommandId).SingleAsync(Ct));
        Assert.Equal(published.FileId, await db.Files.Select(file => file.Id).SingleAsync(Ct));
        Assert.Equal(published.FileId, await db.GalleryMediaBindings.Select(binding => binding.FileId).SingleAsync(Ct));
        var storage = _site.Services.GetRequiredService<FileStorage>();
        var paths = Directory.EnumerateFiles(storage.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        var expected = new[] { FileNames.Main(published.FileId, FileNames.Webp), FileNames.Thumbnail(published.FileId, FileNames.Webp) };
        Assert.Equal(expected.Order(StringComparer.Ordinal), paths);
    }

    [Fact]
    public async Task A_blank_caption_is_invalid_and_a_spent_quota_refuses_a_new_upload_before_decoding()
    {
        await EnableGalleryAsync(limit: 1);
        using var player = await _site.SignedInAsync("vasya");
        using var blank = await UploadAsync(player, SeasonId, Guid.NewGuid(), caption: "   ");
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        using var valid = await UploadAsync(player, SeasonId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
        using var denied = await UploadAsync(player, SeasonId, Guid.NewGuid(), bytes: "Not an image"u8.ToArray());
        await AssertProblemAsync(denied, HttpStatusCode.TooManyRequests, "gallery.dailyLimit");
    }

    [Fact]
    public async Task The_quota_precheck_and_queue_follow_the_current_season_calendar()
    {
        await EnableGalleryAsync(limit: 1, timezone: "UTC");
        using var player = await _site.SignedInAsync("vasya");
        _site.Clock.UtcNow = new DateTimeOffset(2026, 10, 1, 23, 30, 0, TimeSpan.Zero);
        using var first = await UploadAsync(player, SeasonId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        await EnableGalleryAsync(timezone: "Europe/Moscow");
        using var sameDay = await UploadAsync(player, SeasonId, Guid.NewGuid(), bytes: "Not an image"u8.ToArray());
        await AssertProblemAsync(sameDay, HttpStatusCode.TooManyRequests, "gallery.dailyLimit");

        await EnableGalleryAsync(timezone: "UTC");
        _site.Clock.Advance(TimeSpan.FromMinutes(31));
        using var nextDay = await UploadAsync(player, SeasonId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, nextDay.StatusCode);
    }

    [Fact]
    public async Task Pagination_uses_the_tie_breaker_even_when_the_previous_page_anchor_is_hidden()
    {
        await EnableGalleryAsync(limit: 51);
        using var spectator = await _site.SignedInAsync("zritel");
        using var admin = await _site.SignedInAsync("admin");
        var bus = _site.Services.GetRequiredService<CommandBus>();
        var actor = _site.Users["vasya"];
        var fingerprint = new string('A', 64);
        var items = new List<Guid>();
        for (var number = 1; number <= 51; number++)
        {
            var item = new Guid(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, (byte)number);
            var accepted = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), SeasonId,
                new PublishGalleryMoment(item, actor, Caption, fingerprint), actor,
                new PreparedGalleryFile(Guid.NewGuid(), "image/webp", 1, 1, 1, 1, fingerprint)), Ct);
            Assert.True(accepted.IsAccepted, accepted.Rejection?.Detail);
            items.Add(item);
        }

        using var firstResponse = await spectator.GetAsync(Gallery(SeasonId), Ct);
        var first = await OkAsync(firstResponse);
        var firstPage = first.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(items.Skip(1).Reverse(), firstPage.Select(item => item.GetProperty("id").GetGuid()));
        Assert.Single(firstPage.Select(item => item.GetProperty("createdAt").GetDateTimeOffset()).Distinct());
        var cursor = first.GetProperty("nextBefore").GetGuid();
        Assert.Equal(items[1], cursor);

        using var hidden = await HideAsync(admin, SeasonId, cursor);
        Assert.Equal(HttpStatusCode.NoContent, hidden.StatusCode);
        using var secondResponse = await spectator.GetAsync($"{Gallery(SeasonId)}?before={cursor}", Ct);
        var second = await OkAsync(secondResponse);
        var itemOnSecondPage = Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.Equal(items[0], itemOnSecondPage.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextBefore").ValueKind);
    }

    private Task EnableGalleryAsync(int? limit = null, string? timezone = null) =>
        RulesAsync(_site, rules => GalleryRules(rules, limit, timezone));

    private static Ruleset GalleryRules(Ruleset rules, int? limit = null, string? timezone = null) => rules with
    {
        Features = rules.Features with { Gallery = true },
        Season = rules.Season with { Timezone = timezone ?? rules.Season.Timezone },
        Social = rules.Social with { GalleryUploadsPerDay = limit ?? rules.Social.GalleryUploadsPerDay },
    };

    private async Task<Guid> CreateOtherGallerySeasonAsync()
    {
        var id = await _site.CreateSeasonAsync();
        var bus = _site.Services.GetRequiredService<CommandBus>();
        var changed = await bus.SendAsync(new CommandEnvelope(
            Guid.NewGuid(), id, new ChangeRuleset(GalleryRules(RulesetJson.Default())), AuthorId: null), Ct);
        Assert.True(changed.IsAccepted, changed.Rejection?.Detail);
        var started = await bus.SendAsync(new CommandEnvelope(
            Guid.NewGuid(), id, new ChangeSeasonStatus(SeasonStatus.Active), AuthorId: null), Ct);
        Assert.True(started.IsAccepted, started.Rejection?.Detail);
        return id;
    }

    private static string Gallery(Guid seasonId) => $"/api/seasons/{seasonId}/gallery";
    private static string Media(Guid seasonId, Guid id, string size) => $"{Gallery(seasonId)}/{id}/{size}";

    private static Task<HttpResponseMessage> HideAsync(HttpClient admin, Guid seasonId, Guid id) =>
        admin.PostAsJsonAsync($"/api/admin/seasons/{seasonId}/gallery/{id}/hide", new { commandId = Guid.NewGuid() }, Ct);

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient player, Guid seasonId, Guid id, Guid? commandId = null, byte[]? bytes = null, string caption = Caption)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent((commandId ?? Guid.NewGuid()).ToString()), "commandId");
        form.Add(new StringContent(id.ToString()), "itemId");
        form.Add(new StringContent(caption), "caption");
        var file = new ByteArrayContent(bytes ?? Png());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "moment.png");

        return await player.PostAsync(Gallery(seasonId), form, Ct);
    }

    private static async Task<Moment[]> ItemsAsync(HttpClient client, Guid seasonId, Guid? playerId = null)
    {
        var path = playerId is null ? Gallery(seasonId) : $"{Gallery(seasonId)}?playerId={playerId}";
        using var response = await client.GetAsync(path, Ct);
        var page = await OkAsync(response);
        return [.. page.GetProperty("items").EnumerateArray().Select(ToMoment)];
    }

    private static async Task<Moment> ReadItemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return ToMoment(json.RootElement);
    }

    private static Moment ToMoment(JsonElement item) => new(
        item.GetProperty("id").GetGuid(), item.GetProperty("playerId").GetGuid(), item.GetProperty("caption").GetString()!,
        item.GetProperty("imageUrl").GetString()!, item.GetProperty("thumbnailUrl").GetString()!);

    private static async Task AssertImageAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var image = Image.NewFromBuffer(await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.InRange(image.Width, 1, 64);
        Assert.InRange(image.Height, 1, 48);
    }

    private static async Task AssertMissingAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private static byte[] Png()
    {
        using var source = Image.Black(64, 48, bands: 3) + 90;
        using var image = source.Cast(Enums.BandFormat.Uchar);
        return image.PngsaveBuffer();
    }

    private sealed record Moment(Guid Id, Guid PlayerId, string Caption, string ImageUrl, string ThumbnailUrl);
}

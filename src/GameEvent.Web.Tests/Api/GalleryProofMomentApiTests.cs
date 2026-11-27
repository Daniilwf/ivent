using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Gallery;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetVips;
using static GameEvent.Web.Tests.Api.ApiCalls;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;
using ArchiveTransfer = GameEvent.Infrastructure.Seasons.SeasonTransfer;

namespace GameEvent.Web.Tests.Api;

/// <summary>Publication and withdrawal of a particular approved proof attachment (D-927).</summary>
public sealed class GalleryProofMomentApiTests : IAsyncLifetime
{
    private const string Caption = "Финальный кадр";
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Season => $"/api/seasons/{SiteFactory.SeasonId}";
    private static string AdminSeason => $"/api/admin/seasons/{SiteFactory.SeasonId}";
    private static string Gallery => $"{Season}/gallery";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Explicit_publication_releases_only_the_selected_photo_and_withdrawal_keeps_the_shared_daily_charge()
    {
        await EnableGalleryAsync(limit: 1);
        using var owner = await _site.SignedInAsync("vasya");
        using var spectator = await _site.SignedInAsync("zritel");
        using var admin = await _site.SignedInAsync("admin");
        var runId = await CompleteRunAsync(owner);
        var otherFile = await UploadProofImageAsync(owner, width: 32, height: 24);
        var selectedFile = await UploadProofImageAsync(owner, width: 64, height: 48);
        await SubmitAsync(owner, runId, otherFile, selectedFile);
        await ApproveAsync(admin, runId);
        Assert.Empty(await ReadMomentsAsync(spectator));

        var itemId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        using var publication = await PublishAsync(owner, runId, selectedFile, itemId, commandId);
        var moment = await ReadPublishedAsync(publication, HttpStatusCode.Created);
        Assert.Equal(
            new Moment(itemId, _site.Players["vasya"], Caption, $"{Gallery}/{itemId}/image", $"{Gallery}/{itemId}/thumbnail"),
            moment);
        using var retry = await PublishAsync(owner, runId, selectedFile, itemId, commandId);
        Assert.True(retry.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created, await DescribeAsync(retry));
        Assert.Equal(moment, await ReadPublishedAsync(retry, retry.StatusCode));
        Assert.Equal(moment, Assert.Single(await ReadMomentsAsync(spectator)));
        await AssertImageAsync(spectator, moment.ImageUrl, width: 64, height: 48);
        await AssertImageAsync(spectator, moment.ThumbnailUrl, width: 64, height: 48);

        using var withdrawal = await WithdrawAsync(owner, itemId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NoContent, withdrawal.StatusCode);
        await AssertUnpublishedAsync(spectator, moment);
        using var withdrawnRetry = await PublishAsync(owner, runId, selectedFile, itemId, commandId);
        Assert.True(withdrawnRetry.StatusCode == HttpStatusCode.NotFound, await DescribeAsync(withdrawnRetry));
        using var reacted = await spectator.PutAsJsonAsync(
            $"{Gallery}/{itemId}/reactions", new { commandId = Guid.NewGuid(), emoji = "🔥", selected = true }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, reacted.StatusCode);

        using var upload = PictureForm(width: 32, height: 24);
        upload.Add(new StringContent(Guid.NewGuid().ToString()), "itemId");
        upload.Add(new StringContent(Caption), "caption");
        using var exhausted = await owner.PostAsync(Gallery, upload, Ct);
        Assert.True(exhausted.StatusCode == HttpStatusCode.TooManyRequests, await DescribeAsync(exhausted));
        using var problem = JsonDocument.Parse(await exhausted.Content.ReadAsStringAsync(Ct));
        Assert.Equal("gallery.dailyLimit", problem.RootElement.GetProperty("code").GetString());
        using var exhaustedProofs = await owner.GetAsync($"{Gallery}/proofs", Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, exhaustedProofs.StatusCode);
    }

    [Fact]
    public async Task Only_the_author_can_discover_and_withdraw_a_moderator_hidden_proof_moment()
    {
        await EnableGalleryAsync();
        using var owner = await _site.SignedInAsync("vasya");
        using var spectator = await _site.SignedInAsync("zritel");
        using var admin = await _site.SignedInAsync("admin");
        var (runId, fileId) = await CompleteAndSubmitAsync(owner);
        await ApproveAsync(admin, runId);
        var itemId = Guid.NewGuid();
        var publicationId = Guid.NewGuid();
        using var publication = await PublishAsync(owner, runId, fileId, itemId, publicationId);
        var moment = await ReadPublishedAsync(publication, HttpStatusCode.Created);
        var hideId = Guid.NewGuid();
        using var hidden = await admin.PostAsJsonAsync($"{AdminSeason}/gallery/{itemId}/hide", new { commandId = hideId }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, hidden.StatusCode);

        using var fromOwner = await owner.GetAsync(Gallery, Ct);
        var page = await OkAsync(fromOwner);
        Assert.Equal(0, page.GetProperty("items").GetArrayLength());
        Assert.True(page.TryGetProperty("hiddenProofPublications", out var hiddenProofs));
        var note = Assert.Single(hiddenProofs.EnumerateArray());
        Assert.Equal(new[] { "createdAt", "id" }, note.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(itemId, note.GetProperty("id").GetGuid());
        Assert.NotEqual(default, note.GetProperty("createdAt").GetDateTimeOffset());
        Assert.True(fromOwner.Headers.CacheControl?.NoStore);

        using var fromSpectator = await spectator.GetAsync(Gallery, Ct);
        Assert.False((await OkAsync(fromSpectator)).TryGetProperty("hiddenProofPublications", out _));
        using var fromModerator = await admin.GetAsync(Gallery, Ct);
        Assert.False((await OkAsync(fromModerator)).TryGetProperty("hiddenProofPublications", out _));
        using var itemQuery = await owner.GetAsync($"{Gallery}?itemId={itemId}", Ct);
        Assert.False((await OkAsync(itemQuery)).TryGetProperty("hiddenProofPublications", out _));
        using var laterPage = await owner.GetAsync($"{Gallery}?before={itemId}", Ct);
        Assert.False((await OkAsync(laterPage)).TryGetProperty("hiddenProofPublications", out _));

        using var hiddenRetry = await PublishAsync(owner, runId, fileId, itemId, publicationId);
        Assert.True(hiddenRetry.StatusCode == HttpStatusCode.NotFound, await DescribeAsync(hiddenRetry));
        using var spectatorWithdrawal = await WithdrawAsync(spectator, itemId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, spectatorWithdrawal.StatusCode);
        using var withdrawn = await WithdrawAsync(owner, itemId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
        using var fromOwnerAfter = await owner.GetAsync(Gallery, Ct);
        Assert.False((await OkAsync(fromOwnerAfter)).TryGetProperty("hiddenProofPublications", out _));

        using var undo = await admin.PostAsJsonAsync($"{AdminSeason}/undo", new
        {
            commandId = Guid.NewGuid(),
            targetCommandId = hideId,
            comment = "Пересмотр модерации",
        }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, undo.StatusCode);
        using var rejection = JsonDocument.Parse(await undo.Content.ReadAsStringAsync(Ct));
        Assert.Equal("undo.dependents", rejection.RootElement.GetProperty("code").GetString());
        await AssertUnpublishedAsync(spectator, moment);
    }

    [Fact]
    public async Task Publication_rejects_an_unapproved_proof_a_different_author_and_an_unattached_file()
    {
        await EnableGalleryAsync();
        using var owner = await _site.SignedInAsync("vasya");
        using var otherPlayer = await _site.SignedInAsync("petya");
        using var spectator = await _site.SignedInAsync("zritel");
        using var admin = await _site.SignedInAsync("admin");
        var runId = await CompleteRunAsync(owner);
        var attached = await UploadProofImageAsync(owner);
        var unattached = await UploadProofImageAsync(owner);
        await SubmitAsync(owner, runId, attached);

        using var beforeApproval = await owner.GetAsync($"{Gallery}/proofs", Ct);
        Assert.Equal(0, (await OkAsync(beforeApproval)).GetArrayLength());
        using var pending = await PublishAsync(owner, runId, attached, Guid.NewGuid(), Guid.NewGuid());
        Assert.True(pending.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict, await DescribeAsync(pending));

        await ApproveAsync(admin, runId);
        using var owned = await owner.GetAsync($"{Gallery}/proofs", Ct);
        var choice = Assert.Single((await OkAsync(owned)).EnumerateArray());
        Assert.Equal(new[] { "fileId", "runId", "thumbnailUrl", "title" }, choice.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(runId, choice.GetProperty("runId").GetGuid());
        Assert.Equal(attached, choice.GetProperty("fileId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(choice.GetProperty("title").GetString()));
        Assert.Equal($"/api/files/{attached}/thumbnail", choice.GetProperty("thumbnailUrl").GetString());
        Assert.True(owned.Headers.CacheControl?.Private);
        Assert.True(owned.Headers.CacheControl?.NoStore);
        using var otherChoices = await otherPlayer.GetAsync($"{Gallery}/proofs", Ct);
        Assert.Equal(0, (await OkAsync(otherChoices)).GetArrayLength());
        using var spectatorChoices = await spectator.GetAsync($"{Gallery}/proofs", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, spectatorChoices.StatusCode);
        using var foreign = await PublishAsync(otherPlayer, runId, attached, Guid.NewGuid(), Guid.NewGuid());
        Assert.True(foreign.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, await DescribeAsync(foreign));
        using var unrelated = await PublishAsync(owner, runId, unattached, Guid.NewGuid(), Guid.NewGuid());
        Assert.True(unrelated.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict, await DescribeAsync(unrelated));
        using var invalid = await PublishAsync(owner, runId, Guid.Empty, Guid.NewGuid(), Guid.NewGuid());
        Assert.True(invalid.StatusCode == HttpStatusCode.BadRequest, await DescribeAsync(invalid));
        Assert.Empty(await ReadMomentsAsync(spectator));
    }

    [Fact]
    public async Task The_author_can_withdraw_an_archived_proof_moment_and_only_its_gallery_links_are_closed()
    {
        await EnableGalleryAsync();
        using var owner = await _site.SignedInAsync("vasya");
        using var otherPlayer = await _site.SignedInAsync("petya");
        using var spectator = await _site.SignedInAsync("zritel");
        using var admin = await _site.SignedInAsync("admin");
        var (runId, fileId) = await CompleteAndSubmitAsync(owner);
        var approvalId = await ApproveAsync(admin, runId);
        using var publication = await PublishAsync(owner, runId, fileId, Guid.NewGuid(), Guid.NewGuid());
        var moment = await ReadPublishedAsync(publication, HttpStatusCode.Created);

        foreach (var status in new[] { "closing", "finished", "archived" })
        {
            await PostOkAsync(admin, $"{AdminSeason}/status", new { commandId = Guid.NewGuid(), to = status });
        }

        using var invalid = await WithdrawAsync(owner, moment.Id, Guid.Empty);
        Assert.True(invalid.StatusCode == HttpStatusCode.BadRequest, await DescribeAsync(invalid));
        using var foreign = await WithdrawAsync(otherPlayer, moment.Id, Guid.NewGuid());
        Assert.True(foreign.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, await DescribeAsync(foreign));
        Assert.Equal(moment, Assert.Single(await ReadMomentsAsync(spectator)));

        using var withdrawn = await WithdrawAsync(owner, moment.Id, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
        await AssertUnpublishedAsync(spectator, moment);
        await AssertUnpublishedAsync(owner, moment);
        using var originalProof = await admin.GetAsync($"/api/files/{fileId}", Ct);
        Assert.Equal(HttpStatusCode.OK, originalProof.StatusCode);

        await using var db = _site.NewDb();
        var integrity = await SeasonIntegrity.CheckAsync(db, SiteFactory.SeasonId, Ct);
        Assert.NotNull(integrity);
        Assert.True(integrity.IsIntact, string.Join("; ", integrity.Differences));
        var exported = await ArchiveTransfer.ExportAsync(db, SiteFactory.SeasonId, _site.Clock.UtcNow, Ct);
        Assert.NotNull(exported);
        using var zip = new MemoryStream();
        await ArchiveTransfer.WriteZipAsync(exported, zip, Ct);
        zip.Position = 0;
        await using var otherSite = new SiteFactory();
        await otherSite.SeedAsync(withSeason: false);
        await using var importedDb = otherSite.NewDb();
        var imported = await ArchiveTransfer.ImportAsync(importedDb, await ArchiveTransfer.ReadZipAsync(zip, Ct), otherSite.Clock.UtcNow, ct: Ct);
        Assert.True(imported.Integrity.IsIntact, string.Join("; ", imported.Integrity.Differences));
        var restored = await importedDb.GalleryMoments.AsNoTracking().SingleAsync(item => item.ItemId == moment.Id, Ct);
        Assert.Equal(((Guid?)runId, (Guid?)fileId, (Guid?)approvalId),
            (restored.ProofRunId, restored.ProofFileId, restored.ProofApprovalCommandId));
        Assert.Equal(otherSite.Users["vasya"], restored.UserId);
        Assert.True(restored.ConsentRevoked);
        Assert.Empty(await importedDb.GalleryMediaBindings.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("same")]
    [InlineData("replaced")]
    [InlineData("rejected")]
    public async Task Undoing_approval_removes_publication_without_resurrecting_it_on_later_review(string nextReview)
    {
        await EnableGalleryAsync();
        using var owner = await _site.SignedInAsync("vasya");
        using var spectator = await _site.SignedInAsync("zritel");
        using var admin = await _site.SignedInAsync("admin");
        var (runId, fileId) = await CompleteAndSubmitAsync(owner);
        var approval = await ApproveAsync(admin, runId);
        using var publication = await PublishAsync(owner, runId, fileId, Guid.NewGuid(), Guid.NewGuid());
        var moment = await ReadPublishedAsync(publication, HttpStatusCode.Created);
        Assert.Equal(moment, Assert.Single(await ReadMomentsAsync(spectator)));

        await PostOkAsync(admin, $"{AdminSeason}/undo", new
        {
            commandId = Guid.NewGuid(),
            targetCommandId = approval,
            comment = "Проверить пруф заново",
        });
        await AssertUnpublishedAsync(spectator, moment);

        if (nextReview == "rejected")
        {
            await PostOkAsync(admin, $"{AdminSeason}/runs/{runId}/reject", new
            {
                commandId = Guid.NewGuid(),
                comment = "Кадр не подтверждает прохождение",
            });
            using var rejected = await PublishAsync(owner, runId, fileId, Guid.NewGuid(), Guid.NewGuid());
            Assert.True(rejected.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict, await DescribeAsync(rejected));
        }
        else
        {
            if (nextReview == "replaced")
            {
                var replacement = await UploadProofImageAsync(owner, width: 32, height: 24);
                await SubmitAsync(owner, runId, replacement);
            }

            // A second approval at the same clock time must not restore consent for the first approval.
            await ApproveAsync(admin, runId);
            if (nextReview == "replaced")
            {
                using var stale = await PublishAsync(owner, runId, fileId, Guid.NewGuid(), Guid.NewGuid());
                Assert.True(stale.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict, await DescribeAsync(stale));
            }
            else
            {
                var staleImage = new PreparedGalleryFile(Guid.NewGuid(), FileNames.Webp, 64, 64, 48, 1, "", approval);
                var author = _site.Users["vasya"];
                var queued = await _site.Services.GetRequiredService<CommandBus>().SendAsync(new CommandEnvelope(
                    Guid.NewGuid(), SiteFactory.SeasonId, new PublishProofGalleryMoment(Guid.NewGuid(), author, Caption, runId, fileId),
                    author, staleImage), Ct);
                Assert.False(queued.IsAccepted);
                Assert.Equal(GalleryRejectionCodes.ProofUnavailable, queued.Rejection?.Code);
                Assert.Empty(queued.Events);
                using var republished = await PublishAsync(owner, runId, fileId, Guid.NewGuid(), Guid.NewGuid());
                var newMoment = await ReadPublishedAsync(republished, HttpStatusCode.Created);
                Assert.Equal(newMoment, Assert.Single(await ReadMomentsAsync(spectator)));
            }
        }

        await AssertUnpublishedAsync(spectator, moment);
    }

    private Task EnableGalleryAsync(int? limit = null) => RulesAsync(_site, rules => rules with
    {
        Features = rules.Features with { Gallery = true, Reactions = true },
        Social = rules.Social with { GalleryUploadsPerDay = limit ?? rules.Social.GalleryUploadsPerDay },
    });

    private static async Task<Guid> CompleteRunAsync(HttpClient player)
    {
        await PostOkAsync(player, $"{Season}/roll");
        await PostOkAsync(player, $"{Season}/start");
        await PostOkAsync(player, $"{Season}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });
        using var response = await player.GetAsync(Season, Ct);
        var season = await OkAsync(response);
        return season.GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetGuid();
    }

    private static async Task<Guid> UploadProofImageAsync(HttpClient owner, int width = 64, int height = 48)
    {
        using var picture = PictureForm(width, height);
        using var response = await owner.PostAsync("/api/files", picture, Ct);
        Assert.True(response.IsSuccessStatusCode, await DescribeAsync(response));
        using var file = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return file.RootElement.GetProperty("id").GetGuid();
    }

    private static MultipartFormDataContent PictureForm(int width, int height)
    {
        using var image = Image.Black(width, height, bands: 3);
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(Guid.NewGuid().ToString()), "commandId");
        var file = new ByteArrayContent(image.PngsaveBuffer());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "credits.png");
        return form;
    }

    private static Task SubmitAsync(HttpClient owner, Guid runId, params Guid[] files) =>
        PostOkAsync(owner, $"{Season}/runs/{runId}/proof", new { commandId = Guid.NewGuid(), links = Array.Empty<string>(), files });

    private static async Task<(Guid RunId, Guid FileId)> CompleteAndSubmitAsync(HttpClient owner)
    {
        var runId = await CompleteRunAsync(owner);
        var fileId = await UploadProofImageAsync(owner);
        await SubmitAsync(owner, runId, fileId);
        return (runId, fileId);
    }

    private static async Task<Guid> ApproveAsync(HttpClient admin, Guid runId)
    {
        var commandId = Guid.NewGuid();
        await PostOkAsync(admin, $"{AdminSeason}/runs/{runId}/approve", new { commandId });
        return commandId;
    }

    private static Task<HttpResponseMessage> PublishAsync(HttpClient player, Guid runId, Guid fileId, Guid itemId, Guid commandId) =>
        player.PostAsJsonAsync($"{Gallery}/proofs", new { commandId, itemId, runId, fileId, caption = Caption }, Ct);

    private static Task<HttpResponseMessage> WithdrawAsync(HttpClient player, Guid itemId, Guid commandId) =>
        player.PostAsJsonAsync($"{Gallery}/{itemId}/withdraw", new { commandId }, Ct);

    private static async Task<Moment> ReadPublishedAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return ReadMoment(json.RootElement);
    }

    private static async Task<Moment[]> ReadMomentsAsync(HttpClient viewer)
    {
        using var response = await viewer.GetAsync(Gallery, Ct);
        var page = await OkAsync(response);
        return [.. page.GetProperty("items").EnumerateArray().Select(ReadMoment)];
    }

    private static Moment ReadMoment(JsonElement item) => new(
        item.GetProperty("id").GetGuid(), item.GetProperty("playerId").GetGuid(), item.GetProperty("caption").GetString()!,
        item.GetProperty("imageUrl").GetString()!, item.GetProperty("thumbnailUrl").GetString()!);

    private static async Task AssertImageAsync(HttpClient viewer, string url, int width, int height)
    {
        using var response = await viewer.GetAsync(url, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await DescribeAsync(response));
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var image = Image.NewFromBuffer(await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal((width, height), (image.Width, image.Height));
    }

    private static async Task AssertUnpublishedAsync(HttpClient viewer, Moment moment)
    {
        Assert.DoesNotContain(await ReadMomentsAsync(viewer), item => item.Id == moment.Id);
        using var image = await viewer.GetAsync(moment.ImageUrl, Ct);
        using var thumbnail = await viewer.GetAsync(moment.ThumbnailUrl, Ct);
        Assert.Equal(HttpStatusCode.NotFound, image.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, thumbnail.StatusCode);
    }

    private static async Task<string> DescribeAsync(HttpResponseMessage response) =>
        $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}";

    private sealed record Moment(Guid Id, Guid PlayerId, string Caption, string ImageUrl, string ThumbnailUrl);
}

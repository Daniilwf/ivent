using System.Net;
using GameEvent.Web.Tests.Api;
using Microsoft.EntityFrameworkCore;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.PlayerPreview;

public sealed partial class PlayerPreviewApiTests
{
    [Fact]
    public async Task An_own_run_proof_has_scoped_uncacheable_original_and_thumbnail_urls()
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        var fileId = await CompleteAndProveAsync(vasya);

        using var response = await SendScopedAsync(admin, HttpMethod.Get, Preview(_site.Players["vasya"]));
        var preview = await OkAsync(response);
        var proof = preview.GetProperty("season").GetProperty("me").GetProperty("lastCompleted").GetProperty("proof");
        var file = Assert.Single(proof.GetProperty("files").EnumerateArray());

        Assert.Equal("pending", proof.GetProperty("status").GetString());
        Assert.Equal(fileId, file.GetProperty("id").GetGuid());
        Assert.Equal(PreviewFile(_site.Players["vasya"], fileId), file.GetProperty("url").GetString());
        Assert.Equal(PreviewFile(_site.Players["vasya"], fileId) + "/thumbnail", file.GetProperty("thumbnailUrl").GetString());
        foreach (var url in new[] { file.GetProperty("url").GetString()!, file.GetProperty("thumbnailUrl").GetString()! })
        {
            using var picture = await SendScopedAsync(admin, HttpMethod.Get, url);
            await AssertPictureAsync(picture);
        }
    }

    [Fact]
    public async Task A_pending_irl_proof_keeps_its_private_materials_but_rewrites_both_picture_urls()
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        await EventSeason.SetUpAsync(admin, EventSeason.Karaoke);
        await EventSeason.DropAsync(vasya);
        var effectId = (await EventSeason.SingleEffectAsync(vasya)).GetProperty("id").GetGuid();
        var fileId = await EventSeason.UploadAsync(vasya);
        const string Link = "https://proof.example/private-karaoke-material";
        await PostOkAsync(vasya, $"{Season}/effects/{effectId}/irl-proof", new
        {
            commandId = Guid.NewGuid(),
            files = new[] { fileId },
            link = Link,
        });

        using var response = await SendScopedAsync(admin, HttpMethod.Get, Preview(_site.Players["vasya"]));
        var preview = await OkAsync(response);
        var effect = Assert.Single(preview.GetProperty("season").GetProperty("me").GetProperty("manualEffects").EnumerateArray());
        var proof = effect.GetProperty("irlProof");
        var file = Assert.Single(proof.GetProperty("files").EnumerateArray());

        Assert.Equal(effectId, effect.GetProperty("id").GetGuid());
        Assert.Equal("karaoke", effect.GetProperty("objectId").GetString());
        Assert.Equal("pending", proof.GetProperty("status").GetString());
        Assert.Equal(Link, proof.GetProperty("link").GetString());
        Assert.Equal(fileId, file.GetProperty("id").GetGuid());
        Assert.Equal(PreviewFile(_site.Players["vasya"], fileId), file.GetProperty("url").GetString());
        Assert.Equal(PreviewFile(_site.Players["vasya"], fileId) + "/thumbnail", file.GetProperty("thumbnailUrl").GetString());
        using var ownPicture = await SendScopedAsync(admin, HttpMethod.Get, file.GetProperty("url").GetString()!);
        await AssertPictureAsync(ownPicture);
        using var foreignPicture = await SendScopedAsync(admin, HttpMethod.Get, PreviewFile(_site.Players["petya"], fileId));
        Assert.Equal(HttpStatusCode.NotFound, foreignPicture.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_from_another_players_proof_is_outside_the_selected_scope(bool thumbnail)
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        using var petya = await SignInAsync(host, "petya");
        var fileId = await CompleteAndProveAsync(petya);

        using var response = await SendScopedAsync(admin, HttpMethod.Get,
            PreviewFile(_site.Players["vasya"], fileId) + (thumbnail ? "/thumbnail" : ""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Even_an_own_upload_is_not_a_preview_file_until_it_is_bound_to_an_own_proof()
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        var fileId = await EventSeason.UploadAsync(vasya);

        using var response = await SendScopedAsync(admin, HttpMethod.Get, PreviewFile(_site.Players["vasya"], fileId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_soft_deleted_proof_file_is_not_served_by_preview()
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        var fileId = await CompleteAndProveAsync(vasya);
        await using (var db = _site.NewDb())
        {
            // File deletion has no command yet; arrange the soft-deleted row as the existing file tests do.
            await db.Files.Where(file => file.Id == fileId).ExecuteUpdateAsync(update => update.SetProperty(file => file.IsDeleted, true), Ct);
        }

        using var response = await SendScopedAsync(admin, HttpMethod.Get, PreviewFile(_site.Players["vasya"], fileId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Another_seasons_proof_is_denied_even_for_the_same_account()
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        var otherSeason = await _site.CreateSeasonAsync();
        await PostOkAsync(admin, $"/api/admin/seasons/{otherSeason}/players", new { commandId = Guid.NewGuid(), userId = _site.Users["vasya"] });
        await PostOkAsync(admin, $"/api/admin/seasons/{otherSeason}/status", new { commandId = Guid.NewGuid(), to = "active" });
        var otherPlayer = Assert.Single((await ReadJsonAsync(admin, $"/api/admin/seasons/{otherSeason}/players")).EnumerateArray())
            .GetProperty("id").GetGuid();
        var fileId = await CompleteAndProveAsync(vasya, otherSeason);

        using var wrong = await SendScopedAsync(admin, HttpMethod.Get, PreviewFile(_site.Players["vasya"], fileId));
        using var correct = await SendScopedAsync(admin, HttpMethod.Get,
            $"/api/admin/seasons/{otherSeason}/players/{otherPlayer}/preview/files/{fileId}");

        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        await AssertPictureAsync(correct);
    }

    [Fact]
    public async Task A_shared_coop_proof_remains_readable_from_the_legitimate_partners_scope()
    {
        await GameEvent.Web.Tests.Queue.EventsTestSupport.CoopOnAsync(_site);
        var (runId, _) = await InteractionSeason.JoinedAsync(_site);
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        await PostOkAsync(vasya, $"{Season}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });
        var fileId = await EventSeason.UploadAsync(vasya);
        await PostOkAsync(vasya, $"{Season}/runs/{runId}/proof", new
        {
            commandId = Guid.NewGuid(),
            links = Array.Empty<string>(),
            files = new[] { fileId },
        });

        using var partnerPicture = await SendScopedAsync(admin, HttpMethod.Get, PreviewFile(_site.Players["petya"], fileId));

        await AssertPictureAsync(partnerPicture);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("vasya", HttpStatusCode.Forbidden)]
    [InlineData("zritel", HttpStatusCode.Forbidden)]
    public async Task Scoped_picture_urls_do_not_grant_non_admins_access(string? login, HttpStatusCode expected)
    {
        using var host = WithPreview();
        using var vasya = await SignInAsync(host, "vasya");
        var fileId = await CompleteAndProveAsync(vasya);
        using var client = login is null ? host.CreateClient() : await SignInAsync(host, login);

        using var response = await SendScopedAsync(client, HttpMethod.Get, PreviewFile(_site.Players["vasya"], fileId));

        Assert.Equal(expected, response.StatusCode);
    }

    private static string PreviewFile(Guid playerId, Guid fileId) => $"{Preview(playerId)}/files/{fileId}";

    private static async Task<Guid> CompleteAndProveAsync(HttpClient player, Guid? seasonId = null)
    {
        var season = $"/api/seasons/{seasonId ?? SiteFactory.SeasonId}";
        await PostOkAsync(player, $"{season}/roll");
        await PostOkAsync(player, $"{season}/start");
        await PostOkAsync(player, $"{season}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });
        var run = (await ReadJsonAsync(player, season)).GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetGuid();
        var fileId = await EventSeason.UploadAsync(player);
        await PostOkAsync(player, $"{season}/runs/{run}/proof", new
        {
            commandId = Guid.NewGuid(),
            links = Array.Empty<string>(),
            files = new[] { fileId },
        });
        return fileId;
    }

    private static async Task AssertPictureAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("immutable", response.Headers.CacheControl!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync(Ct));
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using static GameEvent.Web.Tests.Api.EventSeason;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// IRL proofs through the API (EV12, task 5.18, D-509, D-686, D-820): the owner of an IRL effect sends his own uploaded
/// pictures and / or one https link (<c>POST /api/seasons/{id}/effects/{effectId}/irl-proof</c>), the admin sees the
/// queue (<c>GET /api/admin/seasons/{id}/irl-proofs</c>) and checks it
/// (<c>POST /api/admin/seasons/{id}/effects/{effectId}/irl-proof/review</c>); the proof is the owner's and the admin's,
/// others see that it was sent. The IRL effect is «Караоке» of CONTENT.md, drawn by a drop from the bad deck.
/// </summary>
public sealed class IrlProofApiTests : IAsyncLifetime
{
    private const string Video = "https://youtu.be/karaoke-night";

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SubmitUrl(Guid effectId) => $"{Season}/effects/{effectId}/irl-proof";

    private static string ReviewUrl(Guid effectId) => $"{Admin}/effects/{effectId}/irl-proof/review";

    private static string QueueUrl => $"{Admin}/irl-proofs";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- The view of an IRL effect ----

    [Fact]
    public async Task An_irl_effect_is_marked_irl_in_its_owners_view_and_waits_for_a_proof()
    {
        var (_, vasya) = await KaraokeAsync();

        var effect = await SingleEffectAsync(vasya);

        Assert.Equal("karaoke", effect.GetProperty("objectId").GetString());
        Assert.True(effect.GetProperty("irl").GetBoolean());
        Assert.Equal(JsonValueKind.Null, effect.GetProperty("irlProof").ValueKind);
    }

    [Fact]
    public async Task A_manual_event_without_a_media_proof_is_not_irl()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Toast);
        await DropAsync(vasya);

        var effect = await SingleEffectAsync(vasya);

        Assert.Equal("toast", effect.GetProperty("objectId").GetString());
        Assert.False(effect.GetProperty("irl").GetBoolean());
    }

    // ---- Success ----

    [Fact]
    public async Task The_owner_sends_his_pictures_and_a_link_and_sees_the_proof_waiting()
    {
        var (_, vasya) = await KaraokeAsync();
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();
        var picture = await UploadAsync(vasya);

        var response = await PostOkAsync(vasya, SubmitUrl(effectId), new { commandId = Guid.NewGuid(), files = new[] { picture }, link = Video });

        Assert.Equal(["irl-proof-submitted"], await TypesAsync(response));
        var proof = (await SingleEffectAsync(vasya)).GetProperty("irlProof");
        Assert.Equal("pending", proof.GetProperty("status").GetString());
        Assert.Equal(Video, proof.GetProperty("link").GetString());
        var file = Assert.Single(proof.GetProperty("files").EnumerateArray());
        Assert.Equal((picture, $"/api/files/{picture}"), (file.GetProperty("id").GetGuid(), file.GetProperty("url").GetString()));
    }

    [Fact]
    public async Task A_link_alone_proves_and_pictures_alone_prove()
    {
        var (_, vasya) = await KaraokeAsync();
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();

        await PostOkAsync(vasya, SubmitUrl(effectId), new { commandId = Guid.NewGuid(), link = Video });

        var proof = (await SingleEffectAsync(vasya)).GetProperty("irlProof");
        Assert.Equal("pending", proof.GetProperty("status").GetString());
        Assert.Empty(proof.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public async Task The_admin_queue_lists_the_waiting_proof_with_its_player_event_files_and_link()
    {
        var (admin, vasya) = await KaraokeAsync();
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();
        var picture = await UploadAsync(vasya);
        await PostOkAsync(vasya, SubmitUrl(effectId), new { commandId = Guid.NewGuid(), files = new[] { picture }, link = Video });

        var item = Assert.Single((await ApiCalls.OkAsync(await admin.GetAsync(QueueUrl, Ct))).EnumerateArray());

        Assert.Equal(effectId, item.GetProperty("effectId").GetGuid());
        Assert.Equal(_site.Players["vasya"], item.GetProperty("playerId").GetGuid());
        Assert.Equal("vasya", item.GetProperty("playerName").GetString());
        Assert.Equal(("karaoke", "Караоке"), (item.GetProperty("objectId").GetString(), item.GetProperty("name").GetString()));
        Assert.StartsWith("Спой песню", item.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal(Video, item.GetProperty("link").GetString());
        Assert.Equal(picture, Assert.Single(item.GetProperty("files").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(_site.Clock.UtcNow, item.GetProperty("submittedAt").GetDateTimeOffset());

        // The admin opens the picture
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/files/{picture}", Ct)).StatusCode);
    }

    [Fact]
    public async Task An_approval_resolves_the_effect_and_empties_the_queue()
    {
        var (admin, vasya) = await KaraokeAsync();
        var effectId = await SubmittedAsync(vasya);

        var response = await PostOkAsync(admin, ReviewUrl(effectId), new { commandId = Guid.NewGuid(), approved = true });

        Assert.Equal(["irl-proof-reviewed", "manual-effect-resolved"], await TypesAsync(response));
        Assert.Empty((await MeAsync(vasya)).GetProperty("manualEffects").EnumerateArray());
        Assert.Empty((await ApiCalls.OkAsync(await admin.GetAsync(QueueUrl, Ct))).EnumerateArray());
    }

    [Fact]
    public async Task A_reject_with_a_comment_leaves_the_effect_waiting_and_shows_the_comment_to_its_owner()
    {
        var (admin, vasya) = await KaraokeAsync();
        var effectId = await SubmittedAsync(vasya);

        await PostOkAsync(admin, ReviewUrl(effectId), new { commandId = Guid.NewGuid(), approved = false, comment = "Видео закрыто" });

        var proof = (await SingleEffectAsync(vasya)).GetProperty("irlProof");
        Assert.Equal(("rejected", "Видео закрыто"), (proof.GetProperty("status").GetString(), proof.GetProperty("comment").GetString()));
        Assert.Empty((await ApiCalls.OkAsync(await admin.GetAsync(QueueUrl, Ct))).EnumerateArray());

        // After a reject the player sends a new proof
        await PostOkAsync(vasya, SubmitUrl(effectId), new { commandId = Guid.NewGuid(), link = "https://youtu.be/karaoke-again" });
        Assert.Equal("pending", (await SingleEffectAsync(vasya)).GetProperty("irlProof").GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_same_command_id_twice_sends_one_proof()
    {
        var (_, vasya) = await KaraokeAsync();
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();
        var commandId = Guid.NewGuid();

        await PostOkAsync(vasya, SubmitUrl(effectId), new { commandId, link = Video });
        await PostOkAsync(vasya, SubmitUrl(effectId), new { commandId, link = Video });

        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Events.CountAsync(e => e.Type == "irl-proof-submitted", Ct));
    }

    [Fact]
    public async Task A_proof_is_accepted_at_the_maximum_of_pictures_of_the_rules()
    {
        var (_, vasya) = await KaraokeAsync(maxFiles: 2);
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();
        Guid[] pictures = [await UploadAsync(vasya), await UploadAsync(vasya)];

        await PostOkAsync(vasya, SubmitUrl(effectId), new { commandId = Guid.NewGuid(), files = pictures });

        Assert.Equal(2, (await SingleEffectAsync(vasya)).GetProperty("irlProof").GetProperty("files").GetArrayLength());
    }

    // ---- Visibility: the proof is the owner's and the admin's (D-684, D-686) ----

    [Fact]
    public async Task Others_see_neither_the_link_nor_the_pictures_in_the_season_view_and_the_feed()
    {
        var (_, vasya) = await KaraokeAsync();
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();
        var picture = await UploadAsync(vasya);
        await PostOkAsync(vasya, SubmitUrl(effectId), new { commandId = Guid.NewGuid(), files = new[] { picture }, link = Video });

        foreach (var login in new[] { "petya", "zritel" })
        {
            var other = await _site.SignedInAsync(login);
            var season = await RawSeasonAsync(other);
            Assert.DoesNotContain(Video, season, StringComparison.Ordinal);
            Assert.DoesNotContain(picture.ToString(), season, StringComparison.OrdinalIgnoreCase);

            var feed = await ApiCalls.OkAsync(await other.GetAsync($"{Season}/feed", Ct));
            var submitted = Assert.Single(feed.GetProperty("entries").EnumerateArray(), e => e.GetProperty("type").GetString() == "irl-proof-submitted");
            Assert.DoesNotContain(Video, submitted.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(picture.ToString(), submitted.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- Rule refusals: 409 ----

    [Fact]
    public async Task Another_players_irl_effect_is_refused()
    {
        var (_, vasya) = await KaraokeAsync();
        var petya = await _site.SignedInAsync("petya");
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();

        var response = await petya.PostAsJsonAsync(SubmitUrl(effectId), new { commandId = Guid.NewGuid(), link = Video }, Ct);

        await AssertConflictAsync(response, "effect.notYours");
        Assert.Equal(JsonValueKind.Null, (await SingleEffectAsync(vasya)).GetProperty("irlProof").ValueKind);
    }

    [Fact]
    public async Task Someone_elses_picture_is_refused_before_the_command()
    {
        var (_, vasya) = await KaraokeAsync();
        var petya = await _site.SignedInAsync("petya");
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();
        var petyasPicture = await UploadAsync(petya);

        var response = await vasya.PostAsJsonAsync(SubmitUrl(effectId), new { commandId = Guid.NewGuid(), files = new[] { petyasPicture } }, Ct);

        await AssertConflictAsync(response, "proof.invalidFile");
        Assert.Equal(JsonValueKind.Null, (await SingleEffectAsync(vasya)).GetProperty("irlProof").ValueKind);
    }

    [Fact]
    public async Task An_unknown_or_deleted_picture_is_refused()
    {
        var (_, vasya) = await KaraokeAsync();
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();
        var deleted = await UploadAsync(vasya);
        await using (var db = _site.NewDb())
        {
            await db.Files.Where(f => f.Id == deleted).ExecuteUpdateAsync(f => f.SetProperty(x => x.IsDeleted, true), Ct);
        }

        await AssertConflictAsync(
            await vasya.PostAsJsonAsync(SubmitUrl(effectId), new { commandId = Guid.NewGuid(), files = new[] { Guid.NewGuid() } }, Ct),
            "proof.invalidFile");
        await AssertConflictAsync(
            await vasya.PostAsJsonAsync(SubmitUrl(effectId), new { commandId = Guid.NewGuid(), files = new[] { deleted } }, Ct),
            "proof.invalidFile");
    }

    [Fact]
    public async Task The_same_picture_twice_is_refused()
    {
        var (_, vasya) = await KaraokeAsync();
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();
        var picture = await UploadAsync(vasya);

        var response = await vasya.PostAsJsonAsync(SubmitUrl(effectId), new { commandId = Guid.NewGuid(), files = new[] { picture, picture } }, Ct);

        await AssertRefusedAsync(response);
        Assert.Equal(JsonValueKind.Null, (await SingleEffectAsync(vasya)).GetProperty("irlProof").ValueKind);
    }

    [Fact]
    public async Task More_pictures_than_the_rules_allow_are_refused()
    {
        var (_, vasya) = await KaraokeAsync(maxFiles: 2);
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();
        Guid[] pictures = [await UploadAsync(vasya), await UploadAsync(vasya), await UploadAsync(vasya)];

        var response = await vasya.PostAsJsonAsync(SubmitUrl(effectId), new { commandId = Guid.NewGuid(), files = pictures }, Ct);

        await AssertConflictAsync(response, "irlProof.invalidFile");
    }

    [Fact]
    public async Task A_second_proof_while_one_waits_is_refused()
    {
        var (_, vasya) = await KaraokeAsync();
        var effectId = await SubmittedAsync(vasya);

        var response = await vasya.PostAsJsonAsync(SubmitUrl(effectId), new { commandId = Guid.NewGuid(), link = "https://youtu.be/another" }, Ct);

        await AssertConflictAsync(response, "irlProof.pending");
    }

    [Fact]
    public async Task A_proof_for_an_effect_that_is_not_irl_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Toast);
        await DropAsync(vasya);
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();

        var response = await vasya.PostAsJsonAsync(SubmitUrl(effectId), new { commandId = Guid.NewGuid(), link = Video }, Ct);

        await AssertConflictAsync(response, "irlProof.notIrl");
    }

    [Fact]
    public async Task The_owner_cannot_resolve_an_irl_effect_himself()
    {
        var (_, vasya) = await KaraokeAsync();
        var effectId = await SubmittedAsync(vasya);

        var response = await vasya.PostAsJsonAsync($"{Season}/effects/{effectId}/resolve", new { commandId = Guid.NewGuid(), outcome = "applied" }, Ct);

        await AssertConflictAsync(response, "effect.adminOnly");
        Assert.Single((await MeAsync(vasya)).GetProperty("manualEffects").EnumerateArray());
    }

    [Fact]
    public async Task A_review_without_a_submitted_proof_is_refused()
    {
        var (admin, vasya) = await KaraokeAsync();
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();

        var response = await admin.PostAsJsonAsync(ReviewUrl(effectId), new { commandId = Guid.NewGuid(), approved = true }, Ct);

        await AssertConflictAsync(response, "irlProof.notSubmitted");
    }

    [Fact]
    public async Task A_reject_without_a_comment_is_refused()
    {
        var (admin, vasya) = await KaraokeAsync();
        var effectId = await SubmittedAsync(vasya);

        await AssertRefusedAsync(await admin.PostAsJsonAsync(ReviewUrl(effectId), new { commandId = Guid.NewGuid(), approved = false }, Ct));
        await AssertRefusedAsync(await admin.PostAsJsonAsync(ReviewUrl(effectId), new { commandId = Guid.NewGuid(), approved = false, comment = "  " }, Ct));

        Assert.Equal("pending", (await SingleEffectAsync(vasya)).GetProperty("irlProof").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Events_cannot_be_switched_off_while_an_irl_proof_waits()
    {
        var (admin, vasya) = await KaraokeAsync();
        await SubmittedAsync(vasya);

        var response = await TrySwitchEventsAsync(admin, on: false);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("irlProof.inPlay", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_events_off_a_proof_and_a_review_are_refused_by_the_flag()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchEventsAsync(admin, on: false);

        await AssertConflictAsync(
            await vasya.PostAsJsonAsync(SubmitUrl(Guid.NewGuid()), new { commandId = Guid.NewGuid(), link = Video }, Ct),
            "feature.disabled");
        await AssertConflictAsync(
            await admin.PostAsJsonAsync(ReviewUrl(Guid.NewGuid()), new { commandId = Guid.NewGuid(), approved = true }, Ct),
            "feature.disabled");
    }

    // ---- Roles: 403 / 401 / 404 ----

    [Fact]
    public async Task A_spectator_cannot_send_a_proof_before_the_files_are_looked_at()
    {
        var (_, vasya) = await KaraokeAsync();
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();
        var zritel = await _site.SignedInAsync("zritel");

        var response = await zritel.PostAsJsonAsync(SubmitUrl(effectId), new { commandId = Guid.NewGuid(), files = new[] { Guid.NewGuid() } }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("petya")]
    [InlineData("zritel")]
    public async Task Players_and_spectators_neither_see_the_queue_nor_review(string login)
    {
        var (_, vasya) = await KaraokeAsync();
        var effectId = await SubmittedAsync(vasya);
        var client = await _site.SignedInAsync(login);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(QueueUrl, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync(ReviewUrl(effectId), new { commandId = Guid.NewGuid(), approved = true }, Ct)).StatusCode);
        Assert.Equal("pending", (await SingleEffectAsync(vasya)).GetProperty("irlProof").GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_anonymous_are_unauthorized()
    {
        var client = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(SubmitUrl(Guid.NewGuid()), new { commandId = Guid.NewGuid(), link = Video }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(QueueUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(ReviewUrl(Guid.NewGuid()), new { commandId = Guid.NewGuid(), approved = true }, Ct)).StatusCode);
    }

    [Fact]
    public async Task The_queue_of_an_unknown_season_is_not_found()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.GetAsync($"/api/admin/seasons/{Guid.NewGuid()}/irl-proofs", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_post_without_the_antiforgery_token_is_refused()
    {
        var (_, vasya) = await KaraokeAsync();
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();
        vasya.DefaultRequestHeaders.Remove(Hosting.Csrf.HeaderName);

        var response = await vasya.PostAsJsonAsync(SubmitUrl(effectId), new { commandId = Guid.NewGuid(), link = Video }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Invalid input: 400 ----

    public static TheoryData<string> BadLinks => new()
    {
        "http://youtu.be/karaoke",
        "ftp://example.com/karaoke.mp4",
        "javascript:alert(1)",
        "https://user:pass@youtu.be/karaoke",
        "https://youtu.be/kara‮oke",
        "https:\\\\youtu.be\\karaoke",
        "https://youtu.be/karaoke https://other.example",
        "not a link",
    };

    [Theory]
    [MemberData(nameof(BadLinks))]
    public async Task A_link_that_is_not_one_https_address_is_invalid_input(string link)
    {
        var (_, vasya) = await KaraokeAsync();
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();

        var response = await vasya.PostAsJsonAsync(SubmitUrl(effectId), new { commandId = Guid.NewGuid(), link }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await SingleEffectAsync(vasya)).GetProperty("irlProof").ValueKind);
    }

    [Fact]
    public async Task A_link_over_the_length_limit_is_invalid_input()
    {
        var (_, vasya) = await KaraokeAsync();
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();

        var response = await vasya.PostAsJsonAsync(SubmitUrl(effectId), new { commandId = Guid.NewGuid(), link = "https://youtu.be/" + new string('a', 3000) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_proof_with_neither_pictures_nor_a_link_is_refused()
    {
        var (_, vasya) = await KaraokeAsync();
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();

        await AssertRefusedAsync(await vasya.PostAsJsonAsync(SubmitUrl(effectId), new { commandId = Guid.NewGuid() }, Ct));
        await AssertRefusedAsync(await vasya.PostAsJsonAsync(SubmitUrl(effectId), new { commandId = Guid.NewGuid(), files = Array.Empty<Guid>(), link = "" }, Ct));

        Assert.Equal(JsonValueKind.Null, (await SingleEffectAsync(vasya)).GetProperty("irlProof").ValueKind);
    }

    [Fact]
    public async Task A_proof_or_a_review_without_a_command_id_is_invalid_input()
    {
        var (admin, vasya) = await KaraokeAsync();
        var effectId = await SubmittedAsync(vasya);

        Assert.Equal(HttpStatusCode.BadRequest, (await vasya.PostAsJsonAsync(SubmitUrl(effectId), new { commandId = Guid.Empty, files = new[] { Guid.NewGuid() } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(ReviewUrl(effectId), new { approved = true }, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_review_without_a_verdict_or_with_a_long_comment_is_invalid_input()
    {
        var (admin, vasya) = await KaraokeAsync();
        var effectId = await SubmittedAsync(vasya);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(ReviewUrl(effectId), new { commandId = Guid.NewGuid(), comment = "?" }, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync(ReviewUrl(effectId), new { commandId = Guid.NewGuid(), approved = false, comment = new string('я', 501) }, Ct)).StatusCode);
        Assert.Equal("pending", (await SingleEffectAsync(vasya)).GetProperty("irlProof").GetProperty("status").GetString());
    }

    // ---- Helpers ----

    /// <summary>Events on with «Караоке» in the bad deck; Вася drops and has the IRL effect.</summary>
    private async Task<(HttpClient Admin, HttpClient Vasya)> KaraokeAsync(int maxFiles = 5)
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Karaoke, maxFiles);
        await DropAsync(vasya);
        return (admin, vasya);
    }

    /// <summary>Вася sends a link for his IRL effect; returns the effect.</summary>
    private static async Task<Guid> SubmittedAsync(HttpClient vasya)
    {
        var effectId = (await SingleEffectAsync(vasya)).GetProperty("id").GetGuid();
        await PostOkAsync(vasya, SubmitUrl(effectId), new { commandId = Guid.NewGuid(), link = Video });
        return effectId;
    }
}

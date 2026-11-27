using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetVips;
using static GameEvent.Web.Tests.Api.ApiCalls;
using static GameEvent.Web.Tests.Api.PollSeason;
using ArchiveTransfer = GameEvent.Infrastructure.Seasons.SeasonTransfer;

namespace GameEvent.Web.Tests.Api;

public sealed class NominationResultApiTests : IAsyncLifetime
{
    private const string WinnerCaption = "A photo from the final vote";
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task An_unpublished_or_legacy_season_has_no_nomination_snapshot()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var spectator = await _site.SignedInAsync("zritel");
        using var unknown = await spectator.GetAsync($"/api/seasons/{Guid.NewGuid()}/nominations", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        await AssertUnpublishedAsync(spectator);

        await SetStatusAsync(admin, "closing");
        await AssertUnpublishedAsync(spectator);
        await SetStatusAsync(admin, "finished");
        await AssertUnpublishedAsync(spectator);
        await SetStatusAsync(admin, "archived");
        await AssertUnpublishedAsync(spectator);
    }

    [Fact]
    public async Task The_archived_snapshot_survives_hidden_media_and_zip_replay()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync("vasya");
        using var spectator = await _site.SignedInAsync("zritel");
        using var anonymous = _site.CreateClient();
        await EnableNominationsAsync(admin);
        var first = await PublishMomentAsync(player, WinnerCaption);
        var second = await PublishMomentAsync(player, "Another finalist");
        var runId = await CompleteAsync(player);
        await SetStatusAsync(admin, "closing");
        await PostOkAsync(admin, AdminUrl($"runs/{runId}/approve"),
            new { commandId = Guid.NewGuid(), comment = "Proof checked" });
        var pollId = await StartBallotAsync(admin, first, second);
        await VoteOkAsync(player, pollId, first.ToString("D"));
        await PostOkAsync(admin, AdminUrl($"polls/{pollId}/close"));
        await AssertUnpublishedAsync(spectator);
        await SetStatusAsync(admin, "finished");

        using var unsigned = await anonymous.GetAsync(Url("nominations"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        var finished = await ResultsAsync(spectator);
        var winner = Assert.Single(finished.GetProperty("voted").EnumerateArray()).GetProperty("winner");
        Assert.Equal(first, winner.GetProperty("id").GetGuid());
        Assert.Equal(WinnerCaption, winner.GetProperty("label").GetString());
        Assert.EndsWith($"/gallery/{first}/thumbnail", winner.GetProperty("thumbnailUrl").GetString());
        Assert.Equal(_site.Users["vasya"], MostRunsWinner(finished).GetProperty("profileUserId").GetGuid());

        await SetStatusAsync(admin, "archived");
        Assert.Equal(finished.GetRawText(), (await ResultsAsync(spectator)).GetRawText());
        await PostOkAsync(admin, AdminUrl($"gallery/{first}/hide"));
        var hidden = await ResultsAsync(spectator);
        AssertVotedResult(hidden, pollId, hasWinner: true);
        Assert.DoesNotContain(WinnerCaption, hidden.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain($"/gallery/{first}", hidden.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(finished.GetProperty("automatic").GetRawText(), hidden.GetProperty("automatic").GetRawText());

        await using var db = _site.NewDb();
        var exported = await ArchiveTransfer.ExportAsync(db, SiteFactory.SeasonId, _site.Clock.UtcNow, Ct);
        Assert.NotNull(exported);
        using var zip = new MemoryStream();
        await ArchiveTransfer.WriteZipAsync(exported, zip, Ct);
        zip.Position = 0;
        await using var other = new SiteFactory();
        await other.SeedAsync(withSeason: false);
        await using var importedDb = other.NewDb();
        var imported = await ArchiveTransfer.ImportAsync(importedDb,
            await ArchiveTransfer.ReadZipAsync(zip, Ct), other.Clock.UtcNow, ct: Ct);
        Assert.True(imported.Integrity.IsIntact, string.Join(", ", imported.Integrity.Differences));
        using var otherSpectator = await other.SignedInAsync("zritel");
        var restored = await ResultsAsync(otherSpectator);
        AssertVotedResult(restored, pollId, hasWinner: true);
        Assert.Equal(other.Users["vasya"], MostRunsWinner(restored).GetProperty("profileUserId").GetGuid());

        await PostOkAsync(admin, $"/api/admin/accounts/{_site.Users["vasya"]}/delete");
        var deletedProfile = MostRunsWinner(await ResultsAsync(spectator));
        Assert.Equal(MostRunsWinner(finished).GetProperty("name").GetString(), deletedProfile.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, deletedProfile.GetProperty("profileUserId").ValueKind);
    }

    [Fact]
    public async Task A_voted_nomination_with_no_votes_has_no_winner()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync("vasya");
        await EnableNominationsAsync(admin);
        var first = await PublishMomentAsync(player, "First nominee");
        var second = await PublishMomentAsync(player, "Second nominee");
        await SetStatusAsync(admin, "closing");
        var pollId = await StartBallotAsync(admin, first, second);
        await PostOkAsync(admin, AdminUrl($"polls/{pollId}/close"));
        await SetStatusAsync(admin, "finished");

        AssertVotedResult(await ResultsAsync(player), pollId, hasWinner: false);
    }

    private static async Task<JsonElement> ResultsAsync(HttpClient viewer)
    {
        using var response = await viewer.GetAsync(Url("nominations"), Ct);
        var result = await OkAsync(response);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return result;
    }

    private static async Task AssertUnpublishedAsync(HttpClient viewer)
    {
        using var response = await viewer.GetAsync(Url("nominations"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static void AssertVotedResult(JsonElement result, Guid pollId, bool hasWinner)
    {
        var voted = Assert.Single(result.GetProperty("voted").EnumerateArray());
        Assert.Equal("funniestMoment", voted.GetProperty("nomination").GetString());
        Assert.Equal(pollId, voted.GetProperty("pollId").GetGuid());
        Assert.Equal(hasWinner, voted.GetProperty("hasWinner").GetBoolean());
        Assert.Equal(JsonValueKind.Null, voted.GetProperty("winner").ValueKind);
    }

    private static JsonElement MostRunsWinner(JsonElement result)
    {
        var nomination = Assert.Single(result.GetProperty("automatic").EnumerateArray(),
            candidate => candidate.GetProperty("nomination").GetString() == "mostRuns");
        Assert.Equal(1m, nomination.GetProperty("value").GetDecimal());
        return Assert.Single(nomination.GetProperty("winners").EnumerateArray());
    }

    private static Task SetStatusAsync(HttpClient admin, string to) =>
        PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to });

    private static async Task<Guid> StartBallotAsync(HttpClient admin, Guid first, Guid second)
    {
        var pollId = Guid.NewGuid();
        await PostOkAsync(admin, AdminUrl("nominations/polls"), new
        {
            commandId = Guid.NewGuid(),
            pollId,
            category = "funniestMoment",
            candidateIds = new[] { first, second },
        });
        return pollId;
    }

    private static async Task EnableNominationsAsync(HttpClient admin)
    {
        using var current = await admin.GetAsync(Url("rules"), Ct);
        var view = await OkAsync(current);
        var rules = JsonNode.Parse(view.GetProperty("ruleset").GetRawText())!;
        var features = rules["features"]!;
        features["polls"] = true;
        features["gallery"] = true;
        features["seasonNominations"] = true;
        var nominations = rules["nominations"]!;
        nominations["durationHours"] = 3;
        nominations["minCandidates"] = 2;
        nominations["maxCandidates"] = 20;
        using var updated = await admin.PutAsJsonAsync(AdminUrl("rules"), new
        {
            commandId = Guid.NewGuid(),
            expectedVersion = view.GetProperty("version").GetInt32(),
            ruleset = rules,
        }, Ct);
        await OkAsync(updated);
    }

    private static async Task<Guid> CompleteAsync(HttpClient player)
    {
        await PostOkAsync(player, Url("roll"));
        await PostOkAsync(player, Url("start"));
        await PostOkAsync(player, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        using var current = await player.GetAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct);
        var season = await OkAsync(current);
        return season.GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetGuid();
    }

    private static async Task<Guid> PublishMomentAsync(HttpClient player, string caption)
    {
        var itemId = Guid.NewGuid();
        using var image = Image.Black(32, 24, bands: 3);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Guid.NewGuid().ToString()), "commandId" },
            { new StringContent(itemId.ToString()), "itemId" },
            { new StringContent(caption), "caption" },
        };
        var file = new ByteArrayContent(image.PngsaveBuffer());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "moment.png");
        using var published = await player.PostAsync(Url("gallery"), form, Ct);
        Assert.True(published.StatusCode == HttpStatusCode.Created, await published.Content.ReadAsStringAsync(Ct));
        return itemId;
    }
}

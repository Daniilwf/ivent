using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Seasons;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The player sends the proof of a completed run over HTTP (C8, W4, D-98):
/// <c>POST /api/seasons/{id}/runs/{runId}/proof</c> {commandId, links[], note?, witnessId?}. Links are 0–5, each an
/// http/https link of at most 500 characters, the note at most 500 — breaking these is a bad request already at the
/// request level; rule refusals (someone else's run, an empty proof, an invalid witness, a checked proof, a closed
/// season) are 409 with the engine code. The season view shows the proof of the last completed run
/// (<c>me.lastCompleted.proof</c>: status, links, note). Players of the season only: spectators, the admin and players
/// outside the season are forbidden. Responses are read as raw JSON, so the tests compile before the DTOs exist.
/// </summary>
public sealed class ProofApiTests : IAsyncLifetime
{
    private const string Link = "https://imgur.com/a/credits";

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(string action, Guid? seasonId = null) => $"/api/seasons/{seasonId ?? SiteFactory.SeasonId}/{action}";

    private static string ProofUrl(Guid runId, Guid? seasonId = null) => Url($"runs/{runId}/proof", seasonId);

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- Success ----

    [Fact]
    public async Task Player_sends_a_proof_of_their_completed_run()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        var response = await PostOkAsync(vasya, ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { Link }, note = "Титры" });

        Assert.Equal(["proof-submitted"], await TypesAsync(response));
        await using var db = _site.NewDb();
        var row = await db.Proofs.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((runId, ProofStatus.Pending, (string?)"Титры"), (row.RunId, row.Status, row.Note));
        var logged = await db.Events.SingleAsync(e => e.Type == "proof-submitted", Ct);
        Assert.Equal(_site.Users["vasya"], logged.AuthorId);
        using var data = JsonDocument.Parse(logged.Data);
        Assert.Equal([Link], data.RootElement.GetProperty("links").EnumerateArray().Select(l => l.GetString()));
    }

    [Fact]
    public async Task Season_view_shows_the_proof_of_the_last_completed_run()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        var before = (await MeJsonAsync(vasya)).GetProperty("lastCompleted");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("proof").ValueKind);

        await PostOkAsync(vasya, ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { Link, "https://youtu.be/ending" }, note = "Титры" });

        var proof = (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("proof");
        Assert.Equal("pending", proof.GetProperty("status").GetString());
        Assert.Equal([Link, "https://youtu.be/ending"], proof.GetProperty("links").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal("Титры", proof.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Witness_alone_is_a_proof()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        await PostOkAsync(vasya, ProofUrl(runId), new { commandId = Guid.NewGuid(), links = Array.Empty<string>(), witnessId = _site.Players["petya"] });

        await using var db = _site.NewDb();
        Assert.Equal(_site.Players["petya"], (await db.Proofs.AsNoTracking().SingleAsync(Ct)).WitnessId);
    }

    [Fact]
    public async Task Witness_only_proof_is_logged_with_the_witness_and_no_links()
    {
        // D-98 (5): the screen sends {links: [], witnessId}; the API accepts it as a proof
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        var response = await PostOkAsync(vasya, ProofUrl(runId), new { commandId = Guid.NewGuid(), links = Array.Empty<string>(), witnessId = _site.Players["petya"] });

        Assert.Equal(["proof-submitted"], await TypesAsync(response));
        await using var db = _site.NewDb();
        var logged = await db.Events.SingleAsync(e => e.Type == "proof-submitted", Ct);
        using var data = JsonDocument.Parse(logged.Data);
        Assert.Equal(_site.Players["petya"], data.RootElement.GetProperty("witnessId").GetGuid());
        Assert.Equal(0, data.RootElement.GetProperty("links").GetArrayLength());
        var proof = (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("proof");
        Assert.Equal("pending", proof.GetProperty("status").GetString());
        Assert.Equal(0, proof.GetProperty("links").GetArrayLength());
    }

    [Fact]
    public async Task Links_and_note_are_stored_trimmed_and_a_blank_note_is_none()
    {
        // D-98 (4)
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        await PostOkAsync(vasya, ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { "  " + Link + " " }, note = "  Титры \n" });

        var proof = (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("proof");
        Assert.Equal([Link], proof.GetProperty("links").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal("Титры", proof.GetProperty("note").GetString());

        await PostOkAsync(vasya, ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { Link }, note = "   " });

        await using var db = _site.NewDb();
        Assert.Null((await db.Proofs.AsNoTracking().SingleAsync(Ct)).Note);
    }

    [Fact]
    public async Task Last_completed_run_status_is_completed_and_rejected_after_a_reject()
    {
        // The season view tells a rejected last run apart, so the screen shows the reject instead of the dice
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        Assert.Equal("completed", (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("status").GetString());

        await _site.SendAsync(new RejectProof(runId, "На скрине другая игра"));

        var last = (await MeJsonAsync(vasya)).GetProperty("lastCompleted");
        Assert.Equal(runId, last.GetProperty("id").GetGuid());
        Assert.Equal("rejected", last.GetProperty("status").GetString());
        Assert.Equal("rejected", last.GetProperty("proof").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Last_completed_run_status_stays_completed_after_an_approval()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        await _site.SendAsync(new ApproveProof(runId, Comment: "Видел на стриме"));

        Assert.Equal("completed", (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("status").GetString());
    }

    [Fact]
    public async Task New_proof_replaces_the_pending_one()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        await PostOkAsync(vasya, ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { Link } });
        await PostOkAsync(vasya, ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { "https://youtu.be/ending" } });

        await using var db = _site.NewDb();
        var row = await db.Proofs.AsNoTracking().SingleAsync(Ct);
        Assert.Contains("youtu.be/ending", row.LinksJson, StringComparison.Ordinal);
        Assert.DoesNotContain("imgur.com", row.LinksJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Five_links_of_500_characters_are_accepted()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        var links = Enumerable.Range(0, Limits.MaxProofLinks).Select(i => LinkOfLength(Limits.MaxProofLinkLength, i)).ToArray();

        await PostOkAsync(vasya, ProofUrl(runId), new { commandId = Guid.NewGuid(), links, note = new string('я', Limits.MaxCommentLength) });
    }

    [Fact]
    public async Task Proof_is_accepted_while_the_season_is_closing()
    {
        // SPEC «Сезон»: after the deadline rolls are forbidden, proofs are accepted
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        await _site.SendAsync(new ChangeSeasonStatus(SeasonStatus.Closing));

        var response = await vasya.PostAsJsonAsync(ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { Link } }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Engine rules: 409 ----

    [Fact]
    public async Task Another_players_run_is_a_conflict_with_not_yours()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        var petya = await _site.SignedInAsync("petya");

        var response = await petya.PostAsJsonAsync(ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { Link } }, Ct);

        await AssertConflictAsync(response, "run.notYours");
        await AssertNoProofEventsAsync();
    }

    [Fact]
    public async Task Run_being_played_is_a_conflict_with_not_completed()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });
        var runId = Guid.Parse((await MeJsonAsync(vasya)).GetProperty("activeRun").GetProperty("id").GetString()!);

        var response = await vasya.PostAsJsonAsync(ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { Link } }, Ct);

        await AssertConflictAsync(response, "run.notCompleted");
    }

    [Fact]
    public async Task Unknown_run_is_a_conflict_with_unknown_run()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(ProofUrl(Guid.NewGuid()), new { commandId = Guid.NewGuid(), links = new[] { Link } }, Ct);

        await AssertConflictAsync(response, "run.unknown");
    }

    [Fact]
    public async Task Proof_without_links_and_witness_is_a_conflict_with_empty()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        var response = await vasya.PostAsJsonAsync(ProofUrl(runId), new { commandId = Guid.NewGuid(), links = Array.Empty<string>(), note = "Верьте" }, Ct);

        await AssertConflictAsync(response, "proof.empty");
        await AssertNoProofEventsAsync();
    }

    [Fact]
    public async Task Own_witness_is_a_conflict_with_invalid_witness()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        var response = await vasya.PostAsJsonAsync(
            ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { Link }, witnessId = _site.Players["vasya"] }, Ct);

        await AssertConflictAsync(response, "proof.witnessInvalid");
    }

    [Fact]
    public async Task Witness_outside_the_season_is_a_conflict_with_invalid_witness()
    {
        // Маша has an account but is not in the season; a user id is not a participant id either
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        var response = await vasya.PostAsJsonAsync(
            ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { Link }, witnessId = _site.Users["masha"] }, Ct);

        await AssertConflictAsync(response, "proof.witnessInvalid");
    }

    [Fact]
    public async Task Proof_after_the_approval_is_a_conflict_with_already_reviewed()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        await _site.SendAsync(new ApproveProof(runId, Comment: "Видел на стриме"));

        var response = await vasya.PostAsJsonAsync(ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { Link } }, Ct);

        await AssertConflictAsync(response, "proof.alreadyReviewed");
    }

    [Fact]
    public async Task Proof_after_the_season_is_finished_is_a_conflict()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        await _site.SendAsync(new ChangeSeasonStatus(SeasonStatus.Closing));
        await _site.SendAsync(new ChangeSeasonStatus(SeasonStatus.Finished));

        var response = await vasya.PostAsJsonAsync(ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { Link } }, Ct);

        await AssertConflictAsync(response, "season.closed");
    }

    // ---- Idempotency ----

    [Fact]
    public async Task Repeating_with_the_same_command_id_acts_once()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        var body = new { commandId = Guid.NewGuid(), links = new[] { Link } };

        await PostOkAsync(vasya, ProofUrl(runId), body);
        var again = await PostOkAsync(vasya, ProofUrl(runId), body);

        using var repeat = JsonDocument.Parse(await again.Content.ReadAsStringAsync(Ct));
        Assert.True(repeat.RootElement.GetProperty("duplicate").GetBoolean());
        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Events.CountAsync(e => e.Type == "proof-submitted", Ct));
    }

    [Fact]
    public async Task Same_command_id_with_another_body_is_a_conflict()
    {
        // D-95: another body under a used id is refused and changes nothing
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        var commandId = Guid.NewGuid();

        await PostOkAsync(vasya, ProofUrl(runId), new { commandId, links = new[] { Link } });
        var second = await vasya.PostAsJsonAsync(ProofUrl(runId), new { commandId, links = new[] { "https://youtu.be/ending" } }, Ct);

        await AssertConflictAsync(second, "command.idReused");
        await using var db = _site.NewDb();
        Assert.DoesNotContain("youtu.be", (await db.Proofs.AsNoTracking().SingleAsync(Ct)).LinksJson, StringComparison.Ordinal);
    }

    // ---- Refused: roles, session, CSRF, season ----

    [Theory]
    [InlineData("zritel")]
    [InlineData("admin")]
    [InlineData("masha")]
    public async Task Spectator_admin_and_player_outside_the_season_are_forbidden(string login)
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        var client = await _site.SignedInAsync(login);

        var response = await client.PostAsJsonAsync(ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { Link } }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoProofEventsAsync();
    }

    [Fact]
    public async Task Anonymous_is_unauthorized()
    {
        var client = await _site.AnonymousAsync();

        var response = await client.PostAsJsonAsync(ProofUrl(Guid.NewGuid()), new { commandId = Guid.NewGuid(), links = new[] { Link } }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_without_the_antiforgery_token_is_refused()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);
        vasya.DefaultRequestHeaders.Remove(Hosting.Csrf.HeaderName);

        var response = await vasya.PostAsJsonAsync(ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { Link } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoProofEventsAsync();
    }

    [Fact]
    public async Task Unknown_season_is_not_found()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(ProofUrl(Guid.NewGuid(), Guid.NewGuid()), new { commandId = Guid.NewGuid(), links = new[] { Link } }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Run_id_that_is_not_a_guid_is_not_found()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(Url("runs/not-a-guid/proof"), new { commandId = Guid.NewGuid(), links = new[] { Link } }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Invalid input: 400 ----

    [Theory]
    [InlineData("""{"commandId":"00000000-0000-0000-0000-000000000000","links":["https://imgur.com/a/1"]}""")]
    [InlineData("""{"links":["https://imgur.com/a/1"]}""")]
    [InlineData("""{"commandId":"not-a-guid","links":["https://imgur.com/a/1"]}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000301"}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000302","links":null}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000303","links":"https://imgur.com/a/1"}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000304","links":[42]}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000305","links":[null]}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000306","links":["javascript:alert(1)"]}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000307","links":["ftp://example.com/credits.png"]}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000308","links":["/proofs/credits.png"]}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000309","links":["imgur.com/a/1"]}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000310","links":["https://imgur.com/a/1","data:text/html,x"]}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000311","links":["https://imgur.com/a/1"],"note":42}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000312","links":["https://imgur.com/a/1"],"witnessId":"not-a-guid"}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000313","links":["https://imgur.com/a/1","https://imgur.com/a/2","https://imgur.com/a/3","https://imgur.com/a/4","https://imgur.com/a/5","https://imgur.com/a/6"]}""")]
    [InlineData("not json")]
    public async Task Invalid_input_is_a_bad_request(string body)
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        var response = await vasya.PostAsync(ProofUrl(runId), new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoProofEventsAsync();
    }

    [Fact]
    public async Task Link_over_500_characters_is_a_bad_request()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        var response = await vasya.PostAsJsonAsync(
            ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { LinkOfLength(Limits.MaxProofLinkLength + 1, 0) } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoProofEventsAsync();
    }

    [Fact]
    public async Task Note_over_500_characters_is_a_bad_request()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var runId = await CompletedRunAsync(vasya);

        var response = await vasya.PostAsJsonAsync(
            ProofUrl(runId), new { commandId = Guid.NewGuid(), links = new[] { Link }, note = new string('я', Limits.MaxCommentLength + 1) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoProofEventsAsync();
    }

    // ---- Helpers ----

    private static string LinkOfLength(int length, int index)
    {
        var prefix = $"https://imgur.com/a/{index}/";
        return prefix + new string('x', length - prefix.Length);
    }

    private async Task<Guid> CompletedRunAsync(HttpClient client)
    {
        await PostOkAsync(client, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(client, Url("start"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(client, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        return Guid.Parse((await MeJsonAsync(client)).GetProperty("lastCompleted").GetProperty("id").GetString()!);
    }

    private async Task AssertNoProofEventsAsync()
    {
        await using var db = _site.NewDb();
        Assert.Equal(0, await db.Events.CountAsync(e => e.Type == "proof-submitted", Ct));
        Assert.Equal(0, await db.Proofs.CountAsync(Ct));
    }

    private static async Task<HttpResponseMessage> PostOkAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return response;
    }

    private static async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task<JsonElement> MeJsonAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        return doc.RootElement.GetProperty("me").Clone();
    }

    private static async Task<List<string>> TypesAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. doc.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()!)];
    }
}

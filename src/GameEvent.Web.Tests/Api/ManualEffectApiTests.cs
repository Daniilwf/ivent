using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Resolving manual effects over HTTP (C11a, E1, D-102): <c>POST /api/seasons/{id}/effects/{effectId}/resolve</c> by the
/// owner and <c>POST /api/admin/seasons/{id}/effects/{effectId}/resolve</c> by the admin, body
/// {commandId, outcome: "applied" | "notApplicable", comment}. A drop leaves the mandatory bad event as a manual effect.
/// </summary>
public sealed class ManualEffectApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    private static string PlayerUrl(Guid effectId, Guid? seasonId = null) =>
        $"/api/seasons/{seasonId ?? SiteFactory.SeasonId}/effects/{effectId}/resolve";

    private static string AdminUrl(Guid effectId, Guid? seasonId = null) =>
        $"/api/admin/seasons/{seasonId ?? SiteFactory.SeasonId}/effects/{effectId}/resolve";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- Success ----

    [Fact]
    public async Task Owner_applies_the_effect_and_it_leaves_the_list()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var effectId = await DropAsync(vasya);

        var response = await PostOkAsync(vasya, PlayerUrl(effectId), new { commandId = Guid.NewGuid(), outcome = "applied" });

        Assert.Equal(["manual-effect-resolved"], await TypesAsync(response));
        Assert.Equal(0, (await MeAsync(vasya)).GetProperty("manualEffects").GetArrayLength());
    }

    [Fact]
    public async Task Owner_marks_not_applicable_with_a_comment()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var effectId = await DropAsync(vasya);

        await PostOkAsync(vasya, PlayerUrl(effectId), new { commandId = Guid.NewGuid(), outcome = "notApplicable", comment = "Колода пуста" });

        await using var db = _site.NewDb();
        var logged = await db.Events.Where(e => e.Type == "manual-effect-resolved").SingleAsync(Ct);
        Assert.Contains("\"outcome\":\"notApplicable\"", logged.Data, StringComparison.Ordinal);
        Assert.Contains("Колода пуста", logged.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_resolves_a_players_effect()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        var effectId = await DropAsync(vasya);

        await PostOkAsync(admin, AdminUrl(effectId), new { commandId = Guid.NewGuid(), outcome = "applied", comment = "Разыграли на стриме" });

        Assert.Equal(0, (await MeAsync(vasya)).GetProperty("manualEffects").GetArrayLength());
    }

    [Fact]
    public async Task Same_command_id_twice_acts_once()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var effectId = await DropAsync(vasya);
        var commandId = Guid.NewGuid();

        await PostOkAsync(vasya, PlayerUrl(effectId), new { commandId, outcome = "applied" });
        await PostOkAsync(vasya, PlayerUrl(effectId), new { commandId, outcome = "applied" });

        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Events.CountAsync(e => e.Type == "manual-effect-resolved", Ct));
    }

    // ---- Rule refusals: 409 ----

    [Fact]
    public async Task Another_players_effect_is_a_conflict()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        var effectId = await DropAsync(vasya);

        var response = await petya.PostAsJsonAsync(PlayerUrl(effectId), new { commandId = Guid.NewGuid(), outcome = "applied" }, Ct);

        await AssertConflictAsync(response, "effect.notYours");
        Assert.Equal(1, (await MeAsync(vasya)).GetProperty("manualEffects").GetArrayLength());
    }

    [Fact]
    public async Task Not_applicable_without_a_comment_is_a_conflict()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var effectId = await DropAsync(vasya);

        var response = await vasya.PostAsJsonAsync(PlayerUrl(effectId), new { commandId = Guid.NewGuid(), outcome = "notApplicable", comment = " " }, Ct);

        await AssertConflictAsync(response, "player.commentRequired");
    }

    [Fact]
    public async Task Admin_without_a_comment_is_a_conflict()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        var effectId = await DropAsync(vasya);

        var response = await admin.PostAsJsonAsync(AdminUrl(effectId), new { commandId = Guid.NewGuid(), outcome = "applied" }, Ct);

        await AssertConflictAsync(response, "player.commentRequired");
    }

    [Fact]
    public async Task Unknown_effect_is_a_conflict()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(PlayerUrl(Guid.NewGuid()), new { commandId = Guid.NewGuid(), outcome = "applied" }, Ct);

        await AssertConflictAsync(response, "effect.notPending");
    }

    // ---- Refused by role: 403 / 401 ----

    [Fact]
    public async Task Spectator_cannot_resolve_as_a_player()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var spectator = await _site.SignedInAsync("zritel");
        var effectId = await DropAsync(vasya);

        var response = await spectator.PostAsJsonAsync(PlayerUrl(effectId), new { commandId = Guid.NewGuid(), outcome = "applied" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("petya")]
    [InlineData("zritel")]
    public async Task Players_and_spectators_cannot_use_the_admin_endpoint(string login)
    {
        var vasya = await _site.SignedInAsync("vasya");
        var effectId = await DropAsync(vasya);
        var client = await _site.SignedInAsync(login);

        var response = await client.PostAsJsonAsync(AdminUrl(effectId), new { commandId = Guid.NewGuid(), outcome = "applied", comment = "Моё" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, (await MeAsync(vasya)).GetProperty("manualEffects").GetArrayLength());
    }

    [Fact]
    public async Task Anonymous_is_unauthorized()
    {
        var client = await _site.AnonymousAsync();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync(PlayerUrl(Guid.NewGuid()), new { commandId = Guid.NewGuid(), outcome = "applied" }, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync(AdminUrl(Guid.NewGuid()), new { commandId = Guid.NewGuid(), outcome = "applied", comment = "x" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Post_without_the_antiforgery_token_is_refused()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var effectId = await DropAsync(vasya);
        vasya.DefaultRequestHeaders.Remove(Hosting.Csrf.HeaderName);

        var response = await vasya.PostAsJsonAsync(PlayerUrl(effectId), new { commandId = Guid.NewGuid(), outcome = "applied" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_season_is_not_found()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(
            AdminUrl(Guid.NewGuid(), Guid.NewGuid()), new { commandId = Guid.NewGuid(), outcome = "applied", comment = "x" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Invalid input: 400 ----

    [Fact]
    public async Task Unknown_outcome_is_a_bad_request()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var effectId = await DropAsync(vasya);

        var response = await vasya.PostAsJsonAsync(PlayerUrl(effectId), new { commandId = Guid.NewGuid(), outcome = "maybe" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_outcome_is_a_bad_request()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var effectId = await DropAsync(vasya);

        var response = await vasya.PostAsJsonAsync(PlayerUrl(effectId), new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Comment_over_500_characters_is_a_bad_request()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var effectId = await DropAsync(vasya);

        var response = await vasya.PostAsJsonAsync(
            PlayerUrl(effectId), new { commandId = Guid.NewGuid(), outcome = "notApplicable", comment = new string('я', 501) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_command_id_is_a_bad_request()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var effectId = await DropAsync(vasya);

        var response = await vasya.PostAsJsonAsync(PlayerUrl(effectId), new { outcome = "applied" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Helpers ----

    // The player rolls, starts and drops: the mandatory bad event waits as a manual effect
    private static async Task<Guid> DropAsync(HttpClient client)
    {
        await PostOkAsync(client, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(client, Url("start"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(client, Url("drop"), new { commandId = Guid.NewGuid() });
        var effect = Assert.Single((await MeAsync(client)).GetProperty("manualEffects").EnumerateArray());
        return effect.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> MeAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        return doc.RootElement.GetProperty("me").Clone();
    }

    private static async Task<List<string>> TypesAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. doc.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()!)];
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
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Finish bonuses are not recalculated retroactively (D-113): a change of the finish list warns the admin that the
/// bonuses already given stay (<c>warnings: ["finish.bonusesKept"]</c>); «Пересчитать бонусы по текущим правилам» —
/// <c>POST /api/admin/seasons/{id}/finish-bonuses/recalculate</c> — is the deliberate recalculation, in the log.
/// </summary>
public sealed class FinishBonusApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Recalculate => $"/api/admin/seasons/{SiteFactory.SeasonId}/finish-bonuses/recalculate";

    private static string Rules => $"/api/admin/seasons/{SiteFactory.SeasonId}/rules";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Changing_the_finish_list_with_finishers_warns_that_their_bonuses_stay()
    {
        await FinishAsync("vasya");
        await FinishAsync("petya");
        var admin = await _site.SignedInAsync("admin");

        var result = await ChangeFinishListAsync(admin, [20, 15]);

        Assert.Equal(["finish.bonusesKept"], result.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
        Assert.Equal(10, await BonusAsync("petya"));
    }

    [Fact]
    public async Task Changing_the_finish_list_without_finishers_warns_of_nothing()
    {
        var admin = await _site.SignedInAsync("admin");

        var result = await ChangeFinishListAsync(admin, [20, 15]);

        Assert.Empty(result.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task The_admin_recalculates_the_bonuses_by_the_current_list()
    {
        await FinishAsync("vasya");
        await FinishAsync("petya");
        var admin = await _site.SignedInAsync("admin");
        await ChangeFinishListAsync(admin, [20, 15]);

        var response = await admin.PostAsJsonAsync(Recalculate, new { commandId = Guid.NewGuid() }, Ct);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(20, await BonusAsync("petya"));
        await using var db = _site.NewDb();
        Assert.True(await db.Events.AnyAsync(e => e.SeasonId == SiteFactory.SeasonId && e.Type == "finish-bonus-rules-refreshed", Ct));

        // Every finisher now holds the current list: the next change of something else warns of nothing
        var next = await ChangeAsync(admin, r => r["roll"]!["freeRerollsPerRoll"] = 2);
        Assert.Empty(next.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task With_every_finisher_on_the_current_list_there_is_nothing_to_recalculate()
    {
        await FinishAsync("vasya");
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Recalculate, new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("finish.nothingToRecalculate", await CodeAsync(response));
    }

    [Fact]
    public async Task A_repeated_recalculation_request_is_one_recalculation()
    {
        await FinishAsync("vasya");
        await FinishAsync("petya");
        var admin = await _site.SignedInAsync("admin");
        await ChangeFinishListAsync(admin, [20]);
        var commandId = Guid.NewGuid();

        Assert.True((await admin.PostAsJsonAsync(Recalculate, new { commandId }, Ct)).IsSuccessStatusCode);
        Assert.True((await admin.PostAsJsonAsync(Recalculate, new { commandId }, Ct)).IsSuccessStatusCode);

        Assert.Equal(20, await BonusAsync("petya"));
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Players_and_spectators_do_not_recalculate(string login)
    {
        var client = await _site.SignedInAsync(login);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Recalculate, new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_does_not_recalculate()
    {
        var client = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Recalculate, new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_recalculation_without_the_antiforgery_token_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Recalculate, new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_recalculation_of_an_unknown_season_is_not_found()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync($"/api/admin/seasons/{Guid.NewGuid()}/finish-bonuses/recalculate", new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_recalculation_without_a_command_id_is_invalid()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Recalculate, new { commandId = Guid.Empty }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Helpers ----

    /// <summary>The admin puts the player one cell before the finish; the player rolls, starts and completes .</summary>
    private async Task FinishAsync(string login)
    {
        var client = await _site.SignedInAsync(login);
        await _site.SendAsync(new AdjustPlayer(_site.Players[login], "Перенос для теста", CellId: $"c{RulesetJson.Default().Map.LinearLength - 1}"));
        foreach (var action in new[] { "roll", "start" })
        {
            await PostOkAsync(client, $"/api/seasons/{SiteFactory.SeasonId}/{action}", new { commandId = Guid.NewGuid() });
        }

        await PostOkAsync(client, $"/api/seasons/{SiteFactory.SeasonId}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
    }

    private async Task<int> BonusAsync(string login)
    {
        await using var db = _site.NewDb();
        return (await db.SeasonPlayers.AsNoTracking().SingleAsync(p => p.Id == _site.Players[login], Ct)).FinishBonus;
    }

    private static Task<JsonElement> ChangeFinishListAsync(HttpClient admin, int[] list) =>
        ChangeAsync(admin, r => r["finish"]!["bonusByOrder"] = new JsonArray([.. list.Select(b => (JsonNode)b)]));

    private static async Task<JsonElement> ChangeAsync(HttpClient admin, Action<JsonNode> change)
    {
        using var current = JsonDocument.Parse(await admin.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}/rules", Ct));
        var ruleset = JsonNode.Parse(current.RootElement.GetProperty("ruleset").GetRawText())!;
        change(ruleset);
        var response = await admin.PutAsJsonAsync(
            Rules, new { commandId = Guid.NewGuid(), expectedVersion = current.RootElement.GetProperty("version").GetInt32(), ruleset }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task PostOkAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }
}

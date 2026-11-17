using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// D-878, SPEC «новые механики выключенными флагами»: navigation describes the requested season's current rules
/// to every signed-in reader, including when polls are off or the reader has no player there. The home view carries
/// the same actual flag, while current-season discovery stays id-only. Written against JSON, before the new route.
/// </summary>
public sealed class SeasonNavigationApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Theory]
    [InlineData("admin")]
    [InlineData("vasya")]
    [InlineData("petya")]
    [InlineData("masha")]
    [InlineData("zritel")]
    public async Task Every_signed_in_role_reads_the_enabled_flag_without_needing_an_own_player(string login)
    {
        var admin = await _site.SignedInAsync("admin");
        await TogglePollsAsync(admin, SiteFactory.SeasonId, true);
        var client = await _site.SignedInAsync(login);

        await AssertViewsAsync(client, SiteFactory.SeasonId, true);

        if (login is "admin" or "masha" or "zritel")
        {
            var season = await ApiCalls.OkAsync(await client.GetAsync(Season(SiteFactory.SeasonId), Ct));
            Assert.Equal(JsonValueKind.Null, season.GetProperty("me").ValueKind);
        }
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task Disabled_polls_are_reported_as_false_without_a_feature_guard_or_a_default_enabled_value()
    {
        var player = await _site.SignedInAsync("vasya");

        await AssertViewsAsync(player, SiteFactory.SeasonId, false);

        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task Current_rule_changes_and_their_undo_change_both_navigation_and_home_flags()
    {
        var admin = await _site.SignedInAsync("admin");
        var player = await _site.SignedInAsync("vasya");
        var enable = await TogglePollsAsync(admin, SiteFactory.SeasonId, true);
        await AssertViewsAsync(player, SiteFactory.SeasonId, true);

        await ApiCalls.PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/undo", new
        {
            commandId = Guid.NewGuid(),
            targetCommandId = enable,
            comment = "Отмена включения голосований",
        });
        await AssertViewsAsync(player, SiteFactory.SeasonId, false);

        await TogglePollsAsync(admin, SiteFactory.SeasonId, true);
        await AssertViewsAsync(player, SiteFactory.SeasonId, true);
        await TogglePollsAsync(admin, SiteFactory.SeasonId, false);
        await AssertViewsAsync(player, SiteFactory.SeasonId, false);
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task Explicit_season_navigation_does_not_use_the_readers_current_season_or_leak_flags_between_seasons()
    {
        var admin = await _site.SignedInAsync("admin");
        await TogglePollsAsync(admin, SiteFactory.SeasonId, true);
        var other = await _site.CreateSeasonAsync();
        await ApiCalls.PostOkAsync(admin, $"/api/admin/seasons/{other}/status", new { commandId = Guid.NewGuid(), to = "active" });
        await ApiCalls.PostOkAsync(admin, $"/api/admin/seasons/{other}/players", new { commandId = Guid.NewGuid(), userId = _site.Users["masha"] });
        var masha = await _site.SignedInAsync("masha");

        var current = await ApiCalls.OkAsync(await masha.GetAsync("/api/seasons/current", Ct));
        Assert.Equal(other, current.GetProperty("id").GetGuid());
        Assert.Equal(new[] { "id" }, current.EnumerateObject().Select(property => property.Name));
        await AssertViewsAsync(masha, SiteFactory.SeasonId, true);
        await AssertViewsAsync(masha, other, false);
        await AssertViewsAsync(masha, SiteFactory.SeasonId, true);
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task Reading_navigation_and_home_does_not_append_commands_events_or_change_the_season_state()
    {
        var admin = await _site.SignedInAsync("admin");
        var player = await _site.SignedInAsync("vasya");
        await TogglePollsAsync(admin, SiteFactory.SeasonId, true);
        var before = JsonSerializer.Serialize(await StateAsync(_site));
        var commandsBefore = await admin.GetStringAsync($"/api/admin/seasons/{SiteFactory.SeasonId}/commands", Ct);

        for (var read = 0; read < 3; read++)
        {
            await AssertViewsAsync(player, SiteFactory.SeasonId, true);
        }

        Assert.Equal(before, JsonSerializer.Serialize(await StateAsync(_site)));
        Assert.Equal(commandsBefore, await admin.GetStringAsync($"/api/admin/seasons/{SiteFactory.SeasonId}/commands", Ct));
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task An_unsigned_reader_cannot_read_navigation_even_when_polls_are_disabled()
    {
        var anonymous = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Navigation(SiteFactory.SeasonId), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff")]
    [InlineData("not-a-guid")]
    public async Task An_unknown_or_invalid_season_is_not_found(string seasonId)
    {
        var reader = await _site.SignedInAsync("zritel");

        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/seasons/{seasonId}/navigation", Ct)).StatusCode);
    }

    private static string Season(Guid seasonId) => $"/api/seasons/{seasonId}";

    private static string Navigation(Guid seasonId) => $"{Season(seasonId)}/navigation";

    private static async Task AssertViewsAsync(HttpClient client, Guid seasonId, bool pollsEnabled)
    {
        var navigation = await ApiCalls.OkAsync(await client.GetAsync(Navigation(seasonId), Ct));
        Assert.Equal(new[] { "id", "pollsEnabled" }, navigation.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(seasonId, navigation.GetProperty("id").GetGuid());
        Assert.Equal(pollsEnabled, navigation.GetProperty("pollsEnabled").GetBoolean());

        var season = await ApiCalls.OkAsync(await client.GetAsync(Season(seasonId), Ct));
        Assert.Equal(seasonId, season.GetProperty("id").GetGuid());
        Assert.Equal(pollsEnabled, season.GetProperty("pollsEnabled").GetBoolean());
    }

    private static async Task<Guid> TogglePollsAsync(HttpClient admin, Guid seasonId, bool enabled)
    {
        var current = await ApiCalls.OkAsync(await admin.GetAsync($"{Season(seasonId)}/rules", Ct));
        var ruleset = JsonNode.Parse(current.GetProperty("ruleset").GetRawText())!.AsObject();
        ruleset["features"]!["polls"] = enabled;
        if (enabled)
        {
            // Poll effects use items: a valid ruleset is required, not a test-only invalid flag combination.
            ruleset["features"]!["items"] = true;
        }
        var commandId = Guid.NewGuid();
        var response = await admin.PutAsJsonAsync($"/api/admin/seasons/{seasonId}/rules", new
        {
            commandId,
            expectedVersion = current.GetProperty("version").GetInt32(),
            ruleset,
        }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return commandId;
    }
}

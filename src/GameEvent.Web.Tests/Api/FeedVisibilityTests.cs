using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Web.Seasons;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Who sees what in the feed (D-124 after the security review): every event type has an entry; a proof's links, note,
/// files and the admin's comment are the player's and the admin's; others see only which command an undo undid and why.
/// </summary>
public sealed class FeedVisibilityTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    private static string Feed => $"/api/seasons/{SiteFactory.SeasonId}/feed";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public void Every_event_type_says_who_sees_it_in_the_feed()
    {
        // A new event type fails here until someone decides what of it the feed shows
        var names = EventCatalog.Types.Select(t => EventCatalog.Describe(t).Name).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(names, FeedVisibility.Types.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_proofs_details_are_the_players_and_the_admins()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var run = await CompletedAsync(vasya);
        await PostOkAsync(vasya, Url($"runs/{run}/proof"), new { commandId = Guid.NewGuid(), links = new[] { "https://imgur.com/a/private" }, note = "Мой адрес в титрах" });
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/runs/{run}/reject", new { commandId = Guid.NewGuid(), comment = "Нет титров на скрине" });

        var others = await EntriesAsync(await _site.SignedInAsync("petya"));
        var spectator = await EntriesAsync(await _site.SignedInAsync("zritel"));
        var own = await EntriesAsync(vasya);
        var admins = await EntriesAsync(admin);

        foreach (var entries in new[] { others, spectator })
        {
            var submitted = Of(entries, "proof-submitted");
            Assert.False(submitted.TryGetProperty("links", out _));
            Assert.False(submitted.TryGetProperty("note", out _));
            Assert.False(submitted.TryGetProperty("files", out _));
            Assert.Equal(run, submitted.GetProperty("runId").GetGuid());
            Assert.False(Of(entries, "proof-rejected").TryGetProperty("comment", out _));
        }

        foreach (var entries in new[] { own, admins })
        {
            Assert.Equal("https://imgur.com/a/private", Of(entries, "proof-submitted").GetProperty("links")[0].GetString());
            Assert.Equal("Мой адрес в титрах", Of(entries, "proof-submitted").GetProperty("note").GetString());
            Assert.Equal("Нет титров на скрине", Of(entries, "proof-rejected").GetProperty("comment").GetString());
        }
    }

    [Fact]
    public async Task Others_see_which_command_an_undo_undid_not_the_state_it_put_back()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var roll = Guid.NewGuid();
        await PostOkAsync(vasya, Url("roll"), new { commandId = roll });
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/undo", new { commandId = Guid.NewGuid(), targetCommandId = roll, comment = "Сбой колеса" });

        var others = Of(await EntriesAsync(await _site.SignedInAsync("petya")), "command-undone");
        var admins = Of(await EntriesAsync(admin), "command-undone");

        Assert.Equal(["commandId", "comment"], others.EnumerateObject().Select(p => p.Name));
        Assert.Equal((roll, "Сбой колеса"), (others.GetProperty("commandId").GetGuid(), others.GetProperty("comment").GetString()));
        Assert.True(admins.TryGetProperty("players", out _));
    }

    [Fact]
    public async Task The_global_log_is_not_a_seasons_feed()
    {
        var admin = await _site.SignedInAsync("admin");

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/seasons/{Guid.Empty}/feed", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_profile_has_no_login_or_role_and_hides_reviews_of_deleted_games_from_others()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var run = await CompletedAsync(vasya);
        await PostOkAsync(vasya, Url($"runs/{run}/review"), new { commandId = Guid.NewGuid(), rating = 7 });
        var gameId = (await JsonAsync(await vasya.GetAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct))).GetProperty("me").GetProperty("lastCompleted").GetProperty("game").GetProperty("id").GetGuid();
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(admin, $"/api/admin/pool/{gameId}/delete", new { commandId = Guid.NewGuid(), reason = "Дубль" });

        var seen = await JsonAsync(await (await _site.SignedInAsync("petya")).GetAsync($"/api/users/{_site.Users["vasya"]}", Ct));
        var adminSees = await JsonAsync(await admin.GetAsync($"/api/users/{_site.Users["vasya"]}", Ct));

        Assert.False(seen.TryGetProperty("login", out _));
        Assert.False(seen.TryGetProperty("role", out _));
        Assert.Empty(seen.GetProperty("reviews").EnumerateArray());
        Assert.Single(adminSees.GetProperty("reviews").EnumerateArray());
    }

    // ---- Helpers ----

    private static JsonElement Of(List<JsonElement> entries, string type) =>
        entries.First(e => e.GetProperty("type").GetString() == type).GetProperty("data");

    private static async Task<List<JsonElement>> EntriesAsync(HttpClient client) =>
        [.. (await JsonAsync(await client.GetAsync(Feed, Ct))).GetProperty("entries").EnumerateArray()];

    private static async Task<Guid> CompletedAsync(HttpClient player)
    {
        await PostOkAsync(player, Url("roll"));
        await PostOkAsync(player, Url("start"));
        await PostOkAsync(player, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        return (await JsonAsync(await player.GetAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct))).GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetGuid();
    }

    private static async Task PostOkAsync(HttpClient client, string url, object? body = null)
    {
        var response = await client.PostAsJsonAsync(url, body ?? new { commandId = Guid.NewGuid() }, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}

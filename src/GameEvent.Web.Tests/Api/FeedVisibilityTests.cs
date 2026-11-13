using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Infrastructure.EventLog;
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
    public void An_anonymous_vote_names_nobody_and_its_change_is_not_shown_even_to_the_admin()
    {
        // D-541, D-726: «голос подан» without the voter or the option; the entry drops the command's author too
        var (poll, user) = (Guid.NewGuid(), Guid.NewGuid());
        var cast = EventCodec.Encode(new Engine.Polls.VoteCast(poll, user, "Horror", Anonymous: true, ShowLiveCounts: false));
        var changed = EventCodec.Encode(new Engine.Polls.VoteChanged(poll, user, "RPG", Anonymous: true, ShowLiveCounts: false));
        JsonObject Data(StoredEvent e) => JsonNode.Parse(e.Data)!.AsObject();

        foreach (var admin in new[] { true, false })
        {
            var shown = FeedVisibility.Shown(cast.Type, Data(cast), admin, viewerPlayerId: null)!;
            Assert.Equal(["anonymous", "pollId"], shown.Select(x => x.Key).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(user.ToString(), shown.ToJsonString(), StringComparison.Ordinal);
            Assert.Null(FeedVisibility.Shown(changed.Type, Data(changed), admin, viewerPlayerId: null));
        }

        Assert.True(FeedVisibility.HidesAuthor(cast.Type, Data(cast)));
        Assert.True(FeedVisibility.HidesAuthor(changed.Type, Data(changed)));
    }

    [Fact]
    public void A_named_vote_shows_its_voter_and_its_option_only_with_live_counts()
    {
        // D-515, D-726: a named poll names the voter; without live counts the option waits for the result
        var (poll, user) = (Guid.NewGuid(), Guid.NewGuid());
        var hidden = EventCodec.Encode(new Engine.Polls.VoteCast(poll, user, "Horror", Anonymous: false, ShowLiveCounts: false));
        var live = EventCodec.Encode(new Engine.Polls.VoteChanged(poll, user, "RPG", Anonymous: false, ShowLiveCounts: true));
        JsonObject Data(StoredEvent e) => JsonNode.Parse(e.Data)!.AsObject();

        var withoutCounts = FeedVisibility.Shown(hidden.Type, Data(hidden), admin: false, viewerPlayerId: null)!;
        Assert.False(withoutCounts.ContainsKey("optionId"));
        Assert.Equal(user, withoutCounts["userId"]!.GetValue<Guid>());
        Assert.Equal("RPG", FeedVisibility.Shown(live.Type, Data(live), admin: false, viewerPlayerId: null)!["optionId"]!.GetValue<string>());
        Assert.False(FeedVisibility.HidesAuthor(hidden.Type, Data(hidden)));
    }

    [Fact]
    public void An_undo_shows_the_admin_no_votes_of_an_anonymous_poll_or_of_one_without_live_counts()
    {
        // D-726: what an undo puts back does not name voters the poll hides
        JsonObject Poll(bool anonymous, bool live) => new()
        {
            ["pollId"] = Guid.NewGuid(),
            ["anonymous"] = anonymous,
            ["showLiveCounts"] = live,
            ["votes"] = new JsonArray(new JsonObject { ["userId"] = Guid.NewGuid(), ["optionId"] = "Horror" }),
        };
        var data = new JsonObject { ["commandId"] = Guid.NewGuid(), ["comment"] = "Ошибка", ["polls"] = new JsonArray(Poll(true, false), Poll(false, false), Poll(false, true)) };

        var polls = FeedVisibility.Shown("command-undone", data, admin: true, viewerPlayerId: null)!["polls"]!.AsArray();

        Assert.Equal([false, false, true], polls.Select(p => p!.AsObject().ContainsKey("votes")));
    }

    [Fact]
    public void An_irl_proofs_files_link_and_comment_are_the_players_and_the_admins()
    {
        // D-684: others see that an IRL proof was sent and checked, not its screenshots, link or the admin's comment
        var (effect, owner, file) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var at = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        var submitted = EventCodec.Encode(new Engine.Proofs.IrlProofSubmitted(effect, owner, [file], "https://video.example/abc", at));
        var reviewed = EventCodec.Encode(new Engine.Proofs.IrlProofReviewed(effect, owner, false, "Видео закрыто", at));
        JsonObject Data(StoredEvent e) => JsonNode.Parse(e.Data)!.AsObject();

        foreach (var viewer in new Guid?[] { Guid.NewGuid(), null })
        {
            var other = FeedVisibility.Shown(submitted.Type, Data(submitted), admin: false, viewer)!;
            Assert.False(other.ContainsKey("files") || other.ContainsKey("link"));
            Assert.Equal(effect, other["effectId"]!.GetValue<Guid>());
            Assert.False(FeedVisibility.Shown(reviewed.Type, Data(reviewed), admin: false, viewer)!.ContainsKey("comment"));
        }

        Assert.Equal("https://video.example/abc", FeedVisibility.Shown(submitted.Type, Data(submitted), admin: false, owner)!["link"]!.GetValue<string>());
        Assert.Equal("Видео закрыто", FeedVisibility.Shown(reviewed.Type, Data(reviewed), admin: true, null)!["comment"]!.GetValue<string>());
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
    public async Task Others_see_a_content_publications_version_comment_and_size_not_the_pack()
    {
        // NB-SEC-3 (D-653): the pack holds every effect and unpublished price; only the admin reads it in the feed
        await using (var db = _site.NewDb())
        {
            var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
            await _site.SendAsync(new ChangeRuleset(state.Rules with { Features = state.Rules.Features with { Items = true } }));
        }

        var item = new ObjectDefinition
        {
            Id = "secret-orange",
            Kind = ObjectKind.Item,
            Name = "Тайный апельсин",
            Description = "Предмет для теста.",
            Rarity = Rarity.Common,
            Price = 10,
            Window = UseWindow.Anytime,
            Stackable = true,
            Effect = new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.Self }, Actions = [new MoveAction { Steps = ContentValue.Of(1) }] },
        };
        await _site.SendAsync(new PublishContent(new ContentPack { Objects = [item] }, "Новый контент"));

        var admin = Of(await EntriesAsync(await _site.SignedInAsync("admin")), "content-published");
        foreach (var viewer in new[] { "petya", "zritel" })
        {
            var seen = Of(await EntriesAsync(await _site.SignedInAsync(viewer)), "content-published");
            Assert.Equal(["version", "comment", "objectCount"], seen.EnumerateObject().Select(p => p.Name));
            Assert.Equal((1, "Новый контент", 1), (seen.GetProperty("version").GetInt32(), seen.GetProperty("comment").GetString(), seen.GetProperty("objectCount").GetInt32()));
        }

        Assert.Equal("secret-orange", admin.GetProperty("pack").GetProperty("objects")[0].GetProperty("id").GetString());
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

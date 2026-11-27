using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Reactions;
using GameEvent.Engine.Rulesets;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Api.ApiCalls;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

public sealed partial class GalleryMomentApiTests
{
    private const string Fire = "🔥";
    private const string Laugh = "😂";

    [Fact]
    public async Task Gallery_reactions_count_each_emoji_per_account_and_retries_do_not_restore_a_removed_choice()
    {
        await EnableGalleryReactionsAsync();
        var player = await _site.SignedInAsync("vasya");
        var playerTab = await _site.SignedInAsync("vasya");
        var spectator = await _site.SignedInAsync("zritel");
        var otherPlayer = await _site.SignedInAsync("petya");
        var itemId = Guid.NewGuid();
        var anotherItem = Guid.NewGuid();
        await PublishGalleryMomentAsync(player, itemId);
        await PublishGalleryMomentAsync(player, anotherItem);

        var selection = Guid.NewGuid();
        await Task.WhenAll(
            SetGalleryReactionAsync(player, itemId, Fire, selected: true, selection),
            SetGalleryReactionAsync(playerTab, itemId, Fire, selected: true, selection));
        await SetGalleryReactionAsync(player, itemId, Laugh, selected: true);
        await SetGalleryReactionAsync(spectator, itemId, Fire, selected: true);
        Assert.Equal([(Fire, 2, true), (Laugh, 1, true)], await ReadGalleryReactionsAsync(player, itemId));
        Assert.Equal([(Fire, 2, true), (Laugh, 1, false)], await ReadGalleryReactionsAsync(spectator, itemId));
        Assert.Equal([(Fire, 2, false), (Laugh, 1, false)], await ReadGalleryReactionsAsync(otherPlayer, itemId));
        Assert.Equal([(Fire, 0, false), (Laugh, 0, false)], await ReadGalleryReactionsAsync(player, anotherItem));

        await SetGalleryReactionAsync(playerTab, itemId, Fire, selected: false);
        await SetGalleryReactionAsync(player, itemId, Fire, selected: true, selection);
        Assert.Equal([(Fire, 1, false), (Laugh, 1, true)], await ReadGalleryReactionsAsync(player, itemId));
        Assert.Equal([(Fire, 1, true), (Laugh, 1, false)], await ReadGalleryReactionsAsync(spectator, itemId));

        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Events.CountAsync(e => e.CommandId == selection && e.Type == GalleryReactionSet.Type, Ct));
    }

    [Fact]
    public async Task Gallery_reactions_with_no_session_no_csrf_or_invalid_input_do_not_write()
    {
        await EnableGalleryReactionsAsync();
        var player = await _site.SignedInAsync("vasya");
        var spectator = await _site.SignedInAsync("zritel");
        var anonymous = await _site.AnonymousAsync();
        var itemId = Guid.NewGuid();
        await PublishGalleryMomentAsync(player, itemId);

        var id = Guid.NewGuid();
        var route = GalleryReactionRoute(SeasonId, itemId);
        using var denied = await WriteGalleryReactionAsync(anonymous, route, id, Fire, selected: true);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        spectator.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var noCsrf = await WriteGalleryReactionAsync(spectator, route, id, Fire, selected: true);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);

        await SiteFactory.RefreshCsrfAsync(spectator);
        using var missingSelection = await spectator.PutAsJsonAsync(route, new { commandId = id, emoji = Fire }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, missingSelection.StatusCode);
        using var emptyCommandId = await WriteGalleryReactionAsync(spectator, route, Guid.Empty, Fire, selected: true);
        Assert.Equal(HttpStatusCode.BadRequest, emptyCommandId.StatusCode);
        using var outsideConfig = await WriteGalleryReactionAsync(spectator, route, id, Fire + " ", selected: true);
        Assert.Equal(HttpStatusCode.Conflict, outsideConfig.StatusCode);
        Assert.Equal([(Fire, 0, false), (Laugh, 0, false)], await ReadGalleryReactionsAsync(spectator, itemId));

        await using var db = _site.NewDb();
        Assert.False(await db.Events.AnyAsync(e => e.CommandId == id, Ct));
    }

    [Fact]
    public async Task Gallery_reactions_are_scoped_to_visible_moments_and_return_after_undoing_moderation()
    {
        await EnableGalleryReactionsAsync();
        var player = await _site.SignedInAsync("vasya");
        var spectator = await _site.SignedInAsync("zritel");
        var admin = await _site.SignedInAsync("admin");
        var itemId = Guid.NewGuid();
        await PublishGalleryMomentAsync(player, itemId);
        await SetGalleryReactionAsync(spectator, itemId, Fire, selected: true);

        var anotherSeason = await CreateOtherGallerySeasonAsync();
        var bus = _site.Services.GetRequiredService<CommandBus>();
        var changed = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), anotherSeason,
            new ChangeRuleset(ReactionRules(GalleryRules(RulesetJson.Default()))), AuthorId: null), Ct);
        Assert.True(changed.IsAccepted, changed.Rejection?.Detail);
        using var outside = await WriteGalleryReactionAsync(spectator, GalleryReactionRoute(anotherSeason, itemId),
            Guid.NewGuid(), Fire, selected: true);
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);

        var hideId = Guid.NewGuid();
        await PostOkAsync(admin, $"/api/admin/seasons/{SeasonId}/gallery/{itemId}/hide", new { commandId = hideId });
        Assert.Empty((await ReadGalleryPageAsync(spectator)).GetProperty("items").EnumerateArray());
        var hiddenEdit = Guid.NewGuid();
        using var hidden = await WriteGalleryReactionAsync(spectator, GalleryReactionRoute(SeasonId, itemId),
            hiddenEdit, Fire, selected: false);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        await PostOkAsync(admin, $"/api/admin/seasons/{SeasonId}/undo", new
        {
            commandId = Guid.NewGuid(),
            targetCommandId = hideId,
            comment = "Вернуть момент",
        });
        Assert.Equal([(Fire, 1, true), (Laugh, 0, false)], await ReadGalleryReactionsAsync(spectator, itemId));

        await using var db = _site.NewDb();
        Assert.False(await db.Events.AnyAsync(e => e.CommandId == hiddenEdit, Ct));
    }

    [Fact]
    public async Task A_gallery_lists_moments_but_does_not_offer_reactions_when_the_reactions_flag_is_off()
    {
        await EnableGalleryAsync();
        var player = await _site.SignedInAsync("vasya");
        var spectator = await _site.SignedInAsync("zritel");
        var itemId = Guid.NewGuid();
        await PublishGalleryMomentAsync(player, itemId);

        var moment = Assert.Single((await ReadGalleryPageAsync(spectator)).GetProperty("items").EnumerateArray());
        Assert.Equal(itemId, moment.GetProperty("id").GetGuid());
        Assert.False(moment.TryGetProperty("reactions", out _));
        using var off = await WriteGalleryReactionAsync(spectator, GalleryReactionRoute(SeasonId, itemId),
            Guid.NewGuid(), Fire, selected: true);
        await AssertProblemAsync(off, HttpStatusCode.Conflict, "feature.disabled");
    }

    [Fact]
    public async Task A_gallery_reaction_does_not_move_the_latest_feed_row_or_cursor()
    {
        await EnableGalleryReactionsAsync();
        var player = await _site.SignedInAsync("vasya");
        var spectator = await _site.SignedInAsync("zritel");
        var seasonRoute = $"/api/seasons/{SeasonId}";
        await PostOkAsync(player, $"{seasonRoute}/roll");
        await PostOkAsync(player, $"{seasonRoute}/start");
        await PostOkAsync(player, $"{seasonRoute}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });
        var itemId = Guid.NewGuid();
        await PublishGalleryMomentAsync(player, itemId);

        var feedRoute = $"{seasonRoute}/feed?limit=1";
        using var initial = await spectator.GetAsync(feedRoute, Ct);
        var before = await OkAsync(initial);
        var previousRow = Assert.Single(before.GetProperty("entries").EnumerateArray());
        var cursor = before.GetProperty("nextBefore").GetInt64();

        var reactionId = Guid.NewGuid();
        await SetGalleryReactionAsync(spectator, itemId, Fire, selected: true, reactionId);
        Assert.Equal([(Fire, 1, true), (Laugh, 0, false)], await ReadGalleryReactionsAsync(spectator, itemId));

        using var changed = await spectator.GetAsync(feedRoute, Ct);
        var after = await OkAsync(changed);
        var currentRow = Assert.Single(after.GetProperty("entries").EnumerateArray());
        Assert.Equal(previousRow.GetProperty("commandId").GetGuid(), currentRow.GetProperty("commandId").GetGuid());
        Assert.Equal(previousRow.GetProperty("sequence").GetInt64(), currentRow.GetProperty("sequence").GetInt64());
        Assert.Equal(cursor, after.GetProperty("nextBefore").GetInt64());
    }

    [Fact]
    public async Task The_queue_rejects_a_gallery_reaction_for_an_account_other_than_its_author()
    {
        await EnableGalleryReactionsAsync();
        var player = await _site.SignedInAsync("vasya");
        var spectator = await _site.SignedInAsync("zritel");
        var itemId = Guid.NewGuid();
        await PublishGalleryMomentAsync(player, itemId);

        var id = Guid.NewGuid();
        var bus = _site.Services.GetRequiredService<CommandBus>();
        var wrongAuthor = await bus.SendAsync(new CommandEnvelope(id, SeasonId,
            new SetGalleryReaction(itemId, _site.Users["vasya"], Fire, Selected: true), _site.Users["zritel"]), Ct);

        Assert.False(wrongAuthor.IsAccepted);
        Assert.Equal("galleryReaction.invalidUser", wrongAuthor.Rejection?.Code);
        Assert.Empty(wrongAuthor.Events);
        Assert.Equal([(Fire, 0, false), (Laugh, 0, false)], await ReadGalleryReactionsAsync(player, itemId));
        Assert.Equal([(Fire, 0, false), (Laugh, 0, false)], await ReadGalleryReactionsAsync(spectator, itemId));
        await using var db = _site.NewDb();
        Assert.False(await db.Events.AnyAsync(e => e.CommandId == id, Ct));
    }

    [Fact]
    public async Task A_spectator_can_add_and_remove_a_reaction_from_a_visible_archived_moment()
    {
        await EnableGalleryReactionsAsync();
        var player = await _site.SignedInAsync("vasya");
        var spectator = await _site.SignedInAsync("zritel");
        var admin = await _site.SignedInAsync("admin");
        var itemId = Guid.NewGuid();
        await PublishGalleryMomentAsync(player, itemId);
        foreach (var status in new[] { "closing", "finished", "archived" })
        {
            await PostOkAsync(admin, $"/api/admin/seasons/{SeasonId}/status", new { commandId = Guid.NewGuid(), to = status });
        }

        Assert.Equal([(Fire, 0, false), (Laugh, 0, false)], await ReadGalleryReactionsAsync(spectator, itemId));
        await SetGalleryReactionAsync(spectator, itemId, Laugh, selected: true);
        Assert.Equal([(Fire, 0, false), (Laugh, 1, true)], await ReadGalleryReactionsAsync(spectator, itemId));
        Assert.Equal([(Fire, 0, false), (Laugh, 1, false)], await ReadGalleryReactionsAsync(player, itemId));
        await SetGalleryReactionAsync(spectator, itemId, Laugh, selected: false);
        Assert.Equal([(Fire, 0, false), (Laugh, 0, false)], await ReadGalleryReactionsAsync(spectator, itemId));
    }

    private Task EnableGalleryReactionsAsync() => RulesAsync(_site, rules => ReactionRules(GalleryRules(rules)));

    private static Ruleset ReactionRules(Ruleset rules) => rules with
    {
        Features = rules.Features with { Reactions = true },
        Social = rules.Social with { Reactions = [Fire, Laugh] },
    };

    private static string GalleryReactionRoute(Guid seasonId, Guid itemId) => $"{Gallery(seasonId)}/{itemId}/reactions";

    private static Task<HttpResponseMessage> WriteGalleryReactionAsync(HttpClient client, string route, Guid commandId,
        string emoji, bool selected) => client.PutAsJsonAsync(route, new { commandId, emoji, selected }, Ct);

    private static async Task SetGalleryReactionAsync(HttpClient client, Guid itemId, string emoji, bool selected,
        Guid? commandId = null)
    {
        var route = GalleryReactionRoute(SeasonId, itemId);
        using var response = await WriteGalleryReactionAsync(client, route, commandId ?? Guid.NewGuid(), emoji, selected);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{route}: {response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task PublishGalleryMomentAsync(HttpClient player, Guid itemId)
    {
        using var response = await UploadAsync(player, SeasonId, itemId);
        await ReadItemAsync(response, HttpStatusCode.Created);
    }

    private static async Task<JsonElement> ReadGalleryPageAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Gallery(SeasonId), Ct);
        return await OkAsync(response);
    }

    private static async Task<(string Emoji, int Count, bool Selected)[]> ReadGalleryReactionsAsync(HttpClient client, Guid itemId)
    {
        var page = await ReadGalleryPageAsync(client);
        var item = Assert.Single(page.GetProperty("items").EnumerateArray(), v => v.GetProperty("id").GetGuid() == itemId);
        return [.. item.GetProperty("reactions").EnumerateArray().Select(v => (
            v.GetProperty("emoji").GetString()!, v.GetProperty("count").GetInt32(), v.GetProperty("selected").GetBoolean()))];
    }
}

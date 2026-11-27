using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Scoring;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Api.ApiCalls;
using static GameEvent.Web.Tests.Api.PollSeason;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// A reaction belongs to the public feed row for a command in one season, with the signed-in account as its actor
/// (SPEC «Лента и общение», D-124, D-541).
/// </summary>
public sealed class FeedReactionApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_feed_row_counts_each_emoji_once_per_signed_in_account()
    {
        await EnableReactionsAsync();
        var (player, target) = await RollAsync();
        var otherPlayer = await _site.SignedInAsync("petya");
        var spectator = await _site.SignedInAsync("zritel");

        Assert.Equal([("❤️", 0, false), ("🔥", 0, false)], Reactions(await ReadFeedAsync(spectator), target));

        await SetAsync(otherPlayer, target, "❤️", true);
        await SetAsync(otherPlayer, target, "🔥", true);
        await SetAsync(spectator, target, "🔥", true);

        Assert.Equal([("❤️", 1, true), ("🔥", 2, true)], Reactions(await ReadFeedAsync(otherPlayer), target));
        Assert.Equal([("❤️", 1, false), ("🔥", 2, true)], Reactions(await ReadFeedAsync(spectator), target));
        Assert.Equal([("❤️", 1, false), ("🔥", 2, false)], Reactions(await ReadFeedAsync(player), target));

        await SetAsync(spectator, target, "🔥", false);

        Assert.Equal([("❤️", 1, false), ("🔥", 1, false)], Reactions(await ReadFeedAsync(spectator), target));
        Assert.Equal([("❤️", 1, true), ("🔥", 1, true)], Reactions(await ReadFeedAsync(otherPlayer), target));
    }

    [Fact]
    public async Task Different_pages_share_the_same_row_and_delayed_retries_do_not_restore_a_removed_reaction()
    {
        await EnableReactionsAsync();
        var (target, _) = await CompleteAsync();
        var firstTab = await _site.SignedInAsync("petya");
        var secondTab = await _site.SignedInAsync("petya");
        var initial = await ReadFeedAsync(firstTab, "?limit=1");
        var cursor = initial.GetProperty("nextBefore").GetInt64();
        var initialEvent = Assert.Single(initial.GetProperty("entries").EnumerateArray());
        Assert.Equal(target, initialEvent.GetProperty("commandId").GetGuid());

        await Task.WhenAll(SetAsync(firstTab, target, "🔥", true), SetAsync(secondTab, target, "🔥", true));
        var repeatId = Guid.NewGuid();
        await SetAsync(firstTab, target, "🔥", true, repeatId);

        var firstPage = await ReadFeedAsync(firstTab, "?limit=1");
        var secondPage = await ReadFeedAsync(secondTab, $"?limit=1&before={cursor}");
        var newest = Assert.Single(firstPage.GetProperty("entries").EnumerateArray());
        var older = Assert.Single(secondPage.GetProperty("entries").EnumerateArray());
        Assert.Equal(target, newest.GetProperty("commandId").GetGuid());
        Assert.Equal(target, older.GetProperty("commandId").GetGuid());
        Assert.NotEqual(newest.GetProperty("sequence").GetInt64(), older.GetProperty("sequence").GetInt64());
        Assert.Equal(cursor, firstPage.GetProperty("nextBefore").GetInt64());
        Assert.Equal([("❤️", 0, false), ("🔥", 1, true)], Reactions(firstPage, target));
        Assert.Equal(Reactions(firstPage, target), Reactions(secondPage, target));

        await SetAsync(secondTab, target, "🔥", false);
        await SetAsync(firstTab, target, "🔥", true, repeatId);

        Assert.Equal([("❤️", 0, false), ("🔥", 0, false)], Reactions(await ReadFeedAsync(firstTab), target));
    }

    [Fact]
    public async Task A_reaction_does_not_block_undoing_the_source_and_the_undone_row_has_no_reactions()
    {
        await EnableReactionsAsync();
        var (_, target) = await RollAsync();
        _site.Clock.Advance(TimeSpan.FromMinutes(2));
        var spectator = await _site.SignedInAsync("zritel");
        var admin = await _site.SignedInAsync("admin");
        await SetAsync(spectator, target, "🔥", true);
        Assert.Equal(("🔥", 1, true), Reactions(await ReadFeedAsync(spectator), target)[1]);

        await PostOkAsync(admin, AdminUrl("undo"), new { commandId = Guid.NewGuid(), targetCommandId = target, comment = "Ошибся кнопкой" });

        var feed = await ReadFeedAsync(spectator);
        var roll = Assert.Single(feed.GetProperty("entries").EnumerateArray(), entry =>
            entry.GetProperty("commandId").GetGuid() == target && entry.GetProperty("type").GetString() == "game-rolled");
        Assert.True(roll.GetProperty("undone").GetBoolean());
        AssertNoTarget(feed, target);
        using var batch = await spectator.GetAsync(Url($"feed/reactions?commandIds={target}"), Ct);
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        var batchReactions = await batch.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Empty(batchReactions.EnumerateObject());
        var refusedId = Guid.NewGuid();
        using var refused = await WriteAsync(spectator, Reaction(target), refusedId, "🔥", false);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        await AssertNoWritesAsync([refusedId]);
    }

    [Fact]
    public async Task A_private_command_and_a_command_from_another_season_are_not_reaction_targets()
    {
        await EnableReactionsAsync(suggestions: true);
        var (player, roll) = await RollAsync();
        var admin = await _site.SignedInAsync("admin");
        var spectator = await _site.SignedInAsync("zritel");
        var suggestion = Guid.NewGuid();
        await PostOkAsync(player, Url("suggestions"), new { commandId = suggestion, kind = "event", text = "Идея для ведущего" });
        var anotherSeason = await _site.CreateSeasonAsync();
        var bus = _site.Services.GetRequiredService<CommandBus>();
        var enabled = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), anotherSeason, new ChangeRuleset(ReactionsRules()), AuthorId: null), Ct);
        Assert.True(enabled.IsAccepted, enabled.Rejection?.Detail);
        Assert.Equal([("❤️", 0, false), ("🔥", 0, false)], Reactions(await ReadFeedAsync(spectator), roll));
        AssertNoTarget(await ReadFeedAsync(admin), suggestion);

        var refusedIds = new List<Guid>();
        foreach (var url in new[] { Reaction(suggestion), Reaction(Guid.NewGuid()), Reaction(roll, anotherSeason) })
        {
            var requestId = Guid.NewGuid();
            refusedIds.Add(requestId);
            using var refused = await WriteAsync(spectator, url, requestId, "🔥", true);
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        }

        await AssertNoWritesAsync([.. refusedIds]);
    }

    [Fact]
    public async Task Even_the_admin_and_the_voter_cannot_react_to_an_anonymous_vote()
    {
        await EnableReactionsAsync(polls: true);
        var admin = await _site.SignedInAsync("admin");
        var player = await _site.SignedInAsync("vasya");
        var spectator = await _site.SignedInAsync("zritel");
        var pollId = await StartPollAsync(_site, admin, NominationJson(anonymous: true));
        var cast = Guid.NewGuid();
        var change = Guid.NewGuid();
        await VoteOkAsync(player, pollId, "Финиш", cast);
        await VoteOkAsync(player, pollId, "Реролл", change);

        var refusedIds = new List<Guid>();
        foreach (var client in new[] { admin, player, spectator })
        {
            var feed = await ReadFeedAsync(client);
            var visibleVote = Assert.Single(feed.GetProperty("entries").EnumerateArray(), entry => entry.GetProperty("commandId").GetGuid() == cast);
            Assert.Equal("vote-cast", visibleVote.GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Null, visibleVote.GetProperty("author").ValueKind);
            AssertNoTarget(feed, cast);
            AssertNoTarget(feed, change);

            foreach (var target in new[] { cast, change })
            {
                var requestId = Guid.NewGuid();
                refusedIds.Add(requestId);
                using var refused = await WriteAsync(client, Reaction(target), requestId, "🔥", true);
                Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            }
        }

        await AssertNoWritesAsync([.. refusedIds]);
    }

    [Fact]
    public async Task A_public_proof_row_gets_reactions_and_its_private_fields_stay_redacted()
    {
        await EnableReactionsAsync();
        var (_, runId) = await CompleteAsync();
        var player = await _site.SignedInAsync("vasya");
        var spectator = await _site.SignedInAsync("zritel");
        var proof = Guid.NewGuid();
        const string Link = "https://imgur.com/a/feed-reaction-private";
        const string Note = "Личная заметка автора";
        await PostOkAsync(player, Url($"runs/{runId}/proof"), new { commandId = proof, links = new[] { Link }, note = Note });

        var firstFeed = await ReadFeedAsync(spectator);
        var proofEntry = Assert.Single(firstFeed.GetProperty("entries").EnumerateArray(), entry => entry.GetProperty("commandId").GetGuid() == proof);
        Assert.Equal("proof-submitted", proofEntry.GetProperty("type").GetString());
        Assert.Equal([("❤️", 0, false), ("🔥", 0, false)], Reactions(firstFeed, proof));
        await SetAsync(spectator, proof, "🔥", true);

        var feed = await ReadFeedAsync(spectator);
        Assert.Equal([("❤️", 0, false), ("🔥", 1, true)], Reactions(feed, proof));
        var publicData = Assert.Single(feed.GetProperty("entries").EnumerateArray(), entry => entry.GetProperty("commandId").GetGuid() == proof)
            .GetProperty("data");
        Assert.False(publicData.TryGetProperty("note", out _));
        Assert.False(publicData.TryGetProperty("links", out _));
        Assert.DoesNotContain(Link, feed.ToString(), StringComparison.Ordinal);
        var authorFeed = await ReadFeedAsync(player);
        var authorData = Assert.Single(authorFeed.GetProperty("entries").EnumerateArray(), entry => entry.GetProperty("commandId").GetGuid() == proof)
            .GetProperty("data");
        Assert.Equal(Link, authorData.GetProperty("links")[0].GetString());
        Assert.Equal(Note, authorData.GetProperty("note").GetString());
    }

    [Fact]
    public async Task A_disabled_season_keeps_the_feed_readable_and_refuses_reactions()
    {
        var (_, target) = await RollAsync();
        var spectator = await _site.SignedInAsync("zritel");

        var feed = await ReadFeedAsync(spectator);
        Assert.Contains(feed.GetProperty("entries").EnumerateArray(), entry => entry.GetProperty("commandId").GetGuid() == target);
        Assert.False(feed.TryGetProperty("reactions", out _));
        var refusedId = Guid.NewGuid();
        using var refused = await WriteAsync(spectator, Reaction(target), refusedId, "🔥", true);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await AssertNoWritesAsync([refusedId]);
    }

    [Fact]
    public async Task A_feed_reaction_needs_authentication_csrf_and_valid_input()
    {
        await EnableReactionsAsync();
        var (_, target) = await RollAsync();
        var anonymous = await _site.AnonymousAsync();
        var spectator = await _site.SignedInAsync("zritel");
        var refusedId = Guid.NewGuid();
        var url = Reaction(target);

        using var denied = await WriteAsync(anonymous, url, refusedId, "🔥", true);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        spectator.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var noCsrf = await WriteAsync(spectator, url, refusedId, "🔥", true);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);

        await SiteFactory.RefreshCsrfAsync(spectator);
        using var missingState = await spectator.PutAsJsonAsync(url, new { commandId = refusedId, emoji = "🔥" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, missingState.StatusCode);
        using var malformed = await spectator.PutAsJsonAsync(url, new { commandId = refusedId, emoji = "🔥", selected = "yes" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var empty = await WriteAsync(spectator, url, refusedId, "", true);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using var unknownEmoji = await WriteAsync(spectator, url, refusedId, "😂", true);
        Assert.Equal(HttpStatusCode.Conflict, unknownEmoji.StatusCode);
        Assert.Equal([("❤️", 0, false), ("🔥", 0, false)], Reactions(await ReadFeedAsync(spectator), target));
        await AssertNoWritesAsync([refusedId]);
    }

    [Fact]
    public async Task Old_persisted_rules_with_extra_fields_do_not_break_either_feed_read()
    {
        await EnableReactionsAsync();
        var (_, target) = await RollAsync();
        await using (var db = _site.NewDb())
        {
            var season = await db.Seasons.SingleAsync(row => row.Id == SiteFactory.SeasonId, Ct);
            var oldRules = JsonNode.Parse(season.RulesetJson)!.AsObject();
            oldRules["retiredFeedSetting"] = true;
            season.RulesetJson = oldRules.ToJsonString();
            await db.SaveChangesAsync(Ct);
        }

        var spectator = await _site.SignedInAsync("zritel");
        using var feed = await spectator.GetAsync(Url("feed"), Ct);
        using var batch = await spectator.GetAsync(Url($"feed/reactions?commandIds={target}"), Ct);
        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
    }

    [Fact]
    public async Task Feed_pages_and_batch_reads_share_the_signed_in_accounts_read_budget()
    {
        await EnableReactionsAsync();
        var (_, target) = await RollAsync();
        var spectator = await _site.SignedInAsync("zritel");
        var feed = Url("feed?limit=1");
        var batch = Url($"feed/reactions?commandIds={target}");

        for (var index = 0; index < Hosting.AppSetup.SeasonReadsPerMinute; index++)
        {
            using var response = await spectator.GetAsync(index % 2 == 0 ? feed : batch, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var limitedFeed = await spectator.GetAsync(feed, Ct);
        using var limitedBatch = await spectator.GetAsync(batch, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedFeed.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedBatch.StatusCode);

        var anotherAccount = await _site.SignedInAsync("petya");
        using var allowed = await anotherAccount.GetAsync(feed, Ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Batch_reads_omit_technical_events_and_enforce_input_and_access()
    {
        await EnableReactionsAsync();
        var (_, target) = await RollAsync();
        var spectator = await _site.SignedInAsync("zritel");
        await SetAsync(spectator, target, "🔥", true);

        // A stored detail-only event has public data in the feed API, but it never forms a row without a headline.
        var technical = Guid.NewGuid();
        var stored = EventCodec.Encode(new ResourceChanged(_site.Players["vasya"], "testResource", 1, ResourceReason.AdminAdjustment));
        await using (var db = _site.NewDb())
        {
            var sequence = await db.Events.Where(e => e.SeasonId == SiteFactory.SeasonId).MaxAsync(e => e.Sequence, Ct);
            db.Events.Add(new GameEventRecord
            {
                SeasonId = SiteFactory.SeasonId,
                Sequence = sequence + 1,
                CommandId = technical,
                CommandType = "LegacyResourceAdjustment",
                CommandHash = new string('0', 64),
                Type = stored.Type,
                Version = stored.Version,
                Data = stored.Data,
                OccurredAt = _site.Clock.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var feed = await ReadFeedAsync(spectator);
        Assert.Contains(feed.GetProperty("entries").EnumerateArray(), entry => entry.GetProperty("commandId").GetGuid() == technical);
        AssertNoTarget(feed, technical);
        var refusedId = Guid.NewGuid();
        using var refused = await WriteAsync(spectator, Reaction(technical), refusedId, "🔥", true);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        var batch = Url($"feed/reactions?commandIds={target}&commandIds={technical}&commandIds={Guid.NewGuid()}");
        var counts = await OkAsync(await spectator.GetAsync(batch, Ct));
        var row = Assert.Single(counts.EnumerateObject());
        Assert.Equal(target.ToString(), row.Name);
        Assert.Contains(row.Value.EnumerateArray(), reaction => reaction.GetProperty("emoji").GetString() == "🔥"
            && reaction.GetProperty("count").GetInt32() == 1 && reaction.GetProperty("selected").GetBoolean());

        var anonymous = await _site.AnonymousAsync();
        using var denied = await anonymous.GetAsync(batch, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var malformed = await spectator.GetAsync(Url("feed/reactions?commandIds=invalid"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var tooMany = await spectator.GetAsync(Url($"feed/reactions?{string.Join('&', Enumerable.Repeat($"commandIds={target}", 101))}"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        await AssertNoWritesAsync([refusedId]);
    }

    private async Task<(HttpClient Player, Guid CommandId)> RollAsync()
    {
        var player = await _site.SignedInAsync("vasya");
        var commandId = Guid.NewGuid();
        await PostOkAsync(player, Url("roll"), new { commandId });
        return (player, commandId);
    }

    private async Task<(Guid CommandId, Guid RunId)> CompleteAsync()
    {
        var (player, _) = await RollAsync();
        await PostOkAsync(player, Url("start"));
        var commandId = Guid.NewGuid();
        await PostOkAsync(player, Url("complete"), new { commandId, difficulty = "normal" });
        var season = await OkAsync(await player.GetAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        return (commandId, season.GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetGuid());
    }

    private Task EnableReactionsAsync(bool polls = false, bool suggestions = false) =>
        _site.SendAsync(new ChangeRuleset(ReactionsRules(polls, suggestions)));

    private static Ruleset ReactionsRules(bool polls = false, bool suggestions = false)
    {
        var rules = RulesetJson.Default();
        return rules with
        {
            Features = rules.Features with { Reactions = true, Items = polls, Polls = polls, Suggestions = suggestions },
            Social = rules.Social with { Reactions = ["❤️", "🔥"] },
        };
    }

    private static string Reaction(Guid targetCommandId, Guid? seasonId = null) =>
        $"/api/seasons/{seasonId ?? SiteFactory.SeasonId}/feed/{targetCommandId}/reactions";

    private static Task<HttpResponseMessage> WriteAsync(HttpClient client, string url, Guid commandId, string emoji, bool selected) =>
        client.PutAsJsonAsync(url, new { commandId, emoji, selected }, Ct);

    private static async Task SetAsync(HttpClient client, Guid target, string emoji, bool selected, Guid? commandId = null)
    {
        using var response = await WriteAsync(client, Reaction(target), commandId ?? Guid.NewGuid(), emoji, selected);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task<JsonElement> ReadFeedAsync(HttpClient client, string query = "") =>
        await OkAsync(await client.GetAsync(Url($"feed{query}"), Ct));

    private static (string Emoji, int Count, bool Selected)[] Reactions(JsonElement feed, Guid commandId) =>
        [.. feed.GetProperty("reactions").GetProperty(commandId.ToString()).EnumerateArray()
            .Select(reaction => (reaction.GetProperty("emoji").GetString()!, reaction.GetProperty("count").GetInt32(), reaction.GetProperty("selected").GetBoolean()))];

    private static void AssertNoTarget(JsonElement feed, Guid commandId) =>
        Assert.False(feed.GetProperty("reactions").TryGetProperty(commandId.ToString(), out _));

    private async Task AssertNoWritesAsync(Guid[] commandIds)
    {
        await using var db = _site.NewDb();
        Assert.False(await db.Events.AnyAsync(entry => commandIds.Contains(entry.CommandId), Ct));
    }
}

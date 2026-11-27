using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Scoring;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Api.ApiCalls;
using static GameEvent.Web.Tests.Api.PollSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>Public feed threads, authorship, moderation and the seasonal daily limit (SPEC «Лента и общение»).</summary>
public sealed class FeedCommentApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Players_and_spectators_share_a_public_thread_without_changing_the_feed_cursor()
    {
        await EnableCommentsAsync();
        var target = await CompleteAsync();
        var player = await _site.SignedInAsync("petya");
        var spectator = await _site.SignedInAsync("zritel");
        var url = Thread(target);
        var feed = Url("feed?limit=1");
        var before = await OkAsync(await spectator.GetAsync(feed, Ct));
        Assert.Equal(target, Assert.Single(before.GetProperty("entries").EnumerateArray()).GetProperty("commandId").GetGuid());
        Assert.Contains(target, before.GetProperty("commentTargets").EnumerateArray().Select(entry => entry.GetGuid()));
        Assert.Empty(await CommentsAsync(spectator, url));

        var playerComment = Guid.NewGuid();
        var spectatorComment = Guid.NewGuid();
        const string PlayerText = "Разметка <b>как текст</b> & заметка.";
        const string SpectatorText = "Поздравляю с прохождением!";
        await ExpectAsync(PostAsync(player, url, playerComment, PlayerText), HttpStatusCode.NoContent);
        await ExpectAsync(PostAsync(spectator, url, spectatorComment, SpectatorText), HttpStatusCode.NoContent);

        var forPlayer = await CommentsAsync(player, url);
        var forSpectator = await CommentsAsync(spectator, url);
        Assert.Equal(2, forPlayer.Length);
        Assert.Equal(forPlayer.Select(c => c.Id), forSpectator.Select(c => c.Id));
        Assert.Equal(new CommentView(playerComment, "petya", PlayerText, true), Assert.Single(forPlayer, c => c.Id == playerComment));
        Assert.Equal(new CommentView(spectatorComment, "zritel", SpectatorText, false), Assert.Single(forPlayer, c => c.Id == spectatorComment));
        Assert.Equal(new CommentView(playerComment, "petya", PlayerText, false), Assert.Single(forSpectator, c => c.Id == playerComment));
        Assert.Equal(new CommentView(spectatorComment, "zritel", SpectatorText, true), Assert.Single(forSpectator, c => c.Id == spectatorComment));

        var after = await OkAsync(await spectator.GetAsync(feed, Ct));
        Assert.Equal(before.GetProperty("nextBefore").GetInt64(), after.GetProperty("nextBefore").GetInt64());
        Assert.Equal(
            Assert.Single(before.GetProperty("entries").EnumerateArray()).GetProperty("sequence").GetInt64(),
            Assert.Single(after.GetProperty("entries").EnumerateArray()).GetProperty("sequence").GetInt64());
    }

    [Fact]
    public async Task The_thread_cursor_is_stable_when_comments_share_a_timestamp_and_the_anchor_is_hidden()
    {
        await EnableCommentsAsync();
        var target = await CompleteAsync();
        var thread = Thread(target);
        var player = await _site.SignedInAsync("petya");
        var spectator = await _site.SignedInAsync("zritel");
        var admin = await _site.SignedInAsync("admin");
        var writers = new[] { player, spectator, await _site.SignedInAsync("vasya") };
        var posted = new HashSet<Guid>();
        _site.Clock.UtcNow = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 51; index++)
        {
            var id = Guid.NewGuid();
            posted.Add(id);
            await ExpectAsync(PostAsync(writers[index % writers.Length], thread, id, "Комментарий в обсуждении."), HttpStatusCode.NoContent);
        }

        using var newestResponse = await spectator.GetAsync(thread, Ct);
        var newest = await OkAsync(newestResponse);
        Assert.True(newestResponse.Headers.CacheControl?.NoStore);
        var first = newest.GetProperty("comments").EnumerateArray().Select(comment => comment.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(50, first.Length);
        var anchor = newest.GetProperty("nextBefore").GetGuid();
        Assert.Equal(first[0], anchor);

        await ExpectAsync(HideAsync(admin, target, anchor), HttpStatusCode.NoContent);
        using var olderResponse = await spectator.GetAsync($"{thread}?before={anchor}", Ct);
        var older = await OkAsync(olderResponse);
        var last = Assert.Single(older.GetProperty("comments").EnumerateArray()).GetProperty("id").GetGuid();
        Assert.DoesNotContain(last, first);
        Assert.True(posted.SetEquals([.. first, last]));
        Assert.Equal(JsonValueKind.Null, older.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task Only_the_author_can_edit_or_delete_and_only_the_admin_can_hide()
    {
        await EnableCommentsAsync();
        var target = await CompleteAsync();
        var url = Thread(target);
        var author = await _site.SignedInAsync("zritel");
        var other = await _site.SignedInAsync("petya");
        var admin = await _site.SignedInAsync("admin");
        var id = Guid.NewGuid();
        const string Original = "Получилось здорово.";
        const string Edited = "Больше всего понравился финиш.";
        await ExpectAsync(PostAsync(author, url, id, Original), HttpStatusCode.NoContent);

        await ExpectAsync(EditAsync(other, url, id, "Это чужой комментарий."), HttpStatusCode.Forbidden);
        await ExpectAsync(RemoveAsync(other, url, id), HttpStatusCode.Forbidden);
        await ExpectAsync(EditAsync(admin, url, id, "Даже админ не подменяет автора."), HttpStatusCode.Forbidden);
        await ExpectAsync(HideAsync(other, target, id), HttpStatusCode.Forbidden);
        await ExpectAsync(HideAsync(author, target, id), HttpStatusCode.Forbidden);
        Assert.Equal(new CommentView(id, "zritel", Original, false), Assert.Single(await CommentsAsync(other, url)));

        await ExpectAsync(EditAsync(author, url, id, Edited), HttpStatusCode.NoContent);
        Assert.Equal(new CommentView(id, "zritel", Edited, true), Assert.Single(await CommentsAsync(author, url)));
        Assert.Equal(new CommentView(id, "zritel", Edited, false), Assert.Single(await CommentsAsync(other, url)));
    }

    [Fact]
    public async Task Removed_and_hidden_text_disappears_for_both_readers_and_cannot_be_restored_by_editing()
    {
        await EnableCommentsAsync();
        var target = await CompleteAsync();
        var url = Thread(target);
        var player = await _site.SignedInAsync("petya");
        var spectator = await _site.SignedInAsync("zritel");
        var admin = await _site.SignedInAsync("admin");
        var hidden = Guid.NewGuid();
        var removed = Guid.NewGuid();
        const string HiddenText = "UniqueHiddenCommentText";
        const string RemovedText = "UniqueRemovedCommentText";
        await ExpectAsync(PostAsync(player, url, hidden, HiddenText), HttpStatusCode.NoContent);
        await ExpectAsync(PostAsync(spectator, url, removed, RemovedText), HttpStatusCode.NoContent);

        await ExpectAsync(HideAsync(admin, target, hidden), HttpStatusCode.NoContent);
        Assert.Equal(removed, Assert.Single(await CommentsAsync(spectator, url)).Id);
        await ExpectAsync(RemoveAsync(spectator, url, removed), HttpStatusCode.NoContent);

        using var editHidden = await EditAsync(player, url, hidden, HiddenText);
        using var editRemoved = await EditAsync(spectator, url, removed, RemovedText);
        Assert.False(editHidden.IsSuccessStatusCode);
        Assert.False(editRemoved.IsSuccessStatusCode);
        foreach (var reader in new[] { player, spectator })
        {
            var thread = await OkAsync(await reader.GetAsync(url, Ct));
            Assert.Empty(thread.GetProperty("comments").EnumerateArray());
            Assert.DoesNotContain(HiddenText, thread.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(RemovedText, thread.ToString(), StringComparison.Ordinal);
            var publicFeed = await OkAsync(await reader.GetAsync(Url("feed"), Ct));
            Assert.DoesNotContain(HiddenText, publicFeed.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(RemovedText, publicFeed.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Europe/Moscow", 20)]
    [InlineData("UTC", 23)]
    public async Task The_daily_limit_is_per_account_across_threads_and_resets_at_season_midnight(string timezone, int lastUtcHour)
    {
        await EnableCommentsAsync(commentsPerDay: 1, timezone: timezone);
        var first = await CompleteAsync();
        var second = await RollAsync("petya");
        var firstThread = Thread(first);
        var secondThread = Thread(second);
        var spectator = await _site.SignedInAsync("zritel");
        var secondTab = await _site.SignedInAsync("zritel");
        var player = await _site.SignedInAsync("petya");
        var admin = await _site.SignedInAsync("admin");
        var spectatorComment = Guid.NewGuid();
        var playerComment = Guid.NewGuid();
        var retryId = Guid.NewGuid();
        _site.Clock.UtcNow = new DateTimeOffset(2026, 10, 1, lastUtcHour, 59, 0, TimeSpan.Zero);

        await Task.WhenAll(
            ExpectAsync(PostAsync(spectator, firstThread, spectatorComment, "Один комментарий.", retryId), HttpStatusCode.NoContent),
            ExpectAsync(PostAsync(secondTab, firstThread, spectatorComment, "Один комментарий.", retryId), HttpStatusCode.NoContent));
        Assert.Equal(spectatorComment, Assert.Single(await CommentsAsync(secondTab, firstThread)).Id);
        await ExpectAsync(EditAsync(secondTab, firstThread, spectatorComment, "Правка не тратит лимит."), HttpStatusCode.NoContent);
        await ExpectAsync(PostAsync(secondTab, secondThread, Guid.NewGuid(), "Другой тред в те же сутки."), HttpStatusCode.Conflict);
        await ExpectAsync(PostAsync(player, secondThread, playerComment, "Другой аккаунт может написать."), HttpStatusCode.NoContent);

        await ExpectAsync(RemoveAsync(spectator, firstThread, spectatorComment), HttpStatusCode.NoContent);
        await ExpectAsync(HideAsync(admin, second, playerComment), HttpStatusCode.NoContent);
        // A delayed HTTP retry can confirm the original command without reviving the deleted comment or resetting its debit.
        await ExpectAsync(PostAsync(secondTab, firstThread, spectatorComment, "Один комментарий.", retryId), HttpStatusCode.NoContent);
        Assert.Empty(await CommentsAsync(secondTab, firstThread));
        await ExpectAsync(PostAsync(spectator, secondThread, Guid.NewGuid(), "Удалённый тоже учтён."), HttpStatusCode.Conflict);
        await ExpectAsync(PostAsync(player, firstThread, Guid.NewGuid(), "Скрытый тоже учтён."), HttpStatusCode.Conflict);

        _site.Clock.Advance(TimeSpan.FromMinutes(1));
        var afterMidnight = Guid.NewGuid();
        await ExpectAsync(PostAsync(spectator, secondThread, afterMidnight, "Новые сутки сезона."), HttpStatusCode.NoContent);
        Assert.Equal(afterMidnight, Assert.Single(await CommentsAsync(player, secondThread)).Id);
    }

    [Fact]
    public async Task Private_anonymous_technical_unknown_and_other_season_rows_have_no_public_thread()
    {
        await EnableCommentsAsync(visibilityFixtures: true);
        var target = await CompleteAsync();
        var player = await _site.SignedInAsync("vasya");
        var spectator = await _site.SignedInAsync("zritel");
        var admin = await _site.SignedInAsync("admin");

        var suggestion = Guid.NewGuid();
        await PostOkAsync(player, Url("suggestions"), new { commandId = suggestion, kind = "event", text = "Идея только для админа" });
        var poll = await StartPollAsync(_site, admin, NominationJson(anonymous: true));
        var cast = Guid.NewGuid();
        var change = Guid.NewGuid();
        await VoteOkAsync(player, poll, "Финиш", cast);
        await VoteOkAsync(player, poll, "Реролл", change);
        var other = await _site.CreateSeasonAsync();
        var bus = _site.Services.GetRequiredService<CommandBus>();
        var nextRules = new ChangeRuleset(CommentsRules(RulesetJson.Default()));
        var enabled = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), other, nextRules, AuthorId: null), Ct);
        Assert.True(enabled.IsAccepted, enabled.Rejection?.ToString());
        var technical = await AddLegacyTechnicalRowAsync();

        await AssertUnavailableAsync(admin, Thread(suggestion));
        await AssertUnavailableAsync(player, Thread(cast));
        await AssertUnavailableAsync(spectator, Thread(change));
        await AssertUnavailableAsync(spectator, Thread(technical));
        await AssertUnavailableAsync(spectator, Thread(Guid.NewGuid()));
        await AssertUnavailableAsync(spectator, Thread(target, other));
    }

    [Fact]
    public async Task Undoing_the_public_source_keeps_its_row_but_closes_its_comment_thread()
    {
        await EnableCommentsAsync();
        var roll = await RollAsync("vasya");
        _site.Clock.Advance(TimeSpan.FromMinutes(2));
        var admin = await _site.SignedInAsync("admin");
        var spectator = await _site.SignedInAsync("zritel");
        var url = Thread(roll);
        var id = Guid.NewGuid();
        await ExpectAsync(PostAsync(spectator, url, id, "Комментарий к броску."), HttpStatusCode.NoContent);
        Assert.Equal(id, Assert.Single(await CommentsAsync(spectator, url)).Id);

        await PostOkAsync(admin, AdminUrl("undo"), new { commandId = Guid.NewGuid(), targetCommandId = roll, comment = "Ошибся кнопкой" });

        var feed = await OkAsync(await spectator.GetAsync(Url("feed"), Ct));
        var row = Assert.Single(feed.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("commandId").GetGuid() == roll && e.GetProperty("type").GetString() == "game-rolled");
        Assert.True(row.GetProperty("undone").GetBoolean());
        await AssertUnavailableAsync(spectator, url);
    }

    [Fact]
    public async Task An_existing_comment_id_cannot_be_changed_through_another_public_thread()
    {
        await EnableCommentsAsync();
        var first = await CompleteAsync();
        var second = await RollAsync("petya");
        var author = await _site.SignedInAsync("zritel");
        var admin = await _site.SignedInAsync("admin");
        var id = Guid.NewGuid();
        const string Original = "Комментарий к первой записи.";
        await ExpectAsync(PostAsync(author, Thread(first), id, Original), HttpStatusCode.NoContent);

        Assert.Empty(await CommentsAsync(author, Thread(second)));
        await ExpectAsync(EditAsync(author, Thread(second), id, "Чужая ветка."), HttpStatusCode.NotFound);
        await ExpectAsync(RemoveAsync(author, Thread(second), id), HttpStatusCode.NotFound);
        await ExpectAsync(HideAsync(admin, second, id), HttpStatusCode.NotFound);
        Assert.Equal(new CommentView(id, "zritel", Original, true), Assert.Single(await CommentsAsync(author, Thread(first))));
    }

    [Fact]
    public async Task The_default_flag_closes_the_thread_while_the_feed_remains_readable()
    {
        var target = await RollAsync("vasya");
        var spectator = await _site.SignedInAsync("zritel");
        var feed = await OkAsync(await spectator.GetAsync(Url("feed"), Ct));
        Assert.Contains(feed.GetProperty("entries").EnumerateArray(), e => e.GetProperty("commandId").GetGuid() == target);
        Assert.False(feed.TryGetProperty("commentTargets", out _));

        var url = Thread(target);
        using var cannotRead = await spectator.GetAsync(url, Ct);
        using var cannotPost = await PostAsync(spectator, url, Guid.NewGuid(), "Флаг выключен.");
        await AssertConflictAsync(cannotRead, "feature.disabled");
        await AssertConflictAsync(cannotPost, "feature.disabled");
    }

    [Fact]
    public async Task A_feed_comment_requires_authentication_and_a_write_needs_csrf()
    {
        var target = await RollAsync("vasya");
        var anonymous = await _site.AnonymousAsync();
        var spectator = await _site.SignedInAsync("zritel");
        var url = Thread(target);
        var id = Guid.NewGuid();

        await ExpectAsync(anonymous.GetAsync(url, Ct), HttpStatusCode.Unauthorized);
        await ExpectAsync(PostAsync(anonymous, url, id, "Нужен вход."), HttpStatusCode.Unauthorized);
        await ExpectAsync(EditAsync(anonymous, url, id, "Нужен вход."), HttpStatusCode.Unauthorized);
        await ExpectAsync(RemoveAsync(anonymous, url, id), HttpStatusCode.Unauthorized);
        await ExpectAsync(HideAsync(anonymous, target, id), HttpStatusCode.Unauthorized);

        spectator.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await ExpectAsync(PostAsync(spectator, url, id, "Нужен токен."), HttpStatusCode.BadRequest);
        await ExpectAsync(EditAsync(spectator, url, id, "Нужен токен."), HttpStatusCode.BadRequest);
        await ExpectAsync(RemoveAsync(spectator, url, id), HttpStatusCode.BadRequest);
        var admin = await _site.SignedInAsync("admin");
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await ExpectAsync(HideAsync(admin, target, id), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Missing_ids_and_invalid_text_are_rejected_without_changing_the_thread()
    {
        await EnableCommentsAsync();
        var target = await CompleteAsync();
        var spectator = await _site.SignedInAsync("zritel");
        var admin = await _site.SignedInAsync("admin");
        var url = Thread(target);
        var id = Guid.NewGuid();
        const string Original = "Целый комментарий.";
        await ExpectAsync(PostAsync(spectator, url, id, Original), HttpStatusCode.NoContent);

        await ExpectAsync(spectator.PostAsJsonAsync(url, new { commentId = Guid.NewGuid(), text = "Пропущен ID команды." }, Ct), HttpStatusCode.BadRequest);
        await ExpectAsync(PostAsync(spectator, url, Guid.Empty, "Невалидный ID комментария."), HttpStatusCode.BadRequest);
        await ExpectAsync(spectator.PostAsJsonAsync(url, new { commandId = Guid.NewGuid(), commentId = Guid.NewGuid(), text = 42 }, Ct), HttpStatusCode.BadRequest);
        await ExpectAsync(EditAsync(spectator, url, id, " \n\t"), HttpStatusCode.BadRequest);
        await ExpectAsync(spectator.DeleteAsync($"{url}/{id}", Ct), HttpStatusCode.BadRequest);
        await ExpectAsync(admin.PostAsJsonAsync(HideUrl(target, id), new { }, Ct), HttpStatusCode.BadRequest);
        Assert.Equal(new CommentView(id, "zritel", Original, true), Assert.Single(await CommentsAsync(spectator, url)));
    }

    private Task EnableCommentsAsync(int? commentsPerDay = null, bool visibilityFixtures = false, string timezone = "Europe/Moscow") =>
        RulesAsync(_site, r => CommentsRules(r, commentsPerDay, visibilityFixtures, timezone));

    private static Ruleset CommentsRules(Ruleset rules, int? commentsPerDay = null, bool visibilityFixtures = false, string timezone = "Europe/Moscow")
    {
        var features = rules.Features with { Comments = true };
        if (visibilityFixtures)
        {
            features = features with { Items = true, Polls = true, Suggestions = true };
        }

        return rules with
        {
            Features = features,
            Season = rules.Season with { Timezone = timezone },
            Social = rules.Social with { CommentsPerDay = commentsPerDay ?? rules.Social.CommentsPerDay },
        };
    }

    private async Task<Guid> CompleteAsync()
    {
        var player = await _site.SignedInAsync("vasya");
        await PostOkAsync(player, Url("roll"));
        await PostOkAsync(player, Url("start"));
        var commandId = Guid.NewGuid();
        await PostOkAsync(player, Url("complete"), new { commandId, difficulty = "normal" });
        return commandId;
    }

    private async Task<Guid> RollAsync(string login)
    {
        var player = await _site.SignedInAsync(login);
        var commandId = Guid.NewGuid();
        await PostOkAsync(player, Url("roll"), new { commandId });
        return commandId;
    }

    private async Task<Guid> AddLegacyTechnicalRowAsync()
    {
        // A standalone fact from an older log can be part of a feed payload without forming a visible row.
        var id = Guid.NewGuid();
        var stored = EventCodec.Encode(new ResourceChanged(_site.Players["vasya"], "legacyResource", 1, ResourceReason.AdminAdjustment));
        await using var db = _site.NewDb();
        var previous = await db.Events.Where(e => e.SeasonId == SiteFactory.SeasonId).MaxAsync(e => e.Sequence, Ct);
        db.Events.Add(new GameEventRecord
        {
            SeasonId = SiteFactory.SeasonId,
            Sequence = previous + 1,
            CommandId = id,
            CommandType = "LegacyResourceAdjustment",
            CommandHash = new string('0', 64),
            Type = stored.Type,
            Version = stored.Version,
            Data = stored.Data,
            OccurredAt = _site.Clock.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
        return id;
    }

    private static string Thread(Guid target, Guid? season = null) => $"/api/seasons/{season ?? SiteFactory.SeasonId}/feed/{target}/comments";

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Guid commentId, string text, Guid? command = null) =>
        client.PostAsJsonAsync(url, new { commandId = command ?? Guid.NewGuid(), commentId, text }, Ct);

    private static Task<HttpResponseMessage> EditAsync(HttpClient client, string url, Guid id, string text) =>
        client.PutAsJsonAsync($"{url}/{id}", new { commandId = Guid.NewGuid(), text }, Ct);

    private static Task<HttpResponseMessage> RemoveAsync(HttpClient client, string url, Guid id) =>
        client.DeleteAsync($"{url}/{id}?commandId={Guid.NewGuid()}", Ct);

    private static Task<HttpResponseMessage> HideAsync(HttpClient client, Guid target, Guid id) =>
        client.PostAsJsonAsync(HideUrl(target, id), new { commandId = Guid.NewGuid() }, Ct);

    private static string HideUrl(Guid target, Guid id) => AdminUrl($"feed/{target}/comments/{id}/hide");

    private static async Task AssertUnavailableAsync(HttpClient client, string url)
    {
        await ExpectAsync(client.GetAsync(url, Ct), HttpStatusCode.NotFound);
        await ExpectAsync(PostAsync(client, url, Guid.NewGuid(), "Без публичной записи нет обсуждения."), HttpStatusCode.NotFound);
    }

    private static async Task ExpectAsync(Task<HttpResponseMessage> request, HttpStatusCode expected)
    {
        using var response = await request;
        Assert.True(response.StatusCode == expected,
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: expected {expected}, received {response.StatusCode}. {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task<CommentView[]> CommentsAsync(HttpClient reader, string url)
    {
        var thread = await OkAsync(await reader.GetAsync(url, Ct));
        return [.. thread.GetProperty("comments").EnumerateArray().Select(c => new CommentView(
            c.GetProperty("id").GetGuid(),
            c.GetProperty("author").GetString()!,
            c.GetProperty("text").GetString()!,
            c.GetProperty("canEdit").GetBoolean()))];
    }

    private sealed record CommentView(Guid Id, string Author, string Text, bool CanEdit);
}

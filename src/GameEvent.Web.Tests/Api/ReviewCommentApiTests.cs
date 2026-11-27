using System.Net;
using System.Net.Http.Json;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Seasons;
using Microsoft.EntityFrameworkCore;
using static GameEvent.Web.Tests.Api.ApiCalls;
using static GameEvent.Web.Tests.Api.PollSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>Comments on a published review and their shared seasonal allowance (SPEC «Лента и общение»).</summary>
public sealed class ReviewCommentApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Players_and_spectators_can_comment_and_editing_the_review_keeps_its_thread()
    {
        await EnableCommentsAsync();
        var (gameId, runId, _) = await CompleteAsync();
        var owner = await _site.SignedInAsync("vasya");
        var player = await _site.SignedInAsync("petya");
        var spectator = await _site.SignedInAsync("zritel");
        var thread = ReviewThread(gameId, runId);
        var byPlayer = Guid.NewGuid();
        var bySpectator = Guid.NewGuid();
        var publicRuns = await OkAsync(await spectator.GetAsync($"/api/pool/{gameId}/runs", Ct));
        var publicReview = Assert.Single(publicRuns.EnumerateArray(), item => item.GetProperty("runId").GetGuid() == runId);
        Assert.True(publicReview.GetProperty("canComment").GetBoolean());
        Assert.Empty(await ReadAsync(spectator, thread));

        await ExpectAsync(PostAsync(player, thread, byPlayer, "Спасибо за отзыв!"), HttpStatusCode.NoContent);
        await ExpectAsync(PostAsync(spectator, thread, bySpectator, "Текст <b>без разметки</b>."), HttpStatusCode.NoContent);
        var before = await ReadAsync(spectator, thread);
        Assert.Equal(2, before.Length);
        Assert.Equal(new CommentView(byPlayer, "petya", "Спасибо за отзыв!", false), Assert.Single(before, c => c.Id == byPlayer));
        Assert.Equal(new CommentView(bySpectator, "zritel", "Текст <b>без разметки</b>.", true), Assert.Single(before, c => c.Id == bySpectator));

        await WriteReviewAsync(owner, runId, 9, "Добавил замечание о сложности последней главы.");

        Assert.Equal(before, await ReadAsync(spectator, thread));
        var forOwner = await ReadAsync(owner, thread);
        Assert.Equal(before.Select(c => c.Id), forOwner.Select(c => c.Id));
        Assert.All(forOwner, item => Assert.False(item.CanEdit));
    }

    [Fact]
    public async Task Only_the_matching_game_and_run_with_a_published_review_have_a_thread()
    {
        await EnableCommentsAsync();
        var (gameId, runId, _) = await CompleteAsync(withReview: false);
        var player = await _site.SignedInAsync("vasya");
        var spectator = await _site.SignedInAsync("zritel");
        await using var db = _site.NewDb();
        var anotherGame = await db.Games.AsNoTracking().Where(game => game.Id != gameId).Select(game => game.Id).FirstAsync(Ct);

        await AssertMissingAsync(spectator, ReviewThread(gameId, runId));
        await WriteReviewAsync(player, runId, 7, "Стоит пройти.");
        Assert.Empty(await ReadAsync(spectator, ReviewThread(gameId, runId)));
        await AssertMissingAsync(spectator, ReviewThread(anotherGame, runId));
        await AssertMissingAsync(spectator, ReviewThread(gameId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Deleting_the_game_hides_its_existing_review_thread_and_refuses_new_comments()
    {
        await EnableCommentsAsync();
        var (gameId, runId, _) = await CompleteAsync();
        var spectator = await _site.SignedInAsync("zritel");
        var admin = await _site.SignedInAsync("admin");
        var thread = ReviewThread(gameId, runId);
        var id = Guid.NewGuid();
        await ExpectAsync(PostAsync(spectator, thread, id, "Обсуждение отзыва."), HttpStatusCode.NoContent);
        Assert.Equal(id, Assert.Single(await ReadAsync(spectator, thread)).Id);

        await PostOkAsync(admin, $"/api/admin/pool/{gameId}/delete", new { commandId = Guid.NewGuid(), reason = "Дубликат игры" });

        await AssertMissingAsync(spectator, thread);
    }

    [Fact]
    public async Task The_comment_author_can_edit_or_delete_and_only_the_admin_can_hide_the_comment()
    {
        await EnableCommentsAsync();
        var (gameId, runId, _) = await CompleteAsync();
        var owner = await _site.SignedInAsync("vasya");
        var spectator = await _site.SignedInAsync("zritel");
        var admin = await _site.SignedInAsync("admin");
        var thread = ReviewThread(gameId, runId);
        var id = Guid.NewGuid();
        const string Original = "Старый комментарий.";
        const string Edited = "UniqueEditedReviewCommentText";
        await ExpectAsync(PostAsync(spectator, thread, id, Original), HttpStatusCode.NoContent);

        await ExpectAsync(EditAsync(owner, thread, id, "Владелец отзыва не автор комментария."), HttpStatusCode.Forbidden);
        await ExpectAsync(DeleteAsync(owner, thread, id), HttpStatusCode.Forbidden);
        await ExpectAsync(HideAsync(spectator, runId, id), HttpStatusCode.Forbidden);
        await ExpectAsync(EditAsync(spectator, thread, id, Edited), HttpStatusCode.NoContent);
        Assert.Equal(new CommentView(id, "zritel", Edited, false), Assert.Single(await ReadAsync(owner, thread)));

        await ExpectAsync(HideAsync(admin, runId, id), HttpStatusCode.NoContent);
        foreach (var reader in new[] { owner, spectator })
        {
            var result = await OkAsync(await reader.GetAsync(thread, Ct));
            Assert.Empty(result.GetProperty("comments").EnumerateArray());
            Assert.DoesNotContain(Edited, result.ToString(), StringComparison.Ordinal);
        }

        var second = Guid.NewGuid();
        await ExpectAsync(PostAsync(spectator, thread, second, "Мой новый комментарий."), HttpStatusCode.NoContent);
        await ExpectAsync(DeleteAsync(spectator, thread, second), HttpStatusCode.NoContent);
        Assert.Empty(await ReadAsync(owner, thread));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_single_daily_limit_covers_both_feed_and_review_threads(bool reviewFirst)
    {
        await EnableCommentsAsync(commentsPerDay: 1);
        var (gameId, runId, feedCommand) = await CompleteAsync();
        var spectator = await _site.SignedInAsync("zritel");
        var player = await _site.SignedInAsync("petya");
        var feedThread = Url($"feed/{feedCommand}/comments");
        var reviewThread = ReviewThread(gameId, runId);
        var first = reviewFirst ? reviewThread : feedThread;
        var second = reviewFirst ? feedThread : reviewThread;
        var id = Guid.NewGuid();

        await ExpectAsync(PostAsync(spectator, first, id, "Единственный комментарий на сегодня."), HttpStatusCode.NoContent);
        await ExpectAsync(PostAsync(spectator, second, Guid.NewGuid(), "Другой вид обсуждения."), HttpStatusCode.Conflict);
        Assert.Empty(await ReadAsync(spectator, second));
        await ExpectAsync(DeleteAsync(spectator, first, id), HttpStatusCode.NoContent);
        await ExpectAsync(PostAsync(spectator, second, Guid.NewGuid(), "Удаление не возвращает лимит."), HttpStatusCode.Conflict);

        var otherAccount = Guid.NewGuid();
        await ExpectAsync(PostAsync(player, second, otherAccount, "Лимит принадлежит аккаунту."), HttpStatusCode.NoContent);
        Assert.Equal(new CommentView(otherAccount, "petya", "Лимит принадлежит аккаунту.", false), Assert.Single(await ReadAsync(spectator, second)));
    }

    [Fact]
    public async Task Two_distinct_concurrent_posts_in_different_threads_share_one_remaining_slot()
    {
        await EnableCommentsAsync(commentsPerDay: 1);
        var (gameId, runId, feedCommand) = await CompleteAsync();
        using var firstTab = await _site.SignedInAsync("zritel");
        using var secondTab = await _site.SignedInAsync("zritel");
        var feed = Url($"feed/{feedCommand}/comments");
        var review = ReviewThread(gameId, runId);
        var requests = await Task.WhenAll(
            PostAsync(firstTab, feed, Guid.NewGuid(), "Комментарий в ленте."),
            PostAsync(secondTab, review, Guid.NewGuid(), "Комментарий к отзыву."));

        using var accepted = Assert.Single(requests, response => response.StatusCode == HttpStatusCode.NoContent);
        using var rejected = Assert.Single(requests, response => response.StatusCode == HttpStatusCode.Conflict);
        await AssertConflictAsync(rejected, "comment.dailyLimit");
        Assert.Single((await ReadAsync(firstTab, feed)).Concat(await ReadAsync(secondTab, review)));
    }

    [Fact]
    public async Task Both_archived_public_targets_can_be_discussed_without_changing_the_frozen_standings()
    {
        await EnableCommentsAsync();
        var (gameId, runId, feedCommand) = await CompleteAsync();
        var spectator = await _site.SignedInAsync("zritel");
        var admin = await _site.SignedInAsync("admin");
        var seasonUrl = $"/api/seasons/{SiteFactory.SeasonId}";
        var thread = ReviewThread(gameId, runId);
        await _site.SendAsync(new ApproveProof(runId, Comment: "Видел прохождение"));
        await _site.SendAsync(new ChangeSeasonStatus(SeasonStatus.Closing));
        await _site.SendAsync(new ChangeSeasonStatus(SeasonStatus.Finished));
        await _site.SendAsync(new ChangeSeasonStatus(SeasonStatus.Archived));
        var before = await OkAsync(await spectator.GetAsync(seasonUrl, Ct));
        Assert.NotEmpty(before.GetProperty("leaderboard").EnumerateArray());

        var id = Guid.NewGuid();
        await ExpectAsync(PostAsync(spectator, thread, id, "Вспомнил эту игру после сезона."), HttpStatusCode.NoContent);
        Assert.Equal(id, Assert.Single(await ReadAsync(spectator, thread)).Id);
        await ExpectAsync(HideAsync(admin, runId, id), HttpStatusCode.NoContent);
        Assert.Empty(await ReadAsync(spectator, thread));

        var feedThread = Url($"feed/{feedCommand}/comments");
        var feedId = Guid.NewGuid();
        var feedPost = Guid.NewGuid();
        await ExpectAsync(spectator.PostAsJsonAsync(feedThread, new
        {
            commandId = feedPost,
            commentId = feedId,
            text = "Вспомнил запись после сезона.",
        }, Ct), HttpStatusCode.NoContent);
        Assert.Equal(feedId, Assert.Single(await ReadAsync(spectator, feedThread)).Id);
        await ExpectAsync(admin.PostAsJsonAsync(AdminUrl($"feed/{feedCommand}/comments/{feedId}/hide"), new { commandId = Guid.NewGuid() }, Ct),
            HttpStatusCode.NoContent);
        Assert.Empty(await ReadAsync(spectator, feedThread));
        using var refusedUndo = await admin.PostAsJsonAsync(AdminUrl("undo"), new
        {
            commandId = Guid.NewGuid(),
            targetCommandId = feedPost,
            comment = "Попытка отменить публикацию в архиве",
        }, Ct);
        await AssertConflictAsync(refusedUndo, "season.closed");

        var after = await OkAsync(await spectator.GetAsync(seasonUrl, Ct));
        Assert.Equal(before.GetProperty("status").GetString(), after.GetProperty("status").GetString());
        Assert.Equal(before.GetProperty("leaderboard").GetRawText(), after.GetProperty("leaderboard").GetRawText());
    }

    [Fact]
    public async Task The_review_comment_routes_require_a_session_csrf_and_well_formed_input()
    {
        var (gameId, runId, _) = await CompleteAsync();
        var reviewer = await _site.SignedInAsync("admin");
        var spectator = await _site.SignedInAsync("zritel");
        var anonymous = await _site.AnonymousAsync();
        var thread = ReviewThread(gameId, runId);
        var id = Guid.NewGuid();

        await ExpectAsync(anonymous.GetAsync(thread, Ct), HttpStatusCode.Unauthorized);
        await ExpectAsync(PostAsync(anonymous, thread, id, "Требуется вход."), HttpStatusCode.Unauthorized);
        await ExpectAsync(EditAsync(anonymous, thread, id, "Требуется вход."), HttpStatusCode.Unauthorized);
        await ExpectAsync(DeleteAsync(anonymous, thread, id), HttpStatusCode.Unauthorized);
        await ExpectAsync(HideAsync(anonymous, runId, id), HttpStatusCode.Unauthorized);
        using var cannotRead = await spectator.GetAsync(thread, Ct);
        await AssertConflictAsync(cannotRead, "feature.disabled");
        using var cannotPost = await PostAsync(spectator, thread, id, "Флаг выключен.");
        await AssertConflictAsync(cannotPost, "feature.disabled");

        await EnableCommentsAsync();
        spectator.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await ExpectAsync(PostAsync(spectator, thread, id, "Требуется токен."), HttpStatusCode.BadRequest);
        await SiteFactory.RefreshCsrfAsync(spectator);
        await ExpectAsync(PostAsync(spectator, thread, id, "Корректный комментарий."), HttpStatusCode.NoContent);
        await ExpectAsync(PostAsync(spectator, thread, Guid.Empty, "Пустой ID."), HttpStatusCode.BadRequest);
        await ExpectAsync(EditAsync(spectator, thread, id, " \n\t"), HttpStatusCode.BadRequest);
        await ExpectAsync(spectator.DeleteAsync($"{thread}/{id}", Ct), HttpStatusCode.BadRequest);
        await ExpectAsync(reviewer.PostAsJsonAsync(HideUrl(runId, id), new { }, Ct), HttpStatusCode.BadRequest);
        Assert.Equal(id, Assert.Single(await ReadAsync(spectator, thread)).Id);
    }

    private Task EnableCommentsAsync(int? commentsPerDay = null) => RulesAsync(_site, rules => rules with
    {
        Features = rules.Features with { Comments = true },
        Social = rules.Social with { CommentsPerDay = commentsPerDay ?? rules.Social.CommentsPerDay },
    });

    private async Task<(Guid GameId, Guid RunId, Guid FeedCommand)> CompleteAsync(bool withReview = true)
    {
        var player = await _site.SignedInAsync("vasya");
        await PostOkAsync(player, Url("roll"));
        await PostOkAsync(player, Url("start"));
        var commandId = Guid.NewGuid();
        await PostOkAsync(player, Url("complete"), new { commandId, difficulty = "normal" });
        var season = await OkAsync(await player.GetAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        var completed = season.GetProperty("me").GetProperty("lastCompleted");
        var runId = completed.GetProperty("id").GetGuid();
        if (withReview)
        {
            await WriteReviewAsync(player, runId, 7, "Стоит пройти.");
        }

        return (completed.GetProperty("game").GetProperty("id").GetGuid(), runId, commandId);
    }

    private static Task WriteReviewAsync(HttpClient owner, Guid runId, int rating, string text) =>
        PostOkAsync(owner, Url($"runs/{runId}/review"), new { commandId = Guid.NewGuid(), rating, text });

    private static string ReviewThread(Guid gameId, Guid runId) => $"/api/pool/{gameId}/runs/{runId}/review-comments";

    private static string HideUrl(Guid runId, Guid id) => AdminUrl($"reviews/{runId}/comments/{id}/hide");

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string thread, Guid commentId, string text) =>
        client.PostAsJsonAsync(thread, new { commandId = Guid.NewGuid(), commentId, text }, Ct);

    private static Task<HttpResponseMessage> EditAsync(HttpClient client, string thread, Guid id, string text) =>
        client.PutAsJsonAsync($"{thread}/{id}", new { commandId = Guid.NewGuid(), text }, Ct);

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string thread, Guid id) =>
        client.DeleteAsync($"{thread}/{id}?commandId={Guid.NewGuid()}", Ct);

    private static Task<HttpResponseMessage> HideAsync(HttpClient client, Guid runId, Guid id) =>
        client.PostAsJsonAsync(HideUrl(runId, id), new { commandId = Guid.NewGuid() }, Ct);

    private static async Task AssertMissingAsync(HttpClient reader, string thread)
    {
        await ExpectAsync(reader.GetAsync(thread, Ct), HttpStatusCode.NotFound);
        await ExpectAsync(PostAsync(reader, thread, Guid.NewGuid(), "Не к чему прикрепить комментарий."), HttpStatusCode.NotFound);
    }

    private static async Task<CommentView[]> ReadAsync(HttpClient reader, string thread)
    {
        var result = await OkAsync(await reader.GetAsync(thread, Ct));
        return [.. result.GetProperty("comments").EnumerateArray().Select(c => new CommentView(
            c.GetProperty("id").GetGuid(),
            c.GetProperty("author").GetString()!,
            c.GetProperty("text").GetString()!,
            c.GetProperty("canEdit").GetBoolean()))];
    }

    private static async Task ExpectAsync(Task<HttpResponseMessage> request, HttpStatusCode expected)
    {
        using var response = await request;
        Assert.True(response.StatusCode == expected,
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: expected {expected}, received {response.StatusCode}. {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private sealed record CommentView(Guid Id, string Author, string Text, bool CanEdit);
}

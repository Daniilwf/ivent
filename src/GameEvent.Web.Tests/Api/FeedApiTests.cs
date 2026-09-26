using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The public reads of E1 (SPEC «Лента», «Отзыв … виден в ленте, профиле и на странице игры»; D-124): the season's feed
/// page by page with authors and undone commands, a user's profile with seasons and reviews, and the runs of a game.
/// </summary>
public sealed class FeedApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    private static string Feed => $"/api/seasons/{SiteFactory.SeasonId}/feed";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task The_feed_is_the_season_log_newest_first_with_authors_and_data()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, Url("roll"));

        var feed = await OkAsync(await (await _site.SignedInAsync("zritel")).GetAsync(Feed, Ct));

        var entries = feed.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal("game-rolled", entries[0].GetProperty("type").GetString());
        Assert.Equal("vasya", entries[0].GetProperty("author").GetString());
        Assert.Equal(_site.Players["vasya"], entries[0].GetProperty("data").GetProperty("playerId").GetGuid());
        Assert.False(entries[0].GetProperty("undone").GetBoolean());
        Assert.Equal("season-created", entries[^1].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, entries[^1].GetProperty("author").ValueKind);
        var sequences = entries.Select(e => e.GetProperty("sequence").GetInt64()).ToList();
        Assert.Equal(sequences.OrderDescending(), sequences);
        Assert.Equal(JsonValueKind.Null, feed.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task The_feed_comes_in_pages()
    {
        var reader = await _site.SignedInAsync("petya");

        var first = await OkAsync(await reader.GetAsync($"{Feed}?limit=2", Ct));
        var next = first.GetProperty("nextBefore").GetInt64();
        var second = await OkAsync(await reader.GetAsync($"{Feed}?limit=2&before={next}", Ct));
        var all = await OkAsync(await reader.GetAsync(Feed, Ct));

        var paged = first.GetProperty("entries").EnumerateArray().Concat(second.GetProperty("entries").EnumerateArray()).Select(e => e.GetProperty("sequence").GetInt64());
        Assert.Equal(all.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("sequence").GetInt64()), paged);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextBefore").ValueKind);
        Assert.Empty((await OkAsync(await reader.GetAsync($"{Feed}?before=1", Ct))).GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task An_undone_command_stays_in_the_feed_marked_and_the_undo_follows_it()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var roll = Guid.NewGuid();
        await PostOkAsync(vasya, Url("roll"), new { commandId = roll });
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/undo", new { commandId = Guid.NewGuid(), targetCommandId = roll, comment = "Ошибся кнопкой" });

        var entries = (await OkAsync(await vasya.GetAsync(Feed, Ct))).GetProperty("entries").EnumerateArray().ToList();

        Assert.Equal("command-undone", entries[0].GetProperty("type").GetString());
        Assert.Equal("admin", entries[0].GetProperty("author").GetString());
        var rolled = entries.Single(e => e.GetProperty("type").GetString() == "game-rolled");
        Assert.True(rolled.GetProperty("undone").GetBoolean());
        Assert.Equal(roll, rolled.GetProperty("commandId").GetGuid());
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?before=0")]
    [InlineData("?limit=abc")]
    public async Task A_bad_page_is_invalid(string query)
    {
        var reader = await _site.SignedInAsync("vasya");

        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(Feed + query, Ct)).StatusCode);
    }

    [Fact]
    public async Task Unknown_things_are_not_found_and_anonymous_reads_nothing()
    {
        var reader = await _site.SignedInAsync("vasya");
        var anonymous = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/seasons/{Guid.NewGuid()}/feed", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/users/{Guid.NewGuid()}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/pool/{Guid.NewGuid()}/runs", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Feed, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/users/{_site.Users["vasya"]}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/pool/{Guid.NewGuid()}/runs", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_profile_shows_the_seasons_the_reviews_and_the_completed_games()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var (runId, gameId) = await CompletedWithReviewAsync(vasya, 8, "Отличная игра");

        var profile = await OkAsync(await (await _site.SignedInAsync("zritel")).GetAsync($"/api/users/{_site.Users["vasya"]}", Ct));

        Assert.Equal("vasya", profile.GetProperty("name").GetString());
        Assert.Equal(1, profile.GetProperty("completed").GetInt32());
        var season = Assert.Single(profile.GetProperty("seasons").EnumerateArray());
        Assert.Equal((SiteFactory.SeasonId, "Тестовый сезон", "active", _site.Players["vasya"]), (season.GetProperty("seasonId").GetGuid(), season.GetProperty("seasonName").GetString(), season.GetProperty("status").GetString(), season.GetProperty("playerId").GetGuid()));
        Assert.Equal(JsonValueKind.Null, season.GetProperty("place").ValueKind);
        var review = Assert.Single(profile.GetProperty("reviews").EnumerateArray());
        Assert.Equal((runId, gameId, 8, "Отличная игра"), (review.GetProperty("runId").GetGuid(), review.GetProperty("gameId").GetGuid(), review.GetProperty("rating").GetInt32(), review.GetProperty("text").GetString()));
        Assert.False(string.IsNullOrEmpty(review.GetProperty("gameTitle").GetString()));
    }

    [Fact]
    public async Task A_spectator_has_a_profile_without_seasons_and_a_deleted_account_has_none()
    {
        var reader = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");

        var spectator = await OkAsync(await reader.GetAsync($"/api/users/{_site.Users["zritel"]}", Ct));
        await PostOkAsync(admin, $"/api/admin/accounts/{_site.Users["masha"]}/delete", new { commandId = Guid.NewGuid() });

        Assert.Empty(spectator.GetProperty("seasons").EnumerateArray());
        Assert.Equal(0, spectator.GetProperty("completed").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/users/{_site.Users["masha"]}", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_game_page_lists_its_runs_with_their_reviews()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var (runId, gameId) = await CompletedWithReviewAsync(vasya, 6, null);

        var runs = await OkAsync(await (await _site.SignedInAsync("petya")).GetAsync($"/api/pool/{gameId}/runs", Ct));

        var run = Assert.Single(runs.EnumerateArray());
        Assert.Equal((runId, "vasya", "completed", "normal", 6), (run.GetProperty("runId").GetGuid(), run.GetProperty("playerName").GetString(), run.GetProperty("status").GetString(), run.GetProperty("difficulty").GetString(), run.GetProperty("rating").GetInt32()));
        Assert.Equal(_site.Users["vasya"], run.GetProperty("userId").GetGuid());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("reviewText").ValueKind);
    }

    [Fact]
    public async Task The_runs_of_a_deleted_game_are_the_admins_to_see()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var (_, gameId) = await CompletedWithReviewAsync(vasya, 5, null);
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(admin, $"/api/admin/pool/{gameId}/delete", new { commandId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, (await vasya.GetAsync($"/api/pool/{gameId}/runs", Ct)).StatusCode);
        Assert.Single((await OkAsync(await admin.GetAsync($"/api/pool/{gameId}/runs", Ct))).EnumerateArray());
    }

    // ---- Helpers ----

    private static async Task<(Guid RunId, Guid GameId)> CompletedWithReviewAsync(HttpClient player, int rating, string? text)
    {
        await PostOkAsync(player, Url("roll"));
        await PostOkAsync(player, Url("start"));
        await PostOkAsync(player, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        var last = (await OkAsync(await player.GetAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct))).GetProperty("me").GetProperty("lastCompleted");
        var runId = last.GetProperty("id").GetGuid();
        await PostOkAsync(player, Url($"runs/{runId}/review"), new { commandId = Guid.NewGuid(), rating, text });
        return (runId, last.GetProperty("game").GetProperty("id").GetGuid());
    }

    private static async Task PostOkAsync(HttpClient client, string url, object? body = null)
    {
        var response = await client.PostAsJsonAsync(url, body ?? new { commandId = Guid.NewGuid() }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}

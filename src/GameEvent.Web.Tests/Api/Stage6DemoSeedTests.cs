using System.Net.Http.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Web.Hosting;
using GameEvent.Web.Seasons;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Api;

[Trait("Category", "Slow")]
public sealed class Stage6DemoSeedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_demo_exposes_social_examples_and_archived_results_through_the_site()
    {
        await using var factory = new SiteFactory();
        await using var site = factory.WithWebHostBuilder(builder => builder.UseSetting("DevSeed:Password", SiteFactory.Password));
        var root = RepositoryRoot();
        Assert.True(await DemoSeed.RunAsync(site.Services, root, Ct));
        using var player = await SignInAsync(site, "player01");
        using var spectator = await SignInAsync(site, "zritel");
        var seasonUrl = $"/api/seasons/{DemoSeed.SeasonId}";

        var current = await player.GetFromJsonAsync<CurrentSeasonView>("/api/seasons/current", Ct);
        Assert.Equal(DemoSeed.SeasonId, current!.Id);
        var feed = await spectator.GetFromJsonAsync<FeedView>($"{seasonUrl}/feed", EngineJson.Options, Ct);
        Assert.NotNull(feed?.Reactions);
        var reactedEntry = Assert.Single(feed.Reactions, entry => entry.Value.Any(reaction => reaction.Count > 0));
        Assert.Contains(feed.Entries, entry => entry.CommandId == reactedEntry.Key && !entry.Undone);
        Assert.Contains(reactedEntry.Key, feed.CommentTargets!);
        var feedThread = await spectator.GetFromJsonAsync<CommentThreadView>($"{seasonUrl}/feed/{reactedEntry.Key}/comments", Ct);
        Assert.Contains(feedThread!.Comments, comment => !string.IsNullOrWhiteSpace(comment.Text));

        await using var db = factory.NewDb();
        var reactedReview = await (
            from review in db.Reviews
            where review.SeasonId == DemoSeed.SeasonId && db.ReviewReactions.Any(reaction => reaction.RunId == review.RunId)
            select review).SingleAsync(Ct);
        var gameUrl = $"/api/pool/{reactedReview.GameId}/runs";
        var runs = await spectator.GetFromJsonAsync<GameRunView[]>(gameUrl, EngineJson.Options, Ct);
        var reviewedRun = Assert.Single(runs!, run => run.RunId == reactedReview.RunId);
        Assert.Contains(reviewedRun.ReviewReactions!, reaction => reaction.Count > 0);
        var reviewThread = await spectator.GetFromJsonAsync<CommentThreadView>($"{gameUrl}/{reactedReview.RunId}/review-comments", Ct);
        Assert.Contains(reviewThread!.Comments, comment => !string.IsNullOrWhiteSpace(comment.Text));

        var gallery = await spectator.GetFromJsonAsync<GalleryView>($"{seasonUrl}/gallery", Ct);
        var moment = Assert.Single(gallery!.Items);
        Assert.Contains(moment.Reactions!, reaction => reaction.Count > 0);
        await AssertPrivateWebpAsync(spectator, moment.ImageUrl);
        await AssertPrivateWebpAsync(spectator, moment.ThumbnailUrl);

        var stats = await player.GetFromJsonAsync<SeasonStatisticsView>($"{seasonUrl}/my-statistics", Ct);
        Assert.True(stats!.CompletedRuns > 0);
        var archive = await db.Seasons.SingleAsync(season => season.Status == SeasonStatus.Archived, Ct);
        var fame = await spectator.GetFromJsonAsync<HallOfFameSeasonView[]>("/api/hall-of-fame", Ct);
        var winner = Assert.Single(Assert.Single(fame!, season => season.SeasonId == archive.Id).Winners);
        Assert.True(winner.Points > 0);
        Assert.NotNull(winner.ProfileUserId);
        var records = await spectator.GetFromJsonAsync<HallOfFameRecordsView>("/api/hall-of-fame/records", Ct);
        var longest = Assert.Single(records!.LongestGames, game => game.SeasonId == archive.Id);
        Assert.True(longest.Hours > 0);

        foreach (var id in new[] { DemoSeed.SeasonId, archive.Id })
        {
            var integrity = await SeasonIntegrity.CheckAsync(db, id, Ct);
            Assert.NotNull(integrity);
            Assert.Empty(integrity.Differences);
        }

        var events = await db.Events.CountAsync(Ct);
        Assert.True(await DemoSeed.RunAsync(site.Services, root, Ct));
        Assert.Equal(events, await db.Events.CountAsync(Ct));
    }

    private static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> site, string login)
    {
        var client = site.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(client);
        await ApiCalls.PostOkAsync(client, "/api/auth/login", new { login, password = SiteFactory.Password });
        return client;
    }

    private static async Task AssertPrivateWebpAsync(HttpClient viewer, string url)
    {
        using var response = await viewer.GetAsync(url, Ct);
        response.EnsureSuccessStatusCode();
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl is { Private: true, NoStore: true });
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync(Ct));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "content", "pool.demo.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root with content/ is not found.");
    }
}

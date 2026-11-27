using System.Net.Http.Json;
using GameEvent.Engine.Achievements;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
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

        var proof = await (
            from entry in db.Proofs
            join owner in db.SeasonPlayers on entry.PlayerId equals owner.Id
            join account in db.Users on owner.UserId equals account.Id
            where entry.SeasonId == DemoSeed.SeasonId && entry.Note == "Кадр для публикации из одобренного пруфа"
            select new { entry.RunId, account.Login }).SingleOrDefaultAsync(Ct);
        Assert.NotNull(proof);
        using var proofOwner = await SignInAsync(site, proof.Login);
        using var choices = await proofOwner.GetAsync($"{seasonUrl}/gallery/proofs", Ct);
        choices.EnsureSuccessStatusCode();
        Assert.True(choices.Headers.CacheControl is { Private: true, NoStore: true });
        var photos = await choices.Content.ReadFromJsonAsync<GalleryProofPhotoView[]>(Ct);
        var photo = Assert.Single(photos!, item => item.RunId == proof.RunId);
        await AssertPrivateWebpAsync(proofOwner, photo.ThumbnailUrl, noStore: false);

        var stats = await player.GetFromJsonAsync<SeasonStatisticsView>($"{seasonUrl}/my-statistics", Ct);
        Assert.True(stats!.CompletedRuns > 0);
        var archive = await db.Seasons.SingleAsync(season => season.Status == SeasonStatus.Archived, Ct);
        var fame = await spectator.GetFromJsonAsync<HallOfFameSeasonView[]>("/api/hall-of-fame", Ct);
        var winner = Assert.Single(Assert.Single(fame!, season => season.SeasonId == archive.Id).Winners);
        Assert.Equal("Лиса", winner.Name);
        Assert.True(winner.Points > 0);
        Assert.NotNull(winner.ProfileUserId);
        var profile = await spectator.GetFromJsonAsync<ProfileView>($"/api/users/{winner.ProfileUserId}", EngineJson.Options, Ct);
        var badge = Assert.Single(profile!.Achievements, achievement => achievement.ObjectId == "unbreakable");
        Assert.Equal((archive.Id, "unbreakable", "Непробиваемый", "Переживи пять атак за сезон."),
            (badge.SeasonId, badge.ObjectId, badge.Name, badge.Description));
        Assert.Contains(profile.Achievements, achievement => achievement is
        { ObjectId: "jackpot", Scope: AchievementScope.AllTime });
        Assert.Contains(profile.Achievements, achievement => achievement is { ObjectId: "long-run" });
        var attacks = await db.Events.Where(row => row.SeasonId == archive.Id && row.Type == "hostile-received")
            .OrderBy(row => row.Sequence).Select(row => row.CommandId).ToListAsync(Ct);
        Assert.Equal(5, attacks.Count);
        var seasonalAwards = await db.Events.Where(row => row.SeasonId == archive.Id && row.Type == "achievement-earned").ToListAsync(Ct);
        var unbreakable = Assert.Single(seasonalAwards, row => Decode(row.Type, row.Version, row.Data) is
            AchievementEarned { Achievement.ObjectId: "unbreakable" });
        Assert.Equal(attacks[^1], unbreakable.CommandId);
        var longRun = Assert.Single(seasonalAwards, row => Decode(row.Type, row.Version, row.Data) is
            AchievementEarned { Achievement.ObjectId: "long-run" });
        Assert.True(await db.Events.AnyAsync(row => row.CommandId == longRun.CommandId && row.Type == "proof-approved", Ct));

        var jackpotCommand = await db.Events.Where(row => row.SeasonId == archive.Id && row.Type == "lifetime-achievement-earned")
            .Select(row => row.CommandId).SingleAsync(Ct);
        var jackpotEvents = (await db.Events.Where(row => row.CommandId == jackpotCommand).ToListAsync(Ct))
            .Select(row => Decode(row.Type, row.Version, row.Data)).ToList();
        Assert.Contains(jackpotEvents, entry => entry is CoinsChanged { Delta: 10, Reason: CoinsReason.Achievement });
        var jackpotThrow = Assert.Single(jackpotEvents.OfType<CompletionRolled>());
        Assert.True(jackpotThrow.Dice.Count >= 3);
        Assert.All(jackpotThrow.Dice, die => Assert.Equal(die.Sides, die.Value));

        var records = await spectator.GetFromJsonAsync<HallOfFameRecordsView>("/api/hall-of-fame/records", Ct);
        var longest = Assert.Single(records!.LongestGames, game => game.SeasonId == archive.Id);
        Assert.Equal(jackpotThrow.RunId, longest.RunId);
        Assert.True(longest.Hours > 30);

        var result = await spectator.GetFromJsonAsync<NominationResultView>($"/api/seasons/{archive.Id}/nominations", EngineJson.Options, Ct);
        Assert.Contains(result!.Automatic, nomination => nomination.Nomination == AutoNomination.MostHours
            && nomination.Winners.Any(player => player.Name == "Лиса") && nomination.Value >= longest.Hours);
        var voted = Assert.Single(result.Voted, nomination => nomination.Nomination == VotedNomination.BestReview);
        Assert.True(voted.HasWinner);
        Assert.Equal(longest.RunId, voted.Winner?.Id);

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

    private static IGameEvent Decode(string type, int version, string data) => EventCodec.Decode(new StoredEvent(type, version, data));

    private static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> site, string login)
    {
        var client = site.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(client);
        await ApiCalls.PostOkAsync(client, "/api/auth/login", new { login, password = SiteFactory.Password });
        return client;
    }

    private static async Task AssertPrivateWebpAsync(HttpClient viewer, string url, bool noStore = true)
    {
        using var response = await viewer.GetAsync(url, Ct);
        response.EnsureSuccessStatusCode();
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl is { Private: true } cache && cache.NoStore == noStore);
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

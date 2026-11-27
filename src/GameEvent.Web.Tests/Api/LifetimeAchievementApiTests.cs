using System.Net;
using System.Text.Json;
using GameEvent.Engine.Achievements;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Undo;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Api.ApiCalls;
using ArchiveTransfer = GameEvent.Infrastructure.Seasons.SeasonTransfer;

namespace GameEvent.Web.Tests.Api;

public sealed class LifetimeAchievementApiTests
{
    private const string Login = "vasya";
    private const string Jackpot = "jackpot";
    private const int Reward = 10;
    private const string Definition = """
        {
          "id": "jackpot", "kind": "achievement", "name": "Джекпот",
          "description": "Выбрось максимум на всех кубиках, когда их хотя бы три.", "scope": "allTime",
          "effect": { "trigger": "afterDice", "condition": { "stat": "allDiceMax", "minDice": 3 },
            "actions": [{ "type": "changeResource", "resource": "coins", "amount": 10 }] }
        }
        """;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_same_account_earns_the_badge_and_coins_in_only_one_season_until_it_is_undone()
    {
        await using var site = MaximizingSite();
        await site.SeedAsync(withSeason: false);
        var first = await PrepareRunAsync(site);
        var second = await PrepareRunAsync(site);
        var userId = site.Users[Login];
        using var spectator = await site.SignedInAsync("zritel");
        using var anonymous = await site.AnonymousAsync();
        using var denied = await anonymous.GetAsync($"/api/users/{userId}", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var firstThrow = Guid.NewGuid();
        var secondThrow = Guid.NewGuid();
        AssertAward(await CompleteAsync(site, first, firstThrow));
        var retry = await CompleteAsync(site, first, firstThrow);
        Assert.True(retry.IsDuplicate);
        AssertBadge(Assert.Single(await BadgesAsync(spectator, userId)), first.SeasonId, site.Clock.UtcNow);
        var secondCompletion = await CompleteAsync(site, second, secondThrow);
        Assert.Empty(Events<LifetimeAchievementEarned>(secondCompletion));
        Assert.DoesNotContain(Events<CoinsChanged>(secondCompletion), reward => reward.Reason == CoinsReason.Achievement);
        AssertBadge(Assert.Single(await BadgesAsync(spectator, userId)), first.SeasonId, site.Clock.UtcNow);

        await SendAsync(site, first.SeasonId, new UndoCommand(firstThrow, "Undo awarding completion"));
        Assert.Equal(0, await CoinsAsync(site, first));
        Assert.Empty(await BadgesAsync(spectator, userId));

        await SendAsync(site, second.SeasonId, new UndoCommand(secondThrow, "Restart the second throw"));
        site.Clock.Advance(TimeSpan.FromMinutes(1));
        AssertAward(await CompleteAsync(site, second, Guid.NewGuid()));
        AssertBadge(Assert.Single(await BadgesAsync(spectator, userId)), second.SeasonId, site.Clock.UtcNow);
        await using var db = site.NewDb();
        var row = await db.AccountAchievements.SingleAsync(Ct);
        Assert.Equal((userId, Jackpot, second.SeasonId, second.PlayerId), (row.UserId, row.ObjectId, row.SeasonId, row.PlayerId));
        await AssertIntegrityAsync(site, first.SeasonId);
        await AssertIntegrityAsync(site, second.SeasonId);
    }

    [Fact]
    public async Task Zip_import_rejects_a_second_active_award_for_a_matched_account_and_accepts_revoked_history()
    {
        await using var origin = MaximizingSite();
        await using var destination = MaximizingSite();
        await origin.SeedAsync(withSeason: false);
        await destination.SeedAsync(withSeason: false);
        var exported = await PrepareRunAsync(origin);
        var local = await PrepareRunAsync(destination);
        var originThrow = Guid.NewGuid();
        AssertAward(await CompleteAsync(origin, exported, originThrow));
        AssertAward(await CompleteAsync(destination, local, Guid.NewGuid()));
        Assert.NotEqual(origin.Users[Login], destination.Users[Login]);

        await using var destinationDb = destination.NewDb();
        var archive = await ExportZipAsync(origin, exported.SeasonId);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ArchiveTransfer.ImportAsync(destinationDb, archive, destination.Clock.UtcNow, ct: Ct));
        Assert.Contains("already has an active lifetime achievement", failure.Message, StringComparison.Ordinal);
        Assert.Contains(Jackpot, failure.Message, StringComparison.Ordinal);

        // A new context sees only the committed local season and award after the failed transfer.
        await using var freshDb = destination.NewDb();
        Assert.Equal(local.SeasonId, (await freshDb.Seasons.SingleAsync(Ct)).Id);
        Assert.Equal(local.SeasonId, (await freshDb.AccountAchievements.SingleAsync(Ct)).SeasonId);
        Assert.False(await freshDb.Events.AnyAsync(entry => entry.SeasonId == exported.SeasonId, Ct));
        Assert.False(await freshDb.Games.AnyAsync(game => game.IsDeleted, Ct));

        await SendAsync(origin, exported.SeasonId, new UndoCommand(originThrow, "Remove the exported award"));
        await ImportZipAsync(origin, destination, exported.SeasonId);
        Assert.Equal(local.SeasonId, (await freshDb.AccountAchievements.AsNoTracking().SingleAsync(Ct)).SeasonId);
    }

    [Fact]
    public async Task Zip_import_preserves_an_active_award_for_the_matched_account_and_its_undo_removes_it()
    {
        await using var origin = MaximizingSite();
        await using var destination = MaximizingSite();
        await origin.SeedAsync(withSeason: false);
        await destination.SeedAsync(withSeason: false);
        var exported = await PrepareRunAsync(origin);
        var throwId = Guid.NewGuid();
        AssertAward(await CompleteAsync(origin, exported, throwId));
        var awardedCoins = await CoinsAsync(origin, exported);

        await ImportZipAsync(origin, destination, exported.SeasonId);

        using var spectator = await destination.SignedInAsync("zritel");
        var localAccount = destination.Users[Login];
        AssertBadge(Assert.Single(await BadgesAsync(spectator, localAccount)), exported.SeasonId, origin.Clock.UtcNow);
        Assert.Equal(awardedCoins, await CoinsAsync(destination, exported));
        await SendAsync(destination, exported.SeasonId, new UndoCommand(throwId, "Remove the imported award"));
        Assert.Empty(await BadgesAsync(spectator, localAccount));
        Assert.Equal(0, await CoinsAsync(destination, exported));
        await AssertIntegrityAsync(destination, exported.SeasonId);
    }

    private static SiteFactory MaximizingSite() => new() { Random = new MaximumRandom() };

    private static async Task<PlayingSeason> PrepareRunAsync(SiteFactory site)
    {
        var seasonId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var rules = RulesetJson.Default();
        await SendAsync(site, seasonId, new CreateSeason(seasonId, "Jackpot", rules with
        {
            Features = rules.Features with { Achievements = true },
            Reward = rules.Reward with { DiceCount = rules.Reward.DiceCount with { Min = 3, Max = 3 } },
        }));
        await SendAsync(site, seasonId, new ChangeSeasonStatus(SeasonStatus.Active));
        await SendAsync(site, seasonId, new AddSeasonPlayer(playerId, site.Users[Login], Login));
        var pack = new ContentPack { Objects = [ContentJson.Parse<ObjectDefinition>(Definition)] };
        await SendAsync(site, seasonId, new PublishContent(pack, "Enable the lifetime achievement"));

        using var player = await site.SignedInAsync(Login);
        await PostOkAsync(player, $"/api/seasons/{seasonId}/roll");
        await PostOkAsync(player, $"/api/seasons/{seasonId}/start");
        return new PlayingSeason(seasonId, playerId);
    }

    private static Task<CommandOutcome> CompleteAsync(SiteFactory site, PlayingSeason season, Guid commandId) =>
        SendAsync(site, season.SeasonId, new CompleteRun(season.PlayerId, Difficulty.Normal), commandId, site.Users[Login]);

    private static async Task<CommandOutcome> SendAsync(
        SiteFactory site, Guid seasonId, ICommand command, Guid? commandId = null, Guid? authorId = null)
    {
        var bus = site.Services.GetRequiredService<CommandBus>();
        var outcome = await bus.SendAsync(new CommandEnvelope(commandId ?? Guid.NewGuid(), seasonId, command, authorId), Ct);
        Assert.True(outcome.IsAccepted, $"{command.GetType().Name}: {outcome.Rejection}");
        return outcome;
    }

    private static IEnumerable<T> Events<T>(CommandOutcome outcome) => outcome.Events.Select(entry => entry.Event).OfType<T>();

    private static void AssertAward(CommandOutcome outcome)
    {
        var award = Assert.Single(Events<LifetimeAchievementEarned>(outcome));
        Assert.Equal(Jackpot, award.Achievement.ObjectId);
        var reward = Assert.Single(Events<CoinsChanged>(outcome), reward => reward.Reason == CoinsReason.Achievement);
        Assert.Equal((award.PlayerId, Reward), (reward.PlayerId, reward.Delta));
    }

    private static async Task<JsonElement.ArrayEnumerator> BadgesAsync(HttpClient spectator, Guid userId) =>
        (await OkAsync(await spectator.GetAsync($"/api/users/{userId}", Ct))).GetProperty("achievements").EnumerateArray();

    private static void AssertBadge(JsonElement badge, Guid seasonId, DateTimeOffset earnedAt) =>
        Assert.Equal((seasonId, Jackpot, "Джекпот", "allTime", earnedAt),
            (badge.GetProperty("seasonId").GetGuid(), badge.GetProperty("objectId").GetString(), badge.GetProperty("name").GetString(),
                badge.GetProperty("scope").GetString(), badge.GetProperty("earnedAt").GetDateTimeOffset()));

    private static async Task<int> CoinsAsync(SiteFactory site, PlayingSeason season)
    {
        await using var db = site.NewDb();
        return await db.SeasonPlayers.Where(player => player.Id == season.PlayerId).Select(player => player.Coins).SingleAsync(Ct);
    }

    private static async Task<SeasonArchive> ExportZipAsync(SiteFactory site, Guid seasonId)
    {
        await using var db = site.NewDb();
        var archive = await ArchiveTransfer.ExportAsync(db, seasonId, site.Clock.UtcNow, Ct);
        Assert.NotNull(archive);
        using var zip = new MemoryStream();
        await ArchiveTransfer.WriteZipAsync(archive, zip, Ct);
        zip.Position = 0;
        return await ArchiveTransfer.ReadZipAsync(zip, Ct);
    }

    private static async Task ImportZipAsync(SiteFactory origin, SiteFactory destination, Guid seasonId)
    {
        var archive = await ExportZipAsync(origin, seasonId);
        await using var db = destination.NewDb();
        var imported = await ArchiveTransfer.ImportAsync(db, archive, destination.Clock.UtcNow, ct: Ct);
        Assert.Contains(imported.MatchedUsers, user => user.Login == Login);
        Assert.True(imported.Integrity.IsIntact, string.Join(", ", imported.Integrity.Differences));
    }

    private static async Task AssertIntegrityAsync(SiteFactory site, Guid seasonId)
    {
        await using var db = site.NewDb();
        var integrity = await SeasonIntegrity.CheckAsync(db, seasonId, Ct);
        Assert.NotNull(integrity);
        Assert.True(integrity.IsIntact, string.Join(", ", integrity.Differences));
    }

    private sealed record PlayingSeason(Guid SeasonId, Guid PlayerId);

    private sealed class MaximumRandom : IRandomSource
    {
        public int NextInt(int minInclusive, int maxExclusive) => maxExclusive - 1;
    }
}

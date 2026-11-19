using System.Text.Json;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Infrastructure.Database;
using GameEvent.Web.Hosting;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Api;

/// <summary>The canonical demo has both playable examples and history for the stage 5 screens (5.25).</summary>
[Trait("Category", "Slow")]
public sealed class Stage5DemoSeedTests(Stage5DemoSeedTests.DemoFixture demo) : IClassFixture<Stage5DemoSeedTests.DemoFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_demo_has_waiting_event_effects_and_an_irl_proof_with_review_history()
    {
        await using var db = demo.NewDb();
        var wallets = (await db.SeasonPlayers
            .Where(player => player.SeasonId == DemoSeed.SeasonId && player.EconomyJson != null)
            .Select(player => player.EconomyJson!)
            .ToListAsync(Ct)).Select(Read<PlayerEconomy>).ToList();
        Assert.Contains(wallets, wallet => wallet.NextRoll.Any(modifier => modifier.EventKind is not null && modifier.RunCondition is not null));
        Assert.Contains(wallets, wallet => wallet.NextDice.Any(modifier => modifier.EventKind is not null));

        var proofs = await db.ManualEffects
            .Where(effect => effect.SeasonId == DemoSeed.SeasonId && effect.IrlProofJson != null)
            .ToListAsync(Ct);
        Assert.Contains(proofs, effect =>
            effect.ObjectId is not null
            && Read<IrlProofState>(effect.IrlProofJson!) is { Status: IrlProofStatus.Pending } proof
            && (proof.Files.Count > 0 || (Uri.TryCreate(proof.Link, UriKind.Absolute, out var link) && link.Scheme == Uri.UriSchemeHttps)));
        var reviews = await EventsAsync<IrlProofReviewed>(db, "irl-proof-reviewed");
        Assert.Contains(reviews, review => review.Approved);
        Assert.Contains(reviews, review => !review.Approved && !string.IsNullOrWhiteSpace(review.Comment));
        Assert.True(demo.Features.Events);
    }

    [Fact]
    public async Task The_demo_has_a_playable_poll_and_a_closed_poll_with_votes_and_a_result()
    {
        await using var db = demo.NewDb();
        var polls = await db.Polls.Where(poll => poll.SeasonId == DemoSeed.SeasonId).ToListAsync(Ct);
        var pollIds = polls.Select(poll => poll.Id).ToList();
        var options = (await db.PollOptions.Where(option => pollIds.Contains(option.PollId)).ToListAsync(Ct))
            .ToLookup(option => option.PollId, option => option.OptionId);
        var votes = (await db.Votes.Where(vote => pollIds.Contains(vote.PollId)).ToListAsync(Ct))
            .ToLookup(vote => vote.PollId);

        bool HasVotes(Guid pollId) =>
            options[pollId].Count() >= 2
            && votes[pollId].Any()
            && votes[pollId].All(vote => options[pollId].Contains(vote.OptionId));

        Assert.Contains(polls, poll => poll.Status == PollStatus.Open && poll.ClosesAt > demo.Now && HasVotes(poll.Id));
        Assert.Contains(polls, poll => poll.Status == PollStatus.Closed && HasVotes(poll.Id)
            && Read<Poll>(poll.DataJson).Result is { } winner && options[poll.Id].Contains(winner));
        Assert.True(demo.Features.Polls);
    }

    [Fact]
    public async Task The_demo_has_an_active_coop_run_and_a_completed_coop_with_both_shares()
    {
        await using var db = demo.NewDb();
        var runs = await db.Runs.Where(run => run.SeasonId == DemoSeed.SeasonId && run.PartnerId != null).ToListAsync(Ct);
        var players = await db.SeasonPlayers.Where(player => player.SeasonId == DemoSeed.SeasonId).ToDictionaryAsync(player => player.Id, Ct);
        Assert.Contains(runs, run => run.Status == RunStatus.Playing
            && run.PartnerId is { } partnerId && partnerId != run.PlayerId
            && players[run.PlayerId].ActiveRunId == run.Id && players[partnerId].ActiveRunId == run.Id);

        var splits = await EventsAsync<CoopRewardSplit>(db, "coop-reward-split");
        Assert.Contains(runs, run => run.Status == RunStatus.Completed && splits.Any(split =>
            split.RunId == run.Id && split.PlayerId == run.PlayerId && split.PartnerId == run.PartnerId
            && split.RollerPoints > 0 && split.PartnerPoints > 0
            && split.RollerPoints + split.PartnerPoints == split.Total
            && split.RollerCoins + split.PartnerCoins == split.Coins));
        Assert.True(demo.Features.Coop);
    }

    [Fact]
    public async Task The_demo_has_an_active_global_goal_and_an_ended_goal_reached_by_play()
    {
        await using var db = demo.NewDb();
        var events = await db.GlobalEvents.Where(global => global.SeasonId == DemoSeed.SeasonId).ToListAsync(Ct);
        Assert.Contains(events, global => global.Status == GlobalEventStatus.Active && global.EndsAt > demo.Now
            && Read<GlobalEventState>(global.DataJson).Goal is { Target: > 0 });

        var reached = await EventsAsync<GlobalGoalReached>(db, "global-goal-reached");
        Assert.Contains(events, global => global.Status == GlobalEventStatus.Ended && global.Reached
            && Read<GlobalEventState>(global.DataJson) is { Goal: { } goal } state
            && state.Contributions.Sum(contribution => contribution.Amount) >= goal.Target
            && reached.Any(e => e.GlobalEventId == global.Id
                && state.Contributions.Any(contribution => contribution.RunId == e.RunId)));
        Assert.True(demo.Features.GlobalEvents);
    }

    private static async Task<List<T>> EventsAsync<T>(GameEventDbContext db, string type) where T : class =>
        (await db.Events.Where(row => row.SeasonId == DemoSeed.SeasonId && row.Type == type && row.UndoneByEventId == null)
            .Select(row => row.Data).ToListAsync(Ct)).Select(Read<T>).ToList();

    private static T Read<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, EngineJson.Options)
        ?? throw new InvalidOperationException($"The projected {typeof(T).Name} is missing.");

    public sealed class DemoFixture : IAsyncLifetime
    {
        private readonly SiteFactory _site = new();

        internal DateTimeOffset Now => _site.Clock.UtcNow;

        internal Features Features { get; private set; } = null!;

        internal GameEventDbContext NewDb() => _site.NewDb();

        public async ValueTask InitializeAsync()
        {
            using var site = _site.WithWebHostBuilder(builder => builder.UseSetting("DevSeed:Password", "demo-password-1"));
            Assert.True(await DemoSeed.RunAsync(site.Services, RepositoryRoot(), Ct));
            await using var db = NewDb();
            var rules = await db.Seasons.Where(season => season.Id == DemoSeed.SeasonId)
                .Select(season => season.RulesetJson).SingleAsync(Ct);
            Features = Read<Ruleset>(rules).Features;
        }

        public async ValueTask DisposeAsync() => await _site.DisposeAsync();

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
}

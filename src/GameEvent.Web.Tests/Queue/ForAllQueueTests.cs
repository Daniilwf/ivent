using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GameEvent.Web.Tests.Queue;

/// <summary>
/// Settlements «на всех» through the queue (D-537, EV53; invariants 2, 3, 8): the cause only queues the settlement, the
/// processor then runs <see cref="SettleForAll"/> as system commands of their own — each its own transaction with its own
/// id, no author, within the season's events per command — until nothing is pending. A settlement left pending by a
/// stop is settled before the next command of the season.
/// A test trigger handler stands in for a goal of 5.9 (D-24): the admin's adjustment with the goal's comment reaches it.
/// </summary>
public sealed class ForAllQueueTests
{
    private const string Goal = "Цель достигнута";
    private const int Players = 17;
    private const int Limit = 20;

    private static readonly Guid s_season = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid s_goal = Guid.Parse("6f000000-0000-0000-0000-000000000001");

    private static readonly EffectSpec s_reward = new()
    {
        Target = new TargetSpec { Selector = TargetSelector.All },
        Actions =
        [
            new ChangeResourceAction { Resource = "coins", Amount = ContentValue.Of(2) },
            new ChangeResourceAction { Resource = "points", Amount = ContentValue.Of(1) },
        ],
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid Player(int i) => Guid.Parse($"10000000-0000-0000-0000-{i:x12}");

    private sealed class GoalReward : ITriggerHandler
    {
        public IEnumerable<IGameEvent> React(SeasonState state, IGameEvent trigger, EngineContext context) =>
            trigger is PlayerAdjusted { Comment: Goal }
                ? [ForAllSettlements.Queue(state, context, ForAllSource.GoalReward, s_goal, "goal-reward", s_reward)]
                : [];
    }

    /// <summary>A season of 17 players whose commands write at most <see cref="Limit"/> events: 2 a player, so the reward needs batches.</summary>
    private static async Task SeasonAsync(QueueHarness h)
    {
        var rules = RulesetJson.Default();
        rules = rules with { Effects = rules.Effects with { MaxEventsPerCommand = Limit } };
        await AcceptedAsync(h, new CreateSeason(s_season, "Тестовый сезон", rules));
        await AcceptedAsync(h, new ChangeSeasonStatus(SeasonStatus.Active));
        for (var i = 1; i <= Players; i++)
        {
            await AcceptedAsync(h, new AddSeasonPlayer(Player(i), Player(i), $"Игрок {i}"));
        }
    }

    [Fact]
    public async Task A_reward_for_all_17_is_settled_in_system_commands_of_its_own()
    {
        await using var h = await QueueHarness.StartWithTriggersAsync([new GoalReward()]);
        await SeasonAsync(h);
        var coinsBefore = await CoinsAsync(h);
        var cause = Guid.NewGuid();

        var outcome = await h.SendAsync(new AdjustPlayer(Player(1), Goal, PointsDelta: 1), s_season, cause);

        Assert.True(outcome.IsAccepted, outcome.Rejection?.ToString());
        var state = await SettledAsync(h);
        Assert.Empty(state.ForAll);

        await using var db = h.NewDb();
        var events = await db.Events.AsNoTracking().Where(e => e.SeasonId == s_season).OrderBy(e => e.Sequence).ToListAsync(Ct);

        // The cause wrote its own events and the queued settlement, none of the reward
        var causeEvents = events.Where(e => e.CommandId == cause).ToList();
        Assert.Single(causeEvents, e => e.Type == "for-all-queued");
        Assert.DoesNotContain(causeEvents, e => e.Type == "for-all-settled");

        // Then separate system commands: their own ids, no author, after the cause, each within the season's limit
        var batches = events.Where(e => e.CommandType == nameof(SettleForAll)).GroupBy(e => e.CommandId).ToList();
        Assert.True(batches.Count > 1, $"{Players} players of 2 events fit one command of {Limit}.");
        Assert.DoesNotContain(batches, b => b.Key == cause);
        Assert.All(batches, b => Assert.InRange(b.Count(), 1, Limit));
        Assert.All(batches, b => Assert.All(b, e => Assert.Null(e.AuthorId)));
        Assert.All(batches, b => Assert.Equal("for-all-settled", b.First().Type));
        Assert.All(batches, b => Assert.DoesNotContain(b, e => e.Type == "effect-chain-cut"));
        Assert.True(batches.Min(b => b.Min(e => e.Sequence)) > causeEvents.Max(e => e.Sequence));

        // Every player got the coins once; the projection equals the log
        var coins = await CoinsAsync(h);
        for (var i = 1; i <= Players; i++)
        {
            Assert.Equal((i, coinsBefore[Player(i)] + 2), (i, coins[Player(i)]));
        }

        var report = await SeasonIntegrity.CheckAsync(db, s_season, Ct);
        Assert.True(report!.IsIntact, string.Join("; ", report.Differences));
    }

    [Fact]
    public async Task A_settlement_left_pending_by_a_stop_is_settled_before_the_next_command()
    {
        var block = new BlockingSettleInterceptor();
        await using var h = await QueueHarness.StartWithTriggersAsync([new GoalReward()], block);
        await SeasonAsync(h);
        var coinsBefore = await CoinsAsync(h);
        var cause = Guid.NewGuid();

        // The first batch hangs in its transaction; the processor stops with the settlement pending
        block.Armed = true;
        var sent = h.SendAsync(new AdjustPlayer(Player(1), Goal, PointsDelta: 1), s_season, cause);
        await block.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        block.Armed = false;
        await h.RestartAsync();
        try
        {
            await sent;
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
        {
            // The cause was answered or cancelled by the stop; it is committed either way (checked below)
        }

        await using (var db = h.NewDb())
        {
            Assert.True(await db.Events.AnyAsync(e => e.CommandId == cause && e.Type == "for-all-queued", Ct));
        }

        // The next command of the season: the settlement goes first
        var next = Guid.NewGuid();
        var outcome = await h.SendAsync(new AdjustPlayer(Player(2), "Другое", CoinsDelta: 1), s_season, next);
        Assert.True(outcome.IsAccepted, outcome.Rejection?.ToString());
        await SettledAsync(h);

        await using var check = h.NewDb();
        var events = await check.Events.AsNoTracking().Where(e => e.SeasonId == s_season).ToListAsync(Ct);
        var settled = events.Where(e => e.CommandType == nameof(SettleForAll)).ToList();
        Assert.NotEmpty(settled);
        Assert.True(
            settled.Min(e => e.Sequence) < events.Where(e => e.CommandId == next).Min(e => e.Sequence),
            "The pending settlement was not settled before the next command.");

        var coins = await CoinsAsync(h);
        for (var i = 1; i <= Players; i++)
        {
            Assert.Equal((i, coinsBefore[Player(i)] + 2 + (i == 2 ? 1 : 0)), (i, coins[Player(i)]));
        }

        var report = await SeasonIntegrity.CheckAsync(check, s_season, Ct);
        Assert.True(report!.IsIntact, string.Join("; ", report.Differences));
    }

    private static async Task AcceptedAsync(QueueHarness h, ICommand command)
    {
        var outcome = await h.SendAsync(command, s_season);
        Assert.True(outcome.IsAccepted, $"{command.GetType().Name} rejected: {outcome.Rejection}");
    }

    private static async Task<Dictionary<Guid, int>> CoinsAsync(QueueHarness h)
    {
        await using var db = h.NewDb();
        return await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == s_season).ToDictionaryAsync(p => p.Id, p => p.Coins, Ct);
    }

    /// <summary>Waits until the season has no pending settlement (the processor settles after the cause); the replayed state.</summary>
    private static async Task<SeasonState> SettledAsync(QueueHarness h)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            await using (var db = h.NewDb())
            {
                var (state, _) = await EventLogReader.ReplaySeasonAsync(db, s_season, Ct);
                if (state.ForAll.IsEmpty)
                {
                    return state;
                }
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException("The settlement is still pending after 15 seconds.");
    }

    /// <summary>While <see cref="Armed"/> is set, a <see cref="SettleForAll"/> hangs in SaveChanges until the processor is stopped.</summary>
    private sealed class BlockingSettleInterceptor : SaveChangesInterceptor
    {
        private volatile bool _armed;

        public bool Armed
        {
            get => _armed;
            set => _armed = value;
        }

        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context is { } context
                && context.ChangeTracker.Entries<GameEventRecord>().Any(e => e.State == EntityState.Added && e.Entity.CommandType == nameof(SettleForAll)))
            {
                Blocked.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}

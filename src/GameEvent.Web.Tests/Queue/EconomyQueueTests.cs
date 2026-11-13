using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Rulesets;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Web.Tests.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GameEvent.Web.Tests.Queue;

/// <summary>
/// EC9 (D-401, D-404): the economy through the queue — the player's economy and the earliest timer are projected into their
/// columns, the integrity check sees them equal to the log, and the scheduler fires the shop's timer through the queue once
/// its moment has come by the server's clock.
/// </summary>
public sealed class EconomyQueueTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    private static ObjectDefinition Item(string id, int price, EffectSpec effect) =>
        new()
        {
            Id = id,
            Kind = ObjectKind.Item,
            Name = id,
            Description = "Предмет для теста.",
            Rarity = Rarity.Common,
            Price = price,
            Window = UseWindow.Anytime,
            Stackable = true,
            Effect = effect,
        };

    private async Task EconomyOnAsync()
    {
        await using (var db = _site.NewDb())
        {
            var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
            await _site.SendAsync(new ChangeRuleset(state.Rules with { Features = state.Rules.Features with { Items = true, Shop = true, Bets = true } }));
        }

        var self = new TargetSpec { Selector = TargetSelector.Self };
        await _site.SendAsync(new PublishContent(
            new ContentPack
            {
                Objects =
                [
                    Item("orange", 10, new EffectSpec { Target = self, Actions = [new MoveAction { Steps = ContentValue.Of(1) }] }),
                    Item("piggy-bank", 12, new EffectSpec { Target = self, Actions = [new ChangeResourceAction { Resource = "coins", Amount = ContentValue.Of(3) }] }),
                ],
            },
            "Контент"));
    }

    [Fact]
    public async Task The_economy_is_projected_and_the_projection_equals_the_log()
    {
        await EconomyOnAsync();
        var vasya = _site.Players["vasya"];
        await _site.SendAsync(new AdjustInventory(vasya, "piggy-bank", null, "Подарок"));
        await _site.SendAsync(new AdjustInventory(vasya, "orange", null, "Подарок"));

        await using (var db = _site.NewDb())
        {
            var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
            var piggy = state.Players[vasya].Wallet.Inventory.First(o => o.ObjectId == "piggy-bank");
            await _site.SendAsync(new UseItem(vasya, piggy.InstanceId));
        }

        await _site.SendAsync(new Engine.Players.AdjustPlayer(vasya, "Монетки", CoinsDelta: 2));
        await _site.SendAsync(new RollShop(vasya));

        await using var check = _site.NewDb();
        var report = await SeasonIntegrity.CheckAsync(check, SiteFactory.SeasonId, Ct);
        Assert.True(report!.IsIntact, string.Join("; ", report.Differences));
        var row = await check.SeasonPlayers.AsNoTracking().SingleAsync(p => p.Id == vasya, Ct);
        Assert.NotNull(row.EconomyJson);
        Assert.Equal(_site.Clock.UtcNow.AddMinutes(15), row.NextTimerAt);
        Assert.Null((await check.SeasonPlayers.AsNoTracking().SingleAsync(p => p.Id == _site.Players["petya"], Ct)).EconomyJson);
    }

    [Fact]
    public async Task Switching_the_shop_off_and_on_moves_the_timer_column_with_it()
    {
        // D-420: a mechanic switched off is not due; the column follows the flags, not only changes of the player
        await EconomyOnAsync();
        var vasya = _site.Players["vasya"];
        await _site.SendAsync(new Engine.Players.AdjustPlayer(vasya, "Монетки", CoinsDelta: 10));
        await _site.SendAsync(new RollShop(vasya));
        var due = _site.Clock.UtcNow.AddMinutes(15);

        await SetShopAsync(on: false);
        Assert.Null(await NextTimerAsync(vasya));

        await SetShopAsync(on: true);
        Assert.Equal(due, await NextTimerAsync(vasya));
    }

    private async Task SetShopAsync(bool on)
    {
        await using var db = _site.NewDb();
        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
        await _site.SendAsync(new ChangeRuleset(state.Rules with { Features = state.Rules.Features with { Shop = on } }));
    }

    private async Task<DateTimeOffset?> NextTimerAsync(Guid player)
    {
        await using var db = _site.NewDb();
        return (await db.SeasonPlayers.AsNoTracking().SingleAsync(p => p.Id == player, Ct)).NextTimerAt;
    }

    [Fact]
    public async Task The_scheduler_fires_the_shops_timer_through_the_queue_once_it_has_come()
    {
        await EconomyOnAsync();
        var vasya = _site.Players["vasya"];
        await _site.SendAsync(new Engine.Players.AdjustPlayer(vasya, "Монетки", CoinsDelta: 10));
        await _site.SendAsync(new RollShop(vasya));

        await TickAsync();
        Assert.Equal(0, await CountAsync("shop-offer-expired"));

        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(16);
        await TickAsync();
        await TickAsync();

        Assert.Equal(1, await CountAsync("shop-offer-expired"));
        await using var db = _site.NewDb();
        Assert.Null((await db.SeasonPlayers.AsNoTracking().SingleAsync(p => p.Id == vasya, Ct)).NextTimerAt);
        var logged = await db.Events.AsNoTracking().SingleAsync(e => e.Type == "shop-offer-expired", Ct);
        Assert.Null(logged.AuthorId);
    }

    /// <summary>
    /// Events on (D-511): Вася uses a «fortune» that draws from the good deck its one event, which asks «монетки или шаг» and
    /// waits for his answer on the server. Returns the moment of the question.
    /// </summary>
    internal static async Task<DateTimeOffset> AskAnEventAsync(SiteFactory site, CancellationToken ct, int? timeoutHours = null)
    {
        ArgumentNullException.ThrowIfNull(site);
        await using (var db = site.NewDb())
        {
            var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, ct);
            await site.SendAsync(new ChangeRuleset(state.Rules with
            {
                Features = state.Rules.Features with { Items = true, Events = true },
                Events = new EventRules { DefaultDeck = EventKind.Good, AnswerTimeoutHours = timeoutHours, IrlProof = new IrlProofRules { MaxFiles = 5 } },
            }));
        }

        var self = new TargetSpec { Selector = TargetSelector.Self };
        var crossroads = new ObjectDefinition
        {
            Id = "crossroads",
            Kind = ObjectKind.Event,
            Name = "Перекрёсток",
            Description = "Монетки или шаг.",
            Rarity = Rarity.Common,
            Effect = new EffectSpec
            {
                Target = self,
                Outcomes = new OutcomesSpec
                {
                    Choice = new OutcomeChoice { Prompt = "Что берёшь?", Options = new ChoiceOptionsSpec { List = ["монетки", "шаг"] } },
                    Cases =
                    [
                        new OutcomeCase { Option = "монетки", Actions = [new ChangeResourceAction { Resource = "coins", Amount = ContentValue.Of(3) }] },
                        new OutcomeCase { Option = "шаг", Actions = [new MoveAction { Steps = ContentValue.Of(1) }] },
                    ],
                },
            },
        };
        await site.SendAsync(new PublishContent(
            new ContentPack
            {
                Objects = [crossroads, Item("fortune", 10, new EffectSpec { Target = self, Actions = [new DrawEventAction { Deck = "good" }] })],
                Wheels = [new WheelDefinition { Id = "good", Name = "Хорошие", Deck = EventKind.Good, Entries = [new WheelEntrySpec { ObjectId = "crossroads", Weight = 1 }] }],
            },
            "Контент"));

        var vasya = site.Players["vasya"];
        await site.SendAsync(new AdjustInventory(vasya, "fortune", null, "Подарок"));
        await using var read = site.NewDb();
        var (given, _) = await EventLogReader.ReplaySeasonAsync(read, SiteFactory.SeasonId, ct);
        var asked = site.Clock.UtcNow;
        await site.SendAsync(new UseItem(vasya, given.Players[vasya].Wallet.Inventory.Single(o => o.ObjectId == "fortune").InstanceId));
        return asked;
    }

    [Fact]
    public async Task A_question_of_an_event_waiting_for_its_answer_is_projected_equal_to_the_log()
    {
        // D-511: the suspended effect is the engine's bookkeeping, the pending choice its projected part
        await AskAnEventAsync(_site, Ct);

        await using var check = _site.NewDb();
        var (state, _) = await EventLogReader.ReplaySeasonAsync(check, SiteFactory.SeasonId, Ct);
        Assert.Single(state.Players[_site.Players["vasya"]].Suspended);
        var report = await SeasonIntegrity.CheckAsync(check, SiteFactory.SeasonId, Ct);
        Assert.True(report!.IsIntact, string.Join("; ", report.Differences));
    }

    [Fact]
    public async Task The_scheduler_answers_a_question_past_its_timeout_through_the_queue()
    {
        // D-560: the timer column is the moment of the timeout, the scheduler fires it by the server's clock
        var asked = await AskAnEventAsync(_site, Ct, timeoutHours: 24);
        var vasya = _site.Players["vasya"];
        Assert.Equal(asked.AddHours(24), await NextTimerAsync(vasya));

        await TickAsync();
        Assert.Equal(0, await CountAsync("effect-resumed"));

        _site.Clock.UtcNow = asked.AddHours(25);
        await TickAsync();
        await TickAsync();

        Assert.Equal(1, await CountAsync("effect-resumed"));
        Assert.Null(await NextTimerAsync(vasya));
        await using var check = _site.NewDb();
        Assert.True((await SeasonIntegrity.CheckAsync(check, SiteFactory.SeasonId, Ct))!.IsIntact);
    }

    [Fact]
    public async Task Conditions_of_runs_checked_changed_and_waived_are_projected_equal_to_the_log()
    {
        // D-598: the condition in the snapshot JSON, its check, the held dice and the waived one from the log
        await using (var db = _site.NewDb())
        {
            var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
            await _site.SendAsync(new ChangeRuleset(state.Rules with
            {
                Features = state.Rules.Features with { Items = true, Events = true },
                Events = new EventRules { DefaultDeck = EventKind.Good, IrlProof = new IrlProofRules { MaxFiles = 5 } },
            }));
        }

        var vow = Item("vow-hard", 10, new EffectSpec
        {
            Target = new TargetSpec { Selector = TargetSelector.Self },
            Actions = [new ModifyNextRollAction { RunCondition = new ConditionSpec { DifficultyAtLeast = Engine.Runs.Difficulty.Hard } }],
        });
        await _site.SendAsync(new PublishContent(new ContentPack { Objects = [vow] }, "Контент"));
        var vasya = _site.Players["vasya"];
        await _site.SendAsync(new AdjustInventory(vasya, "vow-hard", null, "Подарок"));
        Guid runId;
        await using (var read = _site.NewDb())
        {
            var (given, _) = await EventLogReader.ReplaySeasonAsync(read, SiteFactory.SeasonId, Ct);
            await _site.SendAsync(new UseItem(vasya, given.Players[vasya].Wallet.Inventory.Single(o => o.ObjectId == "vow-hard").InstanceId));
        }

        await _site.SendAsync(new Engine.Rolls.RollGame(vasya));
        await _site.SendAsync(new Engine.Runs.StartRun(vasya));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(5);
        await _site.SendAsync(new Engine.Runs.CompleteRun(vasya, Engine.Runs.Difficulty.Hard, 5m, "HLTB"));
        await using (var read = _site.NewDb())
        {
            var (completed, _) = await EventLogReader.ReplaySeasonAsync(read, SiteFactory.SeasonId, Ct);
            runId = completed.Runs.Values.Single(r => r.PlayerId == vasya).RunId;
            Assert.True(completed.Runs[runId].Condition!.Met);
        }

        await _site.SendAsync(new Engine.Proofs.ApproveProof(runId, Engine.Runs.Difficulty.Normal, "Нормальная"));
        await _site.SendAsync(new Engine.Runs.WaiveRunCondition(runId, "Неприменимо"));

        await using var check = _site.NewDb();
        var (state2, _) = await EventLogReader.ReplaySeasonAsync(check, SiteFactory.SeasonId, Ct);
        Assert.Equal(new Engine.Runs.RunConditionState(true, Waived: true), state2.Runs[runId].Condition);
        Assert.Single(state2.Runs[runId].Snapshot.Conditions);
        Assert.Equal("vow-hard", Assert.Single(state2.Players[vasya].Wallet.NextRoll).ObjectId);
        var report = await SeasonIntegrity.CheckAsync(check, SiteFactory.SeasonId, Ct);
        Assert.True(report!.IsIntact, string.Join("; ", report.Differences));
    }

    private async Task<int> CountAsync(string type)
    {
        await using var db = _site.NewDb();
        return await db.Events.CountAsync(e => e.Type == type, Ct);
    }

    private async Task TickAsync()
    {
        var scheduler = _site.Services.GetServices<IHostedService>().OfType<DeadlineScheduler>().Single();
        await scheduler.TickAsync(Ct);
    }
}

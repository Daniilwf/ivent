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

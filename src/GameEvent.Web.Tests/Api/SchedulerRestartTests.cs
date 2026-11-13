using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// D3: the scheduler survives a restart in the middle of a timer. Nothing about a timer lives in the process: the deadline
/// is a column of the season and the earliest economy timer a column of the player (D-101, D-404), so a new host fires
/// both when their moment comes, once, even when it came while the site was down.
/// </summary>
public sealed class SchedulerRestartTests : IAsyncLifetime
{
    private SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_deadline_that_came_while_the_site_was_down_closes_the_season_on_the_first_pass_after_the_restart()
    {
        // Given a deadline in an hour, and the scheduler has looked half an hour in
        await _site.SendAsync(new SetSeasonDeadline(_site.Clock.UtcNow.AddHours(1)));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(30);
        await TickAsync();
        Assert.Equal(SeasonStatus.Active, (await StateAsync()).Status);

        // When the site restarts and comes back after the deadline
        await RestartAsync();
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        await TickAsync();
        await TickAsync();

        // Then the new host closes the season once, as the system
        Assert.Equal(SeasonStatus.Closing, (await StateAsync()).Status);
        await using var db = _site.NewDb();
        var closings = await db.Events.AsNoTracking()
            .Where(e => e.Type == "season-status-changed" && e.Data.Contains("\"to\":\"closing\""))
            .ToListAsync(Ct);
        Assert.Null(Assert.Single(closings).AuthorId);
    }

    [Fact]
    public async Task A_deadline_still_ahead_after_the_restart_waits_for_its_moment()
    {
        await _site.SendAsync(new SetSeasonDeadline(_site.Clock.UtcNow.AddHours(1)));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(30);

        await RestartAsync();
        await TickAsync();
        Assert.Equal(SeasonStatus.Active, (await StateAsync()).Status);

        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(31);
        await TickAsync();
        Assert.Equal(SeasonStatus.Closing, (await StateAsync()).Status);
    }

    [Fact]
    public async Task A_shop_offer_rolled_before_the_restart_expires_after_it_once()
    {
        // Given Вася rolled the shop: its lots expire in 15 minutes (D-404)
        await ShopOnAsync();
        var vasya = _site.Players["vasya"];
        await _site.SendAsync(new Engine.Players.AdjustPlayer(vasya, "Монетки", CoinsDelta: 10));
        await _site.SendAsync(new RollShop(vasya));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(5);
        await TickAsync();

        // When the site restarts in the middle of the timer and the moment passes
        await RestartAsync();
        await TickAsync();
        Assert.Equal(0, await CountAsync("shop-offer-expired"));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(11);
        await TickAsync();
        await TickAsync();

        // Then the new host fires it once, and the timer column is clear
        Assert.Equal(1, await CountAsync("shop-offer-expired"));
        await using var db = _site.NewDb();
        Assert.Null((await db.SeasonPlayers.AsNoTracking().SingleAsync(p => p.Id == vasya, Ct)).NextTimerAt);
        var report = await SeasonIntegrity.CheckAsync(db, SiteFactory.SeasonId, Ct);
        Assert.True(report!.IsIntact, string.Join("; ", report.Differences));
    }

    // ---- Helpers ----

    private async Task RestartAsync()
    {
        var next = _site.Restarted();
        await _site.DisposeAsync();
        _site = next;
    }

    private async Task ShopOnAsync()
    {
        await using (var db = _site.NewDb())
        {
            var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
            await _site.SendAsync(new ChangeRuleset(state.Rules with { Features = state.Rules.Features with { Items = true, Shop = true } }));
        }

        var self = new TargetSpec { Selector = TargetSelector.Self };
        await _site.SendAsync(new PublishContent(
            new ContentPack
            {
                Objects =
                [
                    new ObjectDefinition
                    {
                        Id = "orange",
                        Kind = ObjectKind.Item,
                        Name = "orange",
                        Description = "Предмет для теста.",
                        Rarity = Rarity.Common,
                        Price = 10,
                        Window = UseWindow.Anytime,
                        Stackable = true,
                        Effect = new EffectSpec { Target = self, Actions = [new MoveAction { Steps = ContentValue.Of(1) }] },
                    },
                ],
            },
            "Контент"));
    }

    private async Task TickAsync()
    {
        var scheduler = _site.Services.GetServices<IHostedService>().OfType<DeadlineScheduler>().Single();
        await scheduler.TickAsync(Ct);
    }

    private async Task<SeasonState> StateAsync()
    {
        await using var db = _site.NewDb();
        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
        return state;
    }

    private async Task<int> CountAsync(string type)
    {
        await using var db = _site.NewDb();
        return await db.Events.CountAsync(e => e.Type == type, Ct);
    }
}

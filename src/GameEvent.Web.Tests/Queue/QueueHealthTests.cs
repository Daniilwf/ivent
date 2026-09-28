using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GameEvent.Web.Tests.Queue;

/// <summary>
/// The queue's own counters and its health check (D-107): how many commands wait, whether the consumer reads, whether a
/// command hangs — back to rest after work, a cancelled caller and a shutdown.
/// </summary>
public class QueueHealthTests
{
    private static readonly Guid s_season = Guid.Parse("30000000-0000-0000-0000-0000000000c1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task After_commands_the_queue_is_at_rest_and_healthy()
    {
        await using var h = await QueueHarness.StartAsync();

        Assert.True((await h.SendAsync(new CreateSeason(s_season, "Тестовый сезон", RulesetJson.Default()), s_season)).IsAccepted);
        Assert.True((await h.SendAsync(new ChangeSeasonStatus(SeasonStatus.Active), s_season)).IsAccepted);

        Assert.True(h.Bus.IsConsuming);
        Assert.Equal(0, h.Bus.Queued);
        await WaitUntil(() => h.Bus.CurrentCommandRunningFor is null);
        Assert.Equal(HealthStatus.Healthy, await Check(h.Bus));
    }

    [Fact]
    public async Task A_waiting_command_is_counted_and_a_hung_one_turns_the_check_red()
    {
        var block = new BlockingSaveInterceptor();
        await using var h = await QueueHarness.StartAsync(block);
        Assert.True((await h.SendAsync(new CreateSeason(s_season, "Тестовый сезон", RulesetJson.Default()), s_season)).IsAccepted);

        // Given one command held inside the processor and one more waiting behind it
        block.Armed = true;
        var held = h.SendAsync(new ChangeSeasonStatus(SeasonStatus.Active), s_season);
        await block.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var waiting = h.SendAsync(new AddSeasonPlayer(Guid.NewGuid(), Guid.NewGuid(), "Вася"), s_season);

        Assert.Equal(1, h.Bus.Queued);
        Assert.NotNull(h.Bus.CurrentCommandRunningFor);
        Assert.Equal(HealthStatus.Healthy, await Check(h.Bus));
        Assert.Equal(HealthStatus.Degraded, await Check(h.Bus, ("Health:MaxQueuedCommands", "0")));
        Assert.Equal(HealthStatus.Unhealthy, await Check(h.Bus, ("Health:MaxCommandSeconds", "0")));

        block.Release();
        Assert.True((await held).IsAccepted);
        Assert.True((await waiting).IsAccepted);
        Assert.Equal(0, h.Bus.Queued);
        await WaitUntil(() => h.Bus.CurrentCommandRunningFor is null);
    }

    [Fact]
    public async Task A_command_whose_caller_gave_up_is_uncounted_when_the_consumer_takes_it()
    {
        var block = new BlockingSaveInterceptor();
        await using var h = await QueueHarness.StartAsync(block);
        Assert.True((await h.SendAsync(new CreateSeason(s_season, "Тестовый сезон", RulesetJson.Default()), s_season)).IsAccepted);

        block.Armed = true;
        var held = h.SendAsync(new ChangeSeasonStatus(SeasonStatus.Active), s_season);
        await block.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using var cts = new CancellationTokenSource();
        var gaveUp = h.SendCancellableAsync(new AddSeasonPlayer(Guid.NewGuid(), Guid.NewGuid(), "Вася"), s_season, Guid.NewGuid(), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gaveUp);

        // Still in the channel: counted until the consumer takes it
        Assert.Equal(1, h.Bus.Queued);

        block.Release();
        await held;
        await h.SendAsync(new AddSeasonPlayer(Guid.NewGuid(), Guid.NewGuid(), "Петя"), s_season);
        Assert.Equal(0, h.Bus.Queued);
    }

    [Fact]
    public async Task After_a_shutdown_nothing_is_counted_and_the_check_is_red()
    {
        var block = new BlockingSaveInterceptor();
        await using var h = await QueueHarness.StartAsync(block);
        Assert.True((await h.SendAsync(new CreateSeason(s_season, "Тестовый сезон", RulesetJson.Default()), s_season)).IsAccepted);
        block.Armed = true;
        var held = h.SendAsync(new ChangeSeasonStatus(SeasonStatus.Active), s_season);
        await block.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var waiting = h.SendAsync(new AddSeasonPlayer(Guid.NewGuid(), Guid.NewGuid(), "Вася"), s_season);

        await h.StopAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(h.Bus.IsConsuming);
        Assert.Equal(0, h.Bus.Queued);
        Assert.Null(h.Bus.CurrentCommandRunningFor);
        Assert.Equal(HealthStatus.Unhealthy, await Check(h.Bus));
    }

    [Fact]
    public async Task A_bus_nobody_reads_is_red()
    {
        Assert.Equal(HealthStatus.Unhealthy, await Check(new CommandBus()));
    }

    private static async Task<HealthStatus> Check(CommandBus bus, params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var result = await new QueueHealthCheck(bus, configuration).CheckHealthAsync(new HealthCheckContext(), Ct);
        return result.Status;
    }

    // The consumer clears its "running" mark right after completing the caller's task
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(condition());
    }
}

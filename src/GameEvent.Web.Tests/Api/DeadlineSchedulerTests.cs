using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.EventLog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The deadline scheduler (C10, SE1, D-101): <c>GameEvent.Infrastructure.Seasons.DeadlineScheduler</c>, a hosted
/// <see cref="BackgroundService"/> that every <c>Scheduler:IntervalSeconds</c> finds Active seasons whose deadline has
/// come by the server's clock and sends <see cref="ReachDeadline"/> through the common queue (the system as author, the
/// command id derived from the season and the deadline, so a repeat is a duplicate). <c>TickAsync</c> does one pass. The
/// type is found by name, so the tests compile before it exists.
/// </summary>
public sealed class DeadlineSchedulerTests : IAsyncLifetime
{
    private const string SchedulerType = "GameEvent.Infrastructure.Seasons.DeadlineScheduler";

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public void Scheduler_is_a_hosted_background_service_with_a_single_pass()
    {
        var scheduler = Scheduler();

        Assert.IsAssignableFrom<BackgroundService>(scheduler);
        var tick = scheduler.GetType().GetMethod("TickAsync", [typeof(CancellationToken)]);
        Assert.NotNull(tick);
        Assert.Equal(typeof(Task), tick.ReturnType);
    }

    [Fact]
    public async Task Tick_before_the_deadline_changes_nothing()
    {
        await _site.SendAsync(new SetSeasonDeadline(_site.Clock.UtcNow.AddHours(1)));
        var before = await EventCountAsync();

        await TickAsync();

        Assert.Equal(before, await EventCountAsync());
        Assert.Equal(SeasonStatus.Active, (await StateAsync()).Status);
    }

    [Fact]
    public async Task Tick_after_the_deadline_closes_the_season_through_the_queue()
    {
        await _site.SendAsync(new SetSeasonDeadline(_site.Clock.UtcNow.AddHours(1)));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(2);

        await TickAsync();

        Assert.Equal(SeasonStatus.Closing, (await StateAsync()).Status);
        Assert.Equal(1, await ClosingsAsync());
        await using var db = _site.NewDb();
        var logged = await db.Events.Where(e => e.Type == "season-status-changed").OrderBy(e => e.Sequence).LastAsync(Ct);
        Assert.Null(logged.AuthorId);
    }

    [Fact]
    public async Task Second_tick_changes_nothing()
    {
        await _site.SendAsync(new SetSeasonDeadline(_site.Clock.UtcNow.AddHours(1)));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(2);
        await TickAsync();
        var after = await EventCountAsync();

        await TickAsync();

        Assert.Equal(after, await EventCountAsync());
        Assert.Equal(1, await ClosingsAsync());
    }

    [Fact]
    public async Task Tick_without_a_deadline_changes_nothing()
    {
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddDays(365);
        var before = await EventCountAsync();

        await TickAsync();

        Assert.Equal(before, await EventCountAsync());
    }

    [Fact]
    public async Task Tick_leaves_a_draft_season_alone()
    {
        var draft = await _site.CreateSeasonAsync();
        await _site.Services.GetRequiredService<Infrastructure.Queue.CommandBus>().SendAsync(
            new Infrastructure.Queue.CommandEnvelope(Guid.NewGuid(), draft, new SetSeasonDeadline(_site.Clock.UtcNow.AddHours(1)), AuthorId: null), Ct);
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(2);

        await TickAsync();

        await using var db = _site.NewDb();
        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, draft, Ct);
        Assert.Equal(SeasonStatus.Draft, state.Status);
    }

    // ---- Helpers ----

    private object Scheduler()
    {
        var scheduler = _site.Services.GetServices<IHostedService>().FirstOrDefault(s => s.GetType().FullName == SchedulerType);
        Assert.True(scheduler is not null, $"{SchedulerType} is not registered as a hosted service.");
        return scheduler;
    }

    private async Task TickAsync()
    {
        var scheduler = Scheduler();
        var tick = scheduler.GetType().GetMethod("TickAsync", [typeof(CancellationToken)]);
        Assert.NotNull(tick);
        await (Task)tick.Invoke(scheduler, [Ct])!;
    }

    private async Task<SeasonState> StateAsync()
    {
        await using var db = _site.NewDb();
        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
        return state;
    }

    private async Task<int> EventCountAsync()
    {
        await using var db = _site.NewDb();
        return await db.Events.CountAsync(Ct);
    }

    private async Task<int> ClosingsAsync()
    {
        await using var db = _site.NewDb();
        return await db.Events.CountAsync(e => e.Type == "season-status-changed" && e.Data.Contains("\"to\":\"closing\""), Ct);
    }

    [Fact]
    public void Command_id_follows_the_season_and_the_deadline()
    {
        // A repeated tick for the same deadline is a duplicate; a moved deadline is a new command (D-101)
        var season = Guid.Parse("30000000-0000-0000-0000-0000000000aa");
        var deadline = new DateTimeOffset(2026, 12, 31, 21, 0, 0, TimeSpan.Zero);

        var same = Infrastructure.Seasons.DeadlineScheduler.CommandIdFor(season, deadline);

        Assert.Equal(same, Infrastructure.Seasons.DeadlineScheduler.CommandIdFor(season, deadline.ToOffset(TimeSpan.FromHours(3))));
        Assert.NotEqual(same, Infrastructure.Seasons.DeadlineScheduler.CommandIdFor(season, deadline.AddMinutes(1)));
        Assert.NotEqual(same, Infrastructure.Seasons.DeadlineScheduler.CommandIdFor(Guid.NewGuid(), deadline));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Interval_must_be_above_zero(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Infrastructure.Seasons.DeadlineSchedulerSettings(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Scheduler_loop_is_off_in_the_tests()
    {
        // The tests call TickAsync themselves; a background tick would race the clock they move
        Assert.False(_site.Services.GetRequiredService<Infrastructure.Seasons.DeadlineSchedulerSettings>().Enabled);
    }
}

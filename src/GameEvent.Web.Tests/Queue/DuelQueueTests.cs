using GameEvent.Engine.Duels;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Rulesets;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Web.Tests.Api;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Queue;

/// <summary>
/// Duels through the queue (D-730, EV36): a challenge and then its race are due on the challenger's timer column, though
/// neither changes his player row, and the projection stays equal to the log.
/// </summary>
public sealed class DuelQueueTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_challenge_and_its_race_are_due_on_the_challengers_timer_column()
    {
        await using (var db = _site.NewDb())
        {
            var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
            await _site.SendAsync(new ChangeRuleset(state.Rules with { Features = state.Rules.Features with { Duels = true } }));
        }

        var (vasya, petya) = (_site.Players["vasya"], _site.Players["petya"]);
        await _site.SendAsync(new ChallengeToDuel(vasya, petya));
        var now = _site.Clock.UtcNow;
        Assert.Equal(now.AddHours(24), await NextTimerAsync(vasya));

        Guid challenge;
        await using (var db = _site.NewDb())
        {
            var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
            challenge = Assert.Single(state.Interactions.Values, i => i.Kind == InteractionKind.Duel).InteractionId;
        }

        await _site.SendAsync(new AnswerInvitation(petya, challenge, Accept: true));

        Assert.Equal(now.AddDays(7), await NextTimerAsync(vasya));
        Assert.Null(await NextTimerAsync(petya));
        await using var check = _site.NewDb();
        var report = await SeasonIntegrity.CheckAsync(check, SiteFactory.SeasonId, Ct);
        Assert.True(report!.IsIntact, string.Join("; ", report.Differences));
    }

    private async Task<DateTimeOffset?> NextTimerAsync(Guid player)
    {
        await using var db = _site.NewDb();
        return (await db.SeasonPlayers.AsNoTracking().SingleAsync(p => p.Id == player, Ct)).NextTimerAt;
    }
}

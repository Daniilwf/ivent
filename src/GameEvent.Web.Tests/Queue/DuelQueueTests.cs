using GameEvent.Engine.Duels;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Web.Tests.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameEvent.Web.Tests.Queue;

/// <summary>
/// Duels through the queue (D-730, EV36): a challenge and then its race are due on the challenger's timer column, though
/// neither changes his player row, and the projection stays equal to the log. Deleting an account (D-734, decided by the
/// owner 2026-11-14) cancels the user's active duels without a winner or a payout and closes his pending challenges.
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

    // ---- Deleting an account (D-734, decided by the owner 2026-11-14 as the expert panel's advice (6)) ----

    [Fact]
    public async Task Deleting_an_account_cancels_its_active_duel_without_a_winner_or_a_payout()
    {
        await DuelsOnAsync();
        var (vasya, petya) = (_site.Players["vasya"], _site.Players["petya"]);
        await _site.SendAsync(new ChallengeToDuel(vasya, petya));
        var duel = await SingleChallengeAsync(vasya);
        await _site.SendAsync(new AnswerInvitation(petya, duel, Accept: true));
        var coins = (await StateAsync()).Players[petya].Coins;

        await DeleteAccountAsync("vasya");

        var state = await StateAsync();
        var cancelled = state.Duels[duel];
        Assert.Equal((DuelStatus.Cancelled, (Guid?)null, 0), (cancelled.Status, cancelled.WinnerId, cancelled.Coins));
        Assert.Equal(coins, state.Players[petya].Coins);
        await AssertIntactAsync();
    }

    [Fact]
    public async Task Deleting_an_account_closes_its_pending_challenges_both_ways()
    {
        // Two open duels a player, so Вася may wait for an answer and be challenged at once
        await DuelsOnAsync(maxOpenPerPlayer: 2);
        var masha = await AddMashaAsync();
        var (vasya, petya) = (_site.Players["vasya"], _site.Players["petya"]);
        await _site.SendAsync(new ChallengeToDuel(vasya, petya));
        var sent = await SingleChallengeAsync(vasya);
        await _site.SendAsync(new ChallengeToDuel(masha, vasya));
        var received = await SingleChallengeAsync(masha);

        await DeleteAccountAsync("vasya");

        var state = await StateAsync();
        Assert.Contains(state.Interactions[sent].Status, new[] { InteractionStatus.Expired, InteractionStatus.Withdrawn });
        Assert.Contains(state.Interactions[received].Status, new[] { InteractionStatus.Expired, InteractionStatus.Withdrawn });
        Assert.Empty(state.Duels);
        await AssertIntactAsync();
    }

    [Fact]
    public async Task Deleting_an_account_without_duels_leaves_the_duels_of_others_as_they_are()
    {
        await DuelsOnAsync();
        await AddMashaAsync();
        var (vasya, petya) = (_site.Players["vasya"], _site.Players["petya"]);
        await _site.SendAsync(new ChallengeToDuel(vasya, petya));
        var duel = await SingleChallengeAsync(vasya);
        await _site.SendAsync(new AnswerInvitation(petya, duel, Accept: true));
        var before = await StateAsync();

        await DeleteAccountAsync("masha");

        var after = await StateAsync();
        Assert.Equal(DuelStatus.Active, after.Duels[duel].Status);
        Assert.Equal(before.Duels, after.Duels);
        Assert.Equal(before.Interactions, after.Interactions);
    }

    /// <summary>The admin deletes the account (a global command: no season in the envelope); must be accepted.</summary>
    private async Task DeleteAccountAsync(string login)
    {
        var outcome = await _site.Services.GetRequiredService<CommandBus>()
            .SendAsync(new CommandEnvelope(Guid.NewGuid(), Guid.Empty, new DeleteAccount(_site.Users[login]), AuthorId: null), Ct);
        Assert.True(outcome.IsAccepted, $"Deleting the account was rejected: {outcome.Rejection}");
    }

    private async Task DuelsOnAsync(int maxOpenPerPlayer = 1)
    {
        var state = await StateAsync();
        await _site.SendAsync(new ChangeRuleset(state.Rules with
        {
            Features = state.Rules.Features with { Duels = true },
            Duels = state.Rules.Duels! with { MaxOpenPerPlayer = maxOpenPerPlayer },
        }));
    }

    private async Task<Guid> AddMashaAsync()
    {
        _site.Players["masha"] = Guid.NewGuid();
        await _site.SendAsync(new AddSeasonPlayer(_site.Players["masha"], _site.Users["masha"], "masha"));
        return _site.Players["masha"];
    }

    private async Task<Guid> SingleChallengeAsync(Guid from) =>
        Assert.Single((await StateAsync()).Interactions.Values, i => i.Kind == InteractionKind.Duel && i.FromPlayerId == from && i.Status == InteractionStatus.Pending).InteractionId;

    private async Task<SeasonState> StateAsync()
    {
        await using var db = _site.NewDb();
        return (await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct)).State;
    }

    private async Task AssertIntactAsync()
    {
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

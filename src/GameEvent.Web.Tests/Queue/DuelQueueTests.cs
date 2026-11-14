using GameEvent.Engine.Duels;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
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
    private SiteFactory _site = new();

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
        Assert.Equal((InteractionStatus.Expired, InteractionStatus.Expired), (state.Interactions[sent].Status, state.Interactions[received].Status));
        Assert.Equal(
            new[] { sent, received }.Order(),
            (await ExpiredAsync()).Where(e => e.Reason == InteractionExpiry.AccountDeleted).Select(e => e.InteractionId).Order());
        Assert.Empty(state.Duels);
        await AssertIntactAsync();
    }

    // ---- What a deletion missed is caught up (D-792) ----

    [Fact]
    public async Task A_repeat_of_the_deletion_closes_what_its_first_run_missed()
    {
        // Вася has nothing open when deleted, then is challenged: the repeat of the same deletion (a client's retry after a
        // stop) is a duplicate, and still closes the challenge
        await DuelsOnAsync();
        var (vasya, petya) = (_site.Players["vasya"], _site.Players["petya"]);
        var deletion = Guid.NewGuid();
        await DeleteAccountAsync("vasya", deletion);
        await _site.SendAsync(new ChallengeToDuel(petya, vasya));
        var challenge = await SingleChallengeAsync(petya);

        await DeleteAccountAsync("vasya", deletion);

        Assert.Equal(InteractionStatus.Expired, (await StateAsync()).Interactions[challenge].Status);
        Assert.Equal(InteractionExpiry.AccountDeleted, Assert.Single(await ExpiredAsync()).Reason);
        await AssertIntactAsync();
    }

    [Fact]
    public async Task The_timers_pass_closes_the_duels_a_deleted_account_left()
    {
        await DuelsOnAsync();
        var (vasya, petya) = (_site.Players["vasya"], _site.Players["petya"]);
        await DeleteAccountAsync("vasya");
        await _site.SendAsync(new ChallengeToDuel(petya, vasya));
        var challenge = await SingleChallengeAsync(petya);

        // Nothing is due by the engine's clock: the pass itself is refused, the sweep after it still runs
        await _site.Services.GetRequiredService<CommandBus>()
            .SendAsync(new CommandEnvelope(Guid.NewGuid(), SiteFactory.SeasonId, new Engine.Economy.FireTimers(), AuthorId: null), Ct);

        Assert.Equal(InteractionStatus.Expired, (await StateAsync()).Interactions[challenge].Status);
        Assert.Equal(InteractionExpiry.AccountDeleted, Assert.Single(await ExpiredAsync()).Reason);
        await AssertIntactAsync();
    }

    [Fact]
    public async Task The_start_of_the_site_closes_the_duels_a_deleted_account_left()
    {
        await DuelsOnAsync();
        var (vasya, petya) = (_site.Players["vasya"], _site.Players["petya"]);
        await _site.SendAsync(new ChallengeToDuel(petya, vasya));
        var challenge = await SingleChallengeAsync(petya);
        await _site.SendAsync(new AnswerInvitation(vasya, challenge, Accept: true));

        // The deletion committed, the server stopped before its season command: the account is marked as the deletion left it
        await using (var db = _site.NewDb())
        {
            await db.Users.Where(u => u.Id == _site.Users["vasya"]).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDeleted, true), Ct);
        }

        var next = _site.Restarted();
        await _site.DisposeAsync();
        _site = next;
        _ = _site.Services; // starts the new host: its queue sweeps before the first command

        var duel = await WaitForAsync(async () => (await StateAsync()).Duels[challenge], d => d.Status != DuelStatus.Active);
        Assert.Equal((DuelStatus.Cancelled, DuelEnd.AccountDeleted), (duel.Status, duel.End));
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
    private async Task DeleteAccountAsync(string login, Guid? commandId = null)
    {
        var outcome = await _site.Services.GetRequiredService<CommandBus>()
            .SendAsync(new CommandEnvelope(commandId ?? Guid.NewGuid(), Guid.Empty, new DeleteAccount(_site.Users[login]), AuthorId: null), Ct);
        Assert.True(outcome.IsAccepted, $"Deleting the account was rejected: {outcome.Rejection}");
    }

    /// <summary>The season's logged <see cref="InteractionExpired"/> events, not undone.</summary>
    private async Task<List<InteractionExpired>> ExpiredAsync()
    {
        await using var db = _site.NewDb();
        return [.. (await db.Events.AsNoTracking()
                .Where(e => e.SeasonId == SiteFactory.SeasonId && e.Type == "interaction-expired" && e.UndoneByEventId == null)
                .ToListAsync(Ct))
            .Select(e => EventCodec.Decode(new StoredEvent(e.Type, e.Version, e.Data)))
            .OfType<InteractionExpired>()];
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        for (var tries = 0; ; tries++)
        {
            var value = await read();
            if (done(value) || tries >= 100)
            {
                return value;
            }

            await Task.Delay(50, Ct);
        }
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

using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Seasons;
using GameEvent.Web.Tests.Api;
using Microsoft.EntityFrameworkCore;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Queue;

/// <summary>
/// EV36 (task 5.16): the timers of stage 5 live in the state and in columns, go through the queue as the system and survive a
/// restart. The season's own moments — an open poll's close (D-514, D-727) and a global event's end (D-517, D-756) — are in
/// the season's column <c>Season.NextTimerAt</c> (<c>Timers.NextOfSeason</c>); invitations, duels and requests of the
/// partner board are in the player's <c>SeasonPlayer.NextTimerAt</c>. The scheduler reads both and sends <c>FireTimers</c>.
/// Written by <c>test-author</c> from the SPEC, D-404, D-727, D-756 and the public contract of the engine, before the
/// implementation.
/// </summary>
public sealed class EventsQueueTests : IAsyncLifetime
{
    private SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- The season's timer column ----

    [Fact]
    public async Task An_open_poll_puts_its_close_into_the_seasons_timer_column_and_the_close_clears_it()
    {
        await PollsOnAsync(_site);
        Assert.Null(await SeasonTimerAsync());

        await _site.SendAsync(new StartPoll(Nomination(hours: 24)));
        var poll = Assert.Single((await StateAsync(_site)).Polls.Values);

        Assert.Equal(poll.ClosesAt, await SeasonTimerAsync());
        Assert.Equal(_site.Clock.UtcNow.AddHours(24), poll.ClosesAt);

        // The admin closes it early: nothing of the season waits any more
        await _site.SendAsync(new ClosePoll(poll.PollId));
        Assert.Null(await SeasonTimerAsync());
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task The_seasons_timer_column_holds_the_earliest_of_polls_and_global_events()
    {
        await PollsOnAsync(_site);
        await GlobalEventsOnAsync(_site);
        var start = _site.Clock.UtcNow;

        await _site.SendAsync(new StartPoll(Nomination(hours: 48)));
        Assert.Equal(start.AddHours(48), await SeasonTimerAsync());

        // A global event ending sooner comes first; the poll's close after it
        await _site.SendAsync(new LaunchGlobalEvent(CoinRain.Id, DurationHours: 12));
        Assert.Equal(start.AddHours(12), await SeasonTimerAsync());

        var ev = Assert.Single((await StateAsync(_site)).GlobalEvents.Values);
        await _site.SendAsync(new EndGlobalEvent(ev.GlobalEventId));
        Assert.Equal(start.AddHours(48), await SeasonTimerAsync());
        await AssertIntactAsync(_site);
    }

    // ---- The scheduler: polls by their term (D-727) ----

    [Fact]
    public async Task The_scheduler_closes_a_poll_at_its_term_once_as_the_system()
    {
        // Nothing of any player is due: only the season's column says the poll's moment has come
        await PollsOnAsync(_site);
        await _site.SendAsync(new StartPoll(Nomination(hours: 24)));
        var poll = Assert.Single((await StateAsync(_site)).Polls.Values);

        _site.Clock.UtcNow = poll.ClosesAt.AddSeconds(-1);
        await TickAsync(_site);
        Assert.Equal(0, await CountAsync(_site, "poll-closed"));

        _site.Clock.UtcNow = poll.ClosesAt;
        await TickAsync(_site);
        await TickAsync(_site);

        await using var db = _site.NewDb();
        var closed = await db.Events.AsNoTracking().Where(e => e.SeasonId == SiteFactory.SeasonId && e.Type == "poll-closed").ToListAsync(Ct);
        var logged = Assert.Single(closed);
        Assert.Null(logged.AuthorId);
        Assert.Contains("\"reason\":\"timer\"", logged.Data, StringComparison.Ordinal);
        Assert.Equal(PollStatus.Closed, (await StateAsync(_site)).Polls[poll.PollId].Status);
        Assert.Null(await SeasonTimerAsync());
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task After_the_deadline_the_scheduler_still_closes_a_nomination_by_its_term()
    {
        // D-516, S5.8-20: a poll without an effect waits for its time in a closing season
        await PollsOnAsync(_site);
        await _site.SendAsync(new StartPoll(Nomination(hours: 24)));
        var poll = Assert.Single((await StateAsync(_site)).Polls.Values);
        await _site.SendAsync(new SetSeasonDeadline(_site.Clock.UtcNow.AddHours(1)));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(2);
        await TickAsync(_site);
        Assert.Equal(SeasonStatus.Closing, (await StateAsync(_site)).Status);
        Assert.Equal(PollStatus.Open, (await StateAsync(_site)).Polls[poll.PollId].Status);

        _site.Clock.UtcNow = poll.ClosesAt.AddMinutes(1);
        await TickAsync(_site);

        Assert.Equal(PollStatus.Closed, (await StateAsync(_site)).Polls[poll.PollId].Status);
        Assert.Equal(1, await CountAsync(_site, "poll-closed"));
    }

    // ---- The scheduler: global events by their term (D-756) ----

    [Fact]
    public async Task The_scheduler_ends_a_global_event_at_its_term_once_as_the_system()
    {
        await GlobalEventsOnAsync(_site);
        await _site.SendAsync(new LaunchGlobalEvent(CoinRain.Id, DurationHours: 24));
        var ev = Assert.Single((await StateAsync(_site)).GlobalEvents.Values);

        _site.Clock.UtcNow = ev.EndsAt.AddSeconds(-1);
        await TickAsync(_site);
        Assert.Equal(0, await CountAsync(_site, "global-event-ended"));

        _site.Clock.UtcNow = ev.EndsAt;
        await TickAsync(_site);
        await TickAsync(_site);

        await using var db = _site.NewDb();
        var logged = Assert.Single(await db.Events.AsNoTracking().Where(e => e.Type == "global-event-ended").ToListAsync(Ct));
        Assert.Null(logged.AuthorId);
        var state = await StateAsync(_site);
        Assert.Equal((GlobalEventStatus.Ended, GlobalEventEndReason.Timer), (state.GlobalEvents[ev.GlobalEventId].Status, state.GlobalEvents[ev.GlobalEventId].EndReason));
        Assert.Null(await SeasonTimerAsync());
        await AssertIntactAsync(_site);
    }

    // ---- Restart (EV36, D3) ----

    [Fact]
    public async Task A_poll_and_a_global_event_whose_terms_came_while_the_site_was_down_end_after_the_restart_once()
    {
        await PollsOnAsync(_site);
        await GlobalEventsOnAsync(_site);
        await _site.SendAsync(new StartPoll(Nomination(hours: 24)));
        await _site.SendAsync(new LaunchGlobalEvent(CoinRain.Id, DurationHours: 12));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        await TickAsync(_site);

        // The site goes down, and both terms pass while it is
        await RestartAsync();
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(30);
        await TickAsync(_site);
        await TickAsync(_site);

        Assert.Equal(1, await CountAsync(_site, "poll-closed"));
        Assert.Equal(1, await CountAsync(_site, "global-event-ended"));
        Assert.Null(await SeasonTimerAsync());
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_term_still_ahead_after_the_restart_waits_for_its_moment()
    {
        await PollsOnAsync(_site);
        await _site.SendAsync(new StartPoll(Nomination(hours: 24)));
        var poll = Assert.Single((await StateAsync(_site)).Polls.Values);

        await RestartAsync();
        Assert.Equal(poll.ClosesAt, await SeasonTimerAsync());
        _site.Clock.UtcNow = poll.ClosesAt.AddMinutes(-1);
        await TickAsync(_site);
        Assert.Equal(0, await CountAsync(_site, "poll-closed"));

        _site.Clock.UtcNow = poll.ClosesAt.AddMinutes(1);
        await TickAsync(_site);
        Assert.Equal(1, await CountAsync(_site, "poll-closed"));
    }

    [Fact]
    public async Task A_coop_invitation_burns_by_its_term_after_a_restart()
    {
        // D-523: the invitation's term is the inviter's timer column; nothing about it lives in the process
        await CoopOnAsync(_site);
        var (vasya, petya) = (_site.Players["vasya"], _site.Players["petya"]);
        await PlayingAsync(_site, vasya);
        await _site.SendAsync(new InviteToCoop(vasya, petya));
        var invitation = Assert.Single((await StateAsync(_site)).Interactions.Values);
        Assert.Equal(invitation.ExpiresAt, await PlayerTimerAsync(vasya));

        await RestartAsync();
        _site.Clock.UtcNow = invitation.ExpiresAt.AddMinutes(1);
        await TickAsync(_site);
        await TickAsync(_site);

        Assert.Equal(1, await CountAsync(_site, "interaction-expired"));
        Assert.Equal(InteractionStatus.Expired, (await StateAsync(_site)).Interactions[invitation.InteractionId].Status);
        Assert.Null(await PlayerTimerAsync(vasya));
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_request_of_the_partner_board_closes_by_its_term_after_a_restart()
    {
        await CoopOnAsync(_site, partnerBoard: true);
        var vasya = _site.Players["vasya"];
        await PlayingAsync(_site, vasya);
        await _site.SendAsync(new PostPartnerRequest(vasya));
        var request = Assert.Single((await StateAsync(_site)).PartnerRequests.Values);
        Assert.Equal(request.ExpiresAt, await PlayerTimerAsync(vasya));

        await RestartAsync();
        _site.Clock.UtcNow = request.ExpiresAt.AddMinutes(1);
        await TickAsync(_site);
        await TickAsync(_site);

        Assert.Equal(1, await CountAsync(_site, "partner-request-closed"));
        Assert.Equal(PartnerRequestStatus.Closed, (await StateAsync(_site)).PartnerRequests[request.RequestId].Status);
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_duels_race_ends_by_its_term_after_a_restart()
    {
        // D-730: the race's end is the challenger's timer column; a draw when nobody completed in time
        await RulesAsync(_site, Flags(f => f with { Duels = true }));
        var (vasya, petya) = (_site.Players["vasya"], _site.Players["petya"]);
        await _site.SendAsync(new Engine.Duels.ChallengeToDuel(vasya, petya));
        var challenge = Assert.Single((await StateAsync(_site)).Interactions.Values);
        await _site.SendAsync(new AnswerInvitation(petya, challenge.InteractionId, Accept: true));
        var duel = Assert.Single((await StateAsync(_site)).Duels.Values);

        await RestartAsync();
        _site.Clock.UtcNow = duel.ExpiresAt.AddMinutes(1);
        await TickAsync(_site);
        await TickAsync(_site);

        Assert.Equal(1, await CountAsync(_site, "duel-settled"));
        Assert.Null(await PlayerTimerAsync(vasya));
        await AssertIntactAsync(_site);
    }

    // ---- Helpers ----

    private async Task RestartAsync()
    {
        var next = _site.Restarted();
        await _site.DisposeAsync();
        _site = next;
    }

    /// <summary>The season's timer column, by plain SQL: it is the migration «Events»'s (UTC ticks, as every moment).</summary>
    private async Task<DateTimeOffset?> SeasonTimerAsync()
    {
        await using var db = _site.NewDb();
        await AssertSchemaAsync(db, "Season", "NextTimerAt");
        var ticks = await ScalarAsync(db, "SELECT NextTimerAt FROM Season WHERE upper(Id) = upper($p0)", SiteFactory.SeasonId.ToString());
        return ticks is null ? null : new DateTimeOffset(Convert.ToInt64(ticks, System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero);
    }

    private async Task<DateTimeOffset?> PlayerTimerAsync(Guid player)
    {
        await using var db = _site.NewDb();
        return (await db.SeasonPlayers.AsNoTracking().SingleAsync(p => p.Id == player, Ct)).NextTimerAt;
    }
}

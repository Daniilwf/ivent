using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Interactions;

/// <summary>
/// Coop runs and their invitations (SPEC «Взаимодействие игроков», D-520…D-523, D-545): the roller of a running run of a
/// coop game invites a free player; the accepted one becomes the partner, playing that one run with him. The roller
/// completes it with one throw by his own pipeline; points, steps and completion coins are split (D-521), each moves by his
/// share with his own move. The partner may leave; the roller's drop or tech reroll ends the coop; a reject takes back each
/// one's share (D-522). Everything is refused while <c>features.coop</c> is off, and what it left waits (D-420).
/// </summary>
internal static class Coop
{
    public static Decision Decide(SeasonState state, InviteToCoop command, EngineContext context)
    {
        if (Guard(state, command.PlayerId, context) is { } rejection)
        {
            return rejection;
        }

        var inviter = state.Players[command.PlayerId];
        if (inviter.ActiveRunId is not { } runId || !state.Runs.TryGetValue(runId, out var run) || run.Status != RunStatus.Playing)
        {
            return Decision.Reject(RejectionCodes.CoopRunNotActive, "Only a player playing a run invites to it.");
        }

        if (run.PlayerId != inviter.PlayerId)
        {
            return Decision.Reject(RejectionCodes.CoopRollerOnly, "Only the roller of a coop run invites to it.");
        }

        if (FinishLine.First(state) == inviter.PlayerId)
        {
            return Decision.Reject(RejectionCodes.CoopFirstFinisher, "The first finisher does not play with others (D-510).");
        }

        if (!IsCoopGame(context, run.GameId))
        {
            return Decision.Reject(RejectionCodes.CoopNotCoopGame, "Only a coop game takes a partner.");
        }

        if (run.Coop is not null)
        {
            return Decision.Reject(RejectionCodes.CoopAlreadyJoined, "The run has a partner already.");
        }

        if (Partner(state, command.PartnerId, inviter.PlayerId, run) is { } partnerRefusal)
        {
            return partnerRefusal;
        }

        // Pending by the engine's clock: one past its time is over even before the timer fired (D-523)
        var now = context.Clock.UtcNow;
        // The roller's own: responses of the partner board waiting on the run do not count (D-740)
        if (state.Interactions.Values.Any(i => IsPending(i, now) && i.Kind == InteractionKind.Coop && i.RunId == run.RunId
            && i.FromPlayerId == inviter.PlayerId))
        {
            return Decision.Reject(RejectionCodes.CoopInviteAlreadyOpen, "The run has a pending coop invitation already.");
        }

        var limit = state.Rules.Interactions.MaxOpenInvitesPerPlayer
            ?? throw new InvalidOperationException("interactions.maxOpenInvitesPerPlayer is required with coop on.");
        if (state.Interactions.Values.Count(i => IsPending(i, now) && i.FromPlayerId == inviter.PlayerId) >= limit)
        {
            return Decision.Reject(RejectionCodes.InteractionTooMany, $"At most {limit} pending invitations sent.");
        }

        return Decision.Accept(new InteractionCreated(new Interaction(
            context.Ids.NewId(),
            InteractionKind.Coop,
            inviter.PlayerId,
            command.PartnerId,
            run.RunId,
            now,
            now.AddHours(state.Rules.Interactions.InviteTtlHours))));
    }

    public static Decision Decide(SeasonState state, AnswerInvitation command, EngineContext context)
    {
        if (Guard(state, command.PlayerId, context) is { } rejection)
        {
            return rejection;
        }

        if (Pending(state, command.InteractionId, context) is { } notPending)
        {
            return notPending;
        }

        var invitation = state.Interactions[command.InteractionId];
        if (invitation.Kind != InteractionKind.Coop)
        {
            return Decision.Reject(RejectionCodes.InteractionUnknown, "Only coop invitations are answered in this build.");
        }

        if (invitation.ToPlayerId != command.PlayerId)
        {
            return Decision.Reject(RejectionCodes.InteractionNotYours, "Only the invited player answers.");
        }

        var now = context.Clock.UtcNow;
        var answered = new InteractionAnswered(invitation.InteractionId, command.PlayerId, command.Accept, now);
        if (!command.Accept)
        {
            return Decision.Accept(answered);
        }

        // An accept checks everything again now (D-523): the run still running and without a partner, both still allowed
        if (invitation.RunId is not { } runId || !state.Runs.TryGetValue(runId, out var run) || run.Status != RunStatus.Playing
            || state.Players[run.PlayerId].ActiveRunId != run.RunId || state.Players[run.PlayerId].IsInactive)
        {
            return Decision.Reject(RejectionCodes.CoopRunNotActive, "The run of the invitation is over.");
        }

        if (run.Coop is not null)
        {
            return Decision.Reject(RejectionCodes.CoopAlreadyJoined, "The run has a partner already.");
        }

        if (FinishLine.First(state) == run.PlayerId)
        {
            return Decision.Reject(RejectionCodes.CoopFirstFinisher, "The first finisher does not play with others (D-510).");
        }

        if (!IsCoopGame(context, run.GameId))
        {
            return Decision.Reject(RejectionCodes.CoopNotCoopGame, "Only a coop game takes a partner.");
        }

        // The partner is the invited one, or the responder of the partner board inviting the roller (D-524)
        var partnerId = invitation.FromPlayerId == run.PlayerId ? invitation.ToPlayerId : invitation.FromPlayerId;
        if (Partner(state, partnerId, run.PlayerId, run) is { } partnerRefusal)
        {
            return partnerRefusal;
        }

        var partner = state.Players[partnerId];
        if (partner.Phase != TurnPhase.Idle || partner.ActiveRunId is not null || partner.Offer is not null || partner.Choice is not null)
        {
            return Decision.Reject(RejectionCodes.CoopPartnerBusy, "The partner has a run, an offered game or a choice of his own (D-520).");
        }

        // A bet of the partner on the run would pay him for a run he plays (D-520): no joining over it
        if (partner.Wallet.Bets.Any(b => b.Status == Inventory.BetStatus.Open && b.RunId == run.RunId))
        {
            return Decision.Reject(RejectionCodes.CoopPartnerHasBet, "The partner has an open bet on this run.");
        }

        // The accepted invitation closes the other pending ones of the run (D-523)
        return Decision.Accept(
        [
            answered,
            new CoopJoined(run.RunId, partner.PlayerId, run.PlayerId, invitation.InteractionId, now),
            .. state.Interactions.Values
                .Where(i => i.Status == InteractionStatus.Pending && i.RunId == run.RunId && i.InteractionId != invitation.InteractionId)
                .Select(i => new InteractionExpired(i.InteractionId, InteractionExpiry.Superseded, now)),
        ]);
    }

    public static Decision Decide(SeasonState state, WithdrawInvitation command, EngineContext context)
    {
        if (Guard(state, command.PlayerId, context) is { } rejection)
        {
            return rejection;
        }

        if (Pending(state, command.InteractionId, context) is { } notPending)
        {
            return notPending;
        }

        return state.Interactions[command.InteractionId].FromPlayerId != command.PlayerId
            ? Decision.Reject(RejectionCodes.InteractionNotYours, "Only the inviter withdraws an invitation.")
            : Decision.Accept(new InteractionWithdrawn(command.InteractionId, command.PlayerId, context.Clock.UtcNow));
    }

    public static Decision Decide(SeasonState state, LeaveCoop command, EngineContext context)
    {
        if (Guard(state, command.PlayerId, context) is { } rejection)
        {
            return rejection;
        }

        var player = state.Players[command.PlayerId];
        return player.ActiveRunId is { } runId && state.Runs[runId].PartnerId == player.PlayerId
            ? Decision.Accept(new CoopLeft(runId, player.PlayerId, CoopLeaveReason.Left, context.Clock.UtcNow))
            : Decision.Reject(RejectionCodes.CoopNotPartner, "Only the partner of a running coop run leaves it.");
    }

    /// <summary>
    /// The split of a coop run's completion (D-521): <paramref name="total"/> points and steps and <paramref name="coins"/>
    /// by <c>reward.coop</c> now, the roller's share rounded up.
    /// </summary>
    public static CoopRewardSplit Split(SeasonState state, RunState run, CoopPartner partner, int total, int coins)
    {
        var rules = state.Rules.Reward.Coop;
        var coinsShare = rules.CoinsShare ?? throw new InvalidOperationException("reward.coop.coinsShare is required with coop on.");
        var (rollerPoints, partnerPoints) = CoopShares.Split(total, rules.PointsShare);
        var (rollerCoins, partnerCoins) = CoopShares.Split(coins, coinsShare);
        return new CoopRewardSplit(
            run.RunId, run.PlayerId, partner.PlayerId, total, rollerPoints, partnerPoints, coins, rollerCoins, partnerCoins, rules.PointsShare, coinsShare);
    }

    /// <summary>
    /// The partner's share of the completion (D-521, D-545), on <paramref name="state"/> after the roller's part: his points,
    /// his own move by his share (a finisher does not move, Q-3) with its stop, cells and finish, his coins. The frozen first
    /// gets nothing (D-99).
    /// </summary>
    public static IEnumerable<IGameEvent> PartnerShare(SeasonState state, Guid runId, CoopRewardSplit split, EngineContext context)
    {
        var partner = state.Players[split.PartnerId];
        if (Finishes.IsFrozen(partner))
        {
            return [];
        }

        var events = new List<IGameEvent>();
        if (split.PartnerPoints != 0)
        {
            events.Add(new PointsChanged(partner.PlayerId, split.PartnerPoints, PointsReason.CompletionRoll, runId));
        }

        PlayerMoved? moved = null;
        var walk = partner.Finish is null ? Movement.WalkOwn(state.Map, partner.CellId, split.PartnerPoints) : new Walk([], 0);
        if (walk.Path.Count > 0)
        {
            moved = new PlayerMoved(
                partner.PlayerId, partner.CellId, walk.Path[^1], split.PartnerPoints, [.. walk.Path], MoveReason.CompletionRoll, runId, walk.Paused);
            events.Add(moved);
        }

        if (split.PartnerCoins != 0)
        {
            events.Add(new CoinsChanged(partner.PlayerId, split.PartnerCoins, CoinsReason.CompletionReward, runId));
        }

        var after = events.Aggregate(state, SeasonEngine.Apply);
        events.AddRange(RunLifecycle.AfterOwnMove(after, after.Players[partner.PlayerId], after.Runs[runId], moved, walk, context));
        return events;
    }

    /// <summary>
    /// What follows <paramref name="e"/> (the state already holds it) with coop on (D-523): a run's end closes its pending
    /// invitations, the season's closing all of them (D-516).
    /// </summary>
    public static IEnumerable<IGameEvent> Settle(SeasonState state, IGameEvent e, DateTimeOffset now)
    {
        Func<Interaction, bool>? closes = e switch
        {
            RunCompleted c => i => i.RunId == c.RunId,
            RunDropped d => i => i.RunId == d.RunId,
            RunTechRerolled t => i => i.RunId == t.RunId,
            SeasonStatusChanged { To: SeasonStatus.Closing } => _ => true,
            _ => null,
        };
        if (closes is null)
        {
            return [];
        }

        var reason = e is SeasonStatusChanged ? InteractionExpiry.Deadline : InteractionExpiry.RunEnded;
        return [.. state.Interactions.Values
            .Where(i => i.Status == InteractionStatus.Pending && closes(i))
            .Select(i => new InteractionExpired(i.InteractionId, reason, now))];
    }

    /// <summary>The pending invitations past their time, closed by the timer (D-523); none while coop is off (D-420).</summary>
    public static IEnumerable<IGameEvent> Timeouts(SeasonState state, DateTimeOffset now) =>
        state.Interactions.Values
            .Where(i => i.Status == InteractionStatus.Pending && i.ExpiresAt <= now && InteractionRules.IsOn(state.Rules.Features, i.Kind))
            .Select(i => new InteractionExpired(i.InteractionId, InteractionExpiry.Timeout, now));

    /// <summary>The coop end a player's deactivation brings (the expert panel on D-520): he leaves as if by himself.</summary>
    public static IEnumerable<IGameEvent> OnInactive(SeasonState state, Guid playerId, DateTimeOffset now) =>
        state.Rules.Features.Coop && state.Players[playerId].ActiveRunId is { } runId && state.Runs[runId].PartnerId == playerId
            ? [new CoopLeft(runId, playerId, CoopLeaveReason.PartnerInactive, now)]
            : [];

    /// <summary>The coop end of the roller's drop or tech reroll of <paramref name="run"/> (D-522); none while coop is off.</summary>
    public static IEnumerable<IGameEvent> OnRunGivenUp(SeasonState state, RunState run, CoopLeaveReason reason, DateTimeOffset now) =>
        state.Rules.Features.Coop && run.Coop is { } coop ? [new CoopLeft(run.RunId, coop.PlayerId, reason, now)] : [];

    public static SeasonState Apply(SeasonState state, InteractionCreated e) =>
        state with { Interactions = state.Interactions.SetItem(e.Interaction.InteractionId, e.Interaction) };

    public static SeasonState Apply(SeasonState state, InteractionAnswered e) =>
        Close(state, e.InteractionId, e.Accepted ? InteractionStatus.Accepted : InteractionStatus.Declined);

    public static SeasonState Apply(SeasonState state, InteractionWithdrawn e) => Close(state, e.InteractionId, InteractionStatus.Withdrawn);

    public static SeasonState Apply(SeasonState state, InteractionExpired e) => Close(state, e.InteractionId, InteractionStatus.Expired);

    public static SeasonState Apply(SeasonState state, CoopJoined e)
    {
        var run = state.Runs[e.RunId] with { Coop = new CoopPartner(e.PlayerId, e.InteractionId, e.JoinedAt) };
        var partner = state.Players[e.PlayerId] with { Phase = TurnPhase.Playing, ActiveRunId = e.RunId };
        return state with { Runs = state.Runs.SetItem(e.RunId, run), Players = state.Players.SetItem(e.PlayerId, partner) };
    }

    public static SeasonState Apply(SeasonState state, CoopLeft e)
    {
        state = state with { Runs = state.Runs.SetItem(e.RunId, state.Runs[e.RunId] with { Coop = null }) };
        return Free(state, e.PlayerId, e.RunId);
    }

    // The shares are fixed with the split; the partner's side knows whether the run came after his finish (Q-3, D-545)
    public static SeasonState Apply(SeasonState state, CoopRewardSplit e)
    {
        var run = state.Runs[e.RunId];
        var coop = run.Coop ?? throw new InvalidOperationException($"Run {e.RunId} has no partner to split with.");
        run = run with
        {
            Coop = coop with
            {
                Shares = new CoopShareRules(e.PointsShare, e.CoinsShare),
                AfterFinish = state.Players[e.PartnerId].Finish is not null,
            },
        };
        return state with { Runs = state.Runs.SetItem(e.RunId, run) };
    }

    /// <summary>The partner is out of the run: Idle, without an active run, when it was his.</summary>
    public static SeasonState Free(SeasonState state, Guid partnerId, Guid runId) =>
        state.Players[partnerId].ActiveRunId == runId
            ? state with { Players = state.Players.SetItem(partnerId, state.Players[partnerId] with { Phase = TurnPhase.Idle, ActiveRunId = null }) }
            : state;

    private static SeasonState Close(SeasonState state, Guid id, InteractionStatus status) =>
        state with { Interactions = state.Interactions.SetItem(id, state.Interactions[id] with { Status = status }) };

    // The mechanic, a season running before its deadline, a known player
    internal static Decision? Guard(SeasonState state, Guid playerId, EngineContext context)
    {
        if (state.IsCreated && !state.Rules.Features.Coop)
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "Coop is off in this season.");
        }

        // A closing season is past its deadline: invitations are over (D-516)
        if (state.IsCreated && (state.Status == SeasonStatus.Closing || SeasonSetup.IsPastDeadline(state, context.Clock.UtcNow)))
        {
            return Decision.Reject(RejectionCodes.SeasonDeadlinePassed, "The deadline has passed: no more invitations this season.");
        }

        if (SeasonSetup.RequireActive(state) is { } inactive)
        {
            return inactive;
        }

        return state.Players.ContainsKey(playerId)
            ? null
            : Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {playerId} is not in the season.");
    }

    // A pending invitation, by the engine's clock: past its time it is over even before the timer fired (D-523)
    private static Decision? Pending(SeasonState state, Guid interactionId, EngineContext context) =>
        !state.Interactions.TryGetValue(interactionId, out var invitation)
            ? Decision.Reject(RejectionCodes.InteractionUnknown, $"Invitation {interactionId} is not in the season.")
            : !IsPending(invitation, context.Clock.UtcNow)
                ? Decision.Reject(RejectionCodes.InteractionNotPending, $"Invitation {interactionId} is {invitation.Status}.")
                : null;

    // Who may be the partner (D-520, D-546 (8)): another active player of the season, not the first finisher, for whom the
    // game is not excluded (his own drop, tech reroll, «уже проходил»)
    internal static Decision? Partner(SeasonState state, Guid partnerId, Guid rollerId, RunState run)
    {
        if (partnerId == rollerId || !state.Players.TryGetValue(partnerId, out var partner) || partner.IsInactive)
        {
            return Decision.Reject(RejectionCodes.CoopPartnerInvalid, "The partner is another active player of the season.");
        }

        if (FinishLine.First(state) == partnerId)
        {
            return Decision.Reject(RejectionCodes.CoopFirstFinisher, "The first finisher does not play with others (D-510).");
        }

        return partner.Exclusions.Any(x => x.GameId == run.GameId)
            ? Decision.Reject(RejectionCodes.CoopGameExcluded, "The game is excluded for the partner.")
            : null;
    }

    internal static bool IsPending(Interaction invitation, DateTimeOffset now) =>
        invitation.Status == InteractionStatus.Pending && invitation.ExpiresAt > now;

    internal static bool IsCoopGame(EngineContext context, Guid gameId) => context.Pool.Games.Any(g => g.Id == gameId && g.IsCoop);
}

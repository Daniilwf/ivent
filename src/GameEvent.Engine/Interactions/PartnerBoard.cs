using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Interactions;

/// <summary>Where a request of the partner board stands (GLOSSARY «Доска „ищу напарника“»).</summary>
public enum PartnerRequestStatus
{
    Open,
    Closed,
}

/// <summary>Why a request of the partner board closed (D-524).</summary>
public enum PartnerRequestCloseReason
{
    /// <summary>A partner joined its run: by a response or by a direct invitation.</summary>
    Coop,

    /// <summary>Its run ended: completed, dropped or tech-rerolled.</summary>
    RunEnded,

    /// <summary>Its term <c>partnerBoard.requestTtlHours</c> ran out (the timer).</summary>
    Timeout,

    /// <summary>Its author closed it.</summary>
    Author,

    /// <summary>The season went to «Подведение итогов» (D-516).</summary>
    Deadline,
}

/// <summary>
/// A request «ищу напарника» (D-524): <see cref="PlayerId"/> looks for a partner to his running coop run <see cref="RunId"/>
/// until <see cref="ExpiresAt"/>.
/// </summary>
public sealed record PartnerRequest(
    Guid RequestId,
    Guid PlayerId,
    Guid RunId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    PartnerRequestStatus Status = PartnerRequestStatus.Open);

/// <summary>The roller of a running coop run without a partner posts a request on the board (D-524).</summary>
public sealed record PostPartnerRequest(Guid PlayerId) : ICommand;

/// <summary>
/// A player responds to a request: a coop invitation from him to its author, who accepts or declines it (D-524, D-545).
/// </summary>
public sealed record RespondToPartnerRequest(Guid PlayerId, Guid RequestId) : ICommand;

/// <summary>The author closes his open request (D-524).</summary>
public sealed record ClosePartnerRequest(Guid PlayerId, Guid RequestId) : ICommand;

/// <summary>A request was posted on the board.</summary>
[EventType("partner-request-posted")]
public sealed record PartnerRequestPosted(PartnerRequest Request) : IGameEvent;

/// <summary>A request closed for <see cref="Reason"/>.</summary>
[EventType("partner-request-closed")]
public sealed record PartnerRequestClosed(Guid RequestId, PartnerRequestCloseReason Reason, DateTimeOffset ClosedAt) : IGameEvent;

/// <summary>
/// The partner board (SPEC «Взаимодействие игроков», D-524, D-545): the roller of a running coop run posts a request; a
/// response is a coop invitation from the responder to the author (an exception from «приглашает ролливший», D-520), so both
/// agree and the author stays the roller. Several responses may wait on one request; accepting one supersedes the others
/// (D-712). A request closes by a coop on its run, the run's end, its term, its author or the deadline. Everything is refused
/// while <c>features.partnerBoard</c> or <c>features.coop</c> is off, and what it left waits (D-420).
/// </summary>
internal static class PartnerBoard
{
    public static Decision Decide(SeasonState state, PostPartnerRequest command, EngineContext context)
    {
        if (Guard(state, command.PlayerId, context) is { } rejection)
        {
            return rejection;
        }

        var author = state.Players[command.PlayerId];
        if (author.ActiveRunId is not { } runId || !state.Runs.TryGetValue(runId, out var run) || run.Status != RunStatus.Playing)
        {
            return Decision.Reject(RejectionCodes.CoopRunNotActive, "Only a player playing a run asks for a partner to it.");
        }

        if (run.PlayerId != author.PlayerId)
        {
            return Decision.Reject(RejectionCodes.CoopRollerOnly, "Only the roller of a coop run asks for a partner to it.");
        }

        if (FinishLine.First(state) == author.PlayerId)
        {
            return Decision.Reject(RejectionCodes.CoopFirstFinisher, "The first finisher does not play with others (D-510).");
        }

        if (!Coop.IsCoopGame(context, run.GameId))
        {
            return Decision.Reject(RejectionCodes.CoopNotCoopGame, "Only a coop game takes a partner.");
        }

        if (run.Coop is not null)
        {
            return Decision.Reject(RejectionCodes.CoopAlreadyJoined, "The run has a partner already.");
        }

        var now = context.Clock.UtcNow;
        if (state.PartnerRequests.Values.Any(r => IsOpen(r, now) && r.RunId == run.RunId))
        {
            return Decision.Reject(RejectionCodes.PartnerRequestAlreadyOpen, "The run has an open request already.");
        }

        var ttl = state.Rules.PartnerBoard?.RequestTtlHours
            ?? throw new InvalidOperationException("partnerBoard.requestTtlHours is required with the partner board on.");
        return Decision.Accept(new PartnerRequestPosted(
            new PartnerRequest(context.Ids.NewId(), author.PlayerId, run.RunId, now, now.AddHours(ttl))));
    }

    public static Decision Decide(SeasonState state, RespondToPartnerRequest command, EngineContext context)
    {
        if (Guard(state, command.PlayerId, context) is { } rejection)
        {
            return rejection;
        }

        if (Open(state, command.RequestId, context) is { } notOpen)
        {
            return notOpen;
        }

        var request = state.PartnerRequests[command.RequestId];
        if (request.PlayerId == command.PlayerId)
        {
            return Decision.Reject(RejectionCodes.PartnerRequestOwn, "The author does not respond to his own request.");
        }

        var now = context.Clock.UtcNow;
        if (state.Interactions.Values.Any(i => Coop.IsPending(i, now) && i.Kind == InteractionKind.Coop
            && i.FromPlayerId == command.PlayerId && i.RunId == request.RunId))
        {
            return Decision.Reject(RejectionCodes.PartnerRequestAlreadyResponded, "The player has a pending response to this request.");
        }

        // A request left open while the board was off may name a run that ended or took a partner meanwhile (D-420)
        var run = state.Runs[request.RunId];
        if (run.Status != RunStatus.Playing || state.Players[run.PlayerId].ActiveRunId != run.RunId)
        {
            return Decision.Reject(RejectionCodes.CoopRunNotActive, "The run of the request is over.");
        }

        if (run.Coop is not null)
        {
            return Decision.Reject(RejectionCodes.CoopAlreadyJoined, "The run has a partner already.");
        }

        // The response is the invitation the author answers: what an accept would check, checked now too (D-712)
        if (Coop.Partner(state, command.PlayerId, request.PlayerId, run) is { } partnerRefusal)
        {
            return partnerRefusal;
        }

        var responder = state.Players[command.PlayerId];
        if (responder.Phase != TurnPhase.Idle || responder.ActiveRunId is not null || responder.Offer is not null || responder.Choice is not null)
        {
            return Decision.Reject(RejectionCodes.CoopPartnerBusy, "The responder has a run, an offered game or a choice of his own (D-520).");
        }

        if (responder.Wallet.Bets.Any(b => b.Status == Inventory.BetStatus.Open && b.RunId == run.RunId))
        {
            return Decision.Reject(RejectionCodes.CoopPartnerHasBet, "The responder has an open bet on this run.");
        }

        var limit = state.Rules.Interactions.MaxOpenInvitesPerPlayer
            ?? throw new InvalidOperationException("interactions.maxOpenInvitesPerPlayer is required with coop on.");
        if (state.Interactions.Values.Count(i => Coop.IsPending(i, now) && i.FromPlayerId == responder.PlayerId) >= limit)
        {
            return Decision.Reject(RejectionCodes.InteractionTooMany, $"At most {limit} pending invitations sent.");
        }

        return Decision.Accept(new InteractionCreated(new Interaction(
            context.Ids.NewId(),
            InteractionKind.Coop,
            responder.PlayerId,
            request.PlayerId,
            run.RunId,
            now,
            now.AddHours(state.Rules.Interactions.InviteTtlHours))));
    }

    public static Decision Decide(SeasonState state, ClosePartnerRequest command, EngineContext context)
    {
        if (Guard(state, command.PlayerId, context) is { } rejection)
        {
            return rejection;
        }

        if (Open(state, command.RequestId, context) is { } notOpen)
        {
            return notOpen;
        }

        return state.PartnerRequests[command.RequestId].PlayerId != command.PlayerId
            ? Decision.Reject(RejectionCodes.PartnerRequestNotAuthor, "Only the author closes his request.")
            : Decision.Accept(new PartnerRequestClosed(command.RequestId, PartnerRequestCloseReason.Author, context.Clock.UtcNow));
    }

    /// <summary>
    /// What follows <paramref name="e"/> (the state already holds it) with the board on (D-524): a coop on a run or its end
    /// closes its open request, the season's closing all of them (D-516).
    /// </summary>
    public static IEnumerable<IGameEvent> Settle(SeasonState state, IGameEvent e, DateTimeOffset now)
    {
        (Func<PartnerRequest, bool> Closes, PartnerRequestCloseReason Reason)? closing = e switch
        {
            CoopJoined j => (r => r.RunId == j.RunId, PartnerRequestCloseReason.Coop),
            RunCompleted c => (r => r.RunId == c.RunId, PartnerRequestCloseReason.RunEnded),
            RunDropped d => (r => r.RunId == d.RunId, PartnerRequestCloseReason.RunEnded),
            RunTechRerolled t => (r => r.RunId == t.RunId, PartnerRequestCloseReason.RunEnded),
            SeasonStatusChanged { To: SeasonStatus.Closing } => (_ => true, PartnerRequestCloseReason.Deadline),
            _ => null,
        };
        return closing is not { } close
            ? []
            : [.. state.PartnerRequests.Values
                .Where(r => r.Status == PartnerRequestStatus.Open && close.Closes(r))
                .Select(r => new PartnerRequestClosed(r.RequestId, close.Reason, now))];
    }

    /// <summary>The open requests past their term, closed by the timer; none while the board is off (D-420).</summary>
    public static IEnumerable<IGameEvent> Timeouts(SeasonState state, DateTimeOffset now) =>
        IsOn(state.Rules.Features)
            ? state.PartnerRequests.Values
                .Where(r => r.Status == PartnerRequestStatus.Open && r.ExpiresAt <= now)
                .Select(r => new PartnerRequestClosed(r.RequestId, PartnerRequestCloseReason.Timeout, now))
            : [];

    /// <summary>The board plays with both flags on (D-524).</summary>
    public static bool IsOn(Rulesets.Features features) => features.PartnerBoard && features.Coop;

    public static SeasonState Apply(SeasonState state, PartnerRequestPosted e) =>
        state with { PartnerRequests = state.PartnerRequests.SetItem(e.Request.RequestId, e.Request) };

    public static SeasonState Apply(SeasonState state, PartnerRequestClosed e) =>
        state with
        {
            PartnerRequests = state.PartnerRequests.SetItem(
                e.RequestId, state.PartnerRequests[e.RequestId] with { Status = PartnerRequestStatus.Closed }),
        };

    // The board on with coop (a config may not say otherwise, but a season's ruleset is checked once), then coop's own guard
    private static Decision? Guard(SeasonState state, Guid playerId, EngineContext context) =>
        state.IsCreated && !IsOn(state.Rules.Features)
            ? Decision.Reject(RejectionCodes.FeatureDisabled, "The partner board is off in this season.")
            : Coop.Guard(state, playerId, context);

    // An open request, by the engine's clock: past its term it is closed even before the timer fired (as invitations, D-523)
    private static Decision? Open(SeasonState state, Guid requestId, EngineContext context) =>
        !state.PartnerRequests.TryGetValue(requestId, out var request)
            ? Decision.Reject(RejectionCodes.PartnerRequestUnknown, $"Request {requestId} is not on the board.")
            : !IsOpen(request, context.Clock.UtcNow)
                ? Decision.Reject(RejectionCodes.PartnerRequestNotOpen, $"Request {requestId} is closed.")
                : null;

    private static bool IsOpen(PartnerRequest request, DateTimeOffset now) =>
        request.Status == PartnerRequestStatus.Open && request.ExpiresAt > now;
}

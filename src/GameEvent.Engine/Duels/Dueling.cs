using GameEvent.Engine.Finish;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Duels;

/// <summary>
/// Duels (D-525, the expert panel, D-540, D-730…D-739). A challenge is an invitation of kind Duel (D-718); its accept
/// starts a race of <c>duels.durationDays</c>. The race is decided by a calculation after the command (D-413, not an
/// effect): the first completion of a run that counts wins coins from the system; a drop of such a run forfeits, its tech
/// reroll calls the duel off; the time, the deadline and a reject of the winner's proof end it as a draw. The first
/// finisher plays out of the race (D-510). Everything is refused while <c>features.duels</c> is off, and what it left waits
/// (D-420).
/// </summary>
internal static class Dueling
{
    public static Decision Decide(SeasonState state, ChallengeToDuel command, EngineContext context)
    {
        if (Guard(state, command.PlayerId, context) is { } refused)
        {
            return refused;
        }

        var now = context.Clock.UtcNow;
        var rules = Rules(state);
        if (command.OpponentId == command.PlayerId)
        {
            return Decision.Reject(RejectionCodes.DuelOnSelf, "A duel is with another player.");
        }

        if (!state.Players.ContainsKey(command.OpponentId))
        {
            return Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {command.OpponentId} is not in the season.");
        }

        if (Players(state, command.PlayerId, command.OpponentId) is { } playersRefused)
        {
            return playersRefused;
        }

        if (PendingChallenges(state, now).Any(i => Pair(i.FromPlayerId, i.ToPlayerId, command.PlayerId, command.OpponentId))
            || state.Duels.Values.Any(d => d.Status == DuelStatus.Active && Pair(d.ChallengerId, d.OpponentId, command.PlayerId, command.OpponentId)))
        {
            return Decision.Reject(RejectionCodes.DuelAlreadyOpen, "The two have a challenge waiting or a duel going on already.");
        }

        // The invitations' own limit counts the pending ones of every kind (D-523, D-718)
        var invites = state.Rules.Interactions.MaxOpenInvitesPerPlayer
            ?? throw new InvalidOperationException("interactions.maxOpenInvitesPerPlayer is required with duels on.");
        if (state.Interactions.Values.Count(i => Coop.IsWaiting(state, i, now) && i.FromPlayerId == command.PlayerId) >= invites)
        {
            return Decision.Reject(RejectionCodes.InteractionTooMany, $"At most {invites} pending invitations sent.");
        }

        if (Limits(state, rules, now, command.PlayerId, command.OpponentId, except: null) is { } limited)
        {
            return limited;
        }

        // The cooldown runs from the end of the pair's last duel, whoever challenged (the expert panel (3), D-732)
        if (state.Duels.Values.Any(d => Pair(d.ChallengerId, d.OpponentId, command.PlayerId, command.OpponentId)
            && d.EndedAt is { } ended && now < ended.AddDays(rules.SamePairCooldownDays)))
        {
            return Decision.Reject(RejectionCodes.DuelCooldown, $"The two dueled less than {rules.SamePairCooldownDays} days ago.");
        }

        return Decision.Accept(new InteractionCreated(new Interaction(
            context.Ids.NewId(),
            InteractionKind.Duel,
            command.PlayerId,
            command.OpponentId,
            RunId: null,
            now,
            now.AddHours(state.Rules.Interactions.InviteTtlHours))));
    }

    /// <summary>The account of the player was deleted: he leaves every race and challenge (D-734).</summary>
    public static Decision Decide(SeasonState state, CancelDuelsOfDeletedAccount command, EngineContext context)
    {
        if (SeasonSetup.RequireActive(state) is { } inactive)
        {
            return inactive;
        }

        if (!state.Players.ContainsKey(command.PlayerId))
        {
            return Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {command.PlayerId} is not in the season.");
        }

        var events = Leave(state, command.PlayerId, InteractionExpiry.AccountDeleted, DuelEnd.AccountDeleted, context.Clock.UtcNow).ToList();
        return events.Count == 0
            ? Decision.Reject(RejectionCodes.DuelNothingToCancel, "The player has no duel going on and no challenge waiting.")
            : Decision.Accept(events);
    }

    /// <summary>The challenged player answers a duel's challenge; an accept checks everything again now (D-523) and starts the race.</summary>
    public static Decision Decide(SeasonState state, AnswerInvitation command, EngineContext context)
    {
        if (Guard(state, command.PlayerId, context) is { } refused)
        {
            return refused;
        }

        var now = context.Clock.UtcNow;
        if (!state.Interactions.TryGetValue(command.InteractionId, out var challenge))
        {
            return Decision.Reject(RejectionCodes.InteractionUnknown, $"Invitation {command.InteractionId} is not in the season.");
        }

        if (!IsPending(challenge, now))
        {
            return Decision.Reject(RejectionCodes.InteractionNotPending, $"Challenge {challenge.InteractionId} is {challenge.Status}.");
        }

        if (challenge.ToPlayerId != command.PlayerId)
        {
            return Decision.Reject(RejectionCodes.InteractionNotYours, "Only the challenged player answers.");
        }

        var answered = new InteractionAnswered(challenge.InteractionId, command.PlayerId, command.Accept, now);
        if (!command.Accept)
        {
            return Decision.Accept(answered);
        }

        // The answering player's own limit is «too many», the challenger's «busy» (D-732)
        var rules = Rules(state);
        if ((Players(state, challenge.FromPlayerId, challenge.ToPlayerId)
            ?? Limits(state, rules, now, challenge.ToPlayerId, challenge.FromPlayerId, challenge.InteractionId)) is { } changed)
        {
            return changed;
        }

        // The runs the two play now never win this duel (the expert panel (2), D-731)
        EquatableArray<Guid> playing =
        [
            .. new[] { challenge.FromPlayerId, challenge.ToPlayerId }
                .Select(id => state.Players[id].ActiveRunId)
                .OfType<Guid>()
                .Order(),
        ];
        return Decision.Accept(
            answered,
            new DuelStarted(challenge.InteractionId, challenge.FromPlayerId, challenge.ToPlayerId, now, now.AddDays(rules.DurationDays), playing));
    }

    /// <summary>The challenger takes back his challenge while it waits (D-523).</summary>
    public static Decision Decide(SeasonState state, WithdrawInvitation command, EngineContext context)
    {
        if (Guard(state, command.PlayerId, context) is { } refused)
        {
            return refused;
        }

        var now = context.Clock.UtcNow;
        if (!state.Interactions.TryGetValue(command.InteractionId, out var challenge))
        {
            return Decision.Reject(RejectionCodes.InteractionUnknown, $"Invitation {command.InteractionId} is not in the season.");
        }

        if (!IsPending(challenge, now))
        {
            return Decision.Reject(RejectionCodes.InteractionNotPending, $"Challenge {challenge.InteractionId} is {challenge.Status}.");
        }

        return challenge.FromPlayerId != command.PlayerId
            ? Decision.Reject(RejectionCodes.InteractionNotYours, "Only the challenger withdraws a challenge.")
            : Decision.Accept(new InteractionWithdrawn(challenge.InteractionId, command.PlayerId, now));
    }

    /// <summary>
    /// Whether the answer or withdrawal of <paramref name="interactionId"/> follows the duel's rules: a duel's challenge, or an
    /// unknown invitation while duels are on and coop is off (so it is refused as unknown, not as coop switched off).
    /// </summary>
    public static bool IsDuel(SeasonState state, Guid interactionId) =>
        state.Interactions.TryGetValue(interactionId, out var invitation)
            ? invitation.Kind == InteractionKind.Duel
            : state.Ruleset is { Features: { Duels: true, Coop: false } };

    /// <summary>What <paramref name="e"/> (the state already holds it) settles of the duels, with duels on.</summary>
    public static IEnumerable<IGameEvent> Settle(SeasonState state, IGameEvent e, DateTimeOffset now)
    {
        var rules = Rules(state);
        switch (e)
        {
            case RunCompleted completed:
                {
                    // The queue's order decides: the first completion wins, a later one finds the duel over (the expert panel (1))
                    var run = state.Runs[completed.RunId];
                    var wins = Wins(state, completed.PlayerId);
                    foreach (var duel in Active(state, completed.PlayerId).Where(d => completed.CompletedAt < d.ExpiresAt && Counts(rules, d, run)))
                    {
                        var coins = Coins(state, rules, completed.PlayerId, wins++);
                        foreach (var next in Win(duel, DuelEnd.Completed, completed.PlayerId, run.RunId, coins, completed.CompletedAt))
                        {
                            yield return next;
                        }
                    }

                    break;
                }

            case RunDropped dropped when rules.DropForfeits:
                // A voluntary drop of a run that counts gives the duel to the other player at once (the expert panel (4))
                foreach (var next in Forfeit(state, rules, dropped.PlayerId, dropped.RunId, dropped.DroppedAt))
                {
                    yield return next;
                }

                break;
            case RunTechRerolled { Reason: TechRerollReason.Wish } wished when rules.DropForfeits:
                // A wish reroll is the player's own choice, not a technical trouble: it forfeits like a drop (D-733)
                foreach (var next in Forfeit(state, rules, wished.PlayerId, wished.RunId, wished.RerolledAt))
                {
                    yield return next;
                }

                break;
            case RunTechRerolled { Reason: not TechRerollReason.Wish } rerolled:
                {
                    // Not the player's fault: the duel is called off, nobody gets anything
                    var run = state.Runs[rerolled.RunId];
                    foreach (var duel in Active(state, rerolled.PlayerId).Where(d => rerolled.RerolledAt < d.ExpiresAt && Counts(rules, d, run)))
                    {
                        yield return new DuelSettled(duel.DuelId, DuelStatus.Cancelled, DuelEnd.TechReroll, null, null, 0, rerolled.RerolledAt);
                    }

                    break;
                }

            case ProofRejected rejected:
                // The win and its coins go back; the duel is a draw, the win does not pass on (the expert panel (5), D-414)
                foreach (var duel in state.Duels.Values.Where(d => d is { Status: DuelStatus.Won, End: DuelEnd.Completed } && d.RunId == rejected.RunId))
                {
                    var winner = duel.WinnerId!.Value;
                    var coins = Finishes.IsFrozen(state.Players[winner]) ? 0 : duel.Coins;
                    yield return new DuelWinRevoked(duel.DuelId, winner, rejected.RunId, coins);
                    if (coins != 0)
                    {
                        yield return new CoinsChanged(winner, -coins, CoinsReason.DuelRevoked, rejected.RunId);
                    }
                }

                break;
            case Players.PlayerInactivitySet { IsInactive: true } inactive:
                foreach (var next in Leave(state, inactive.PlayerId, InteractionExpiry.Inactive, DuelEnd.Inactive, now))
                {
                    yield return next;
                }

                break;
            case PlayerFinished or PlayerFinishRevoked or PlayerFrozen when FinishLine.First(state) is { } first:
                // Whoever is the first finisher now plays out of the race (D-510): a finish, or a revoke of the one before him
                foreach (var next in Leave(state, first, InteractionExpiry.FirstFinisher, DuelEnd.FirstFinisher, now))
                {
                    yield return next;
                }

                break;
            case SeasonStatusChanged { To: SeasonStatus.Closing }:
                // After the deadline no run is completed any more (D-516)
                foreach (var challenge in state.Interactions.Values.Where(i => i is { Kind: InteractionKind.Duel, Status: InteractionStatus.Pending }))
                {
                    yield return new InteractionExpired(challenge.InteractionId, InteractionExpiry.Deadline, now);
                }

                foreach (var duel in state.Duels.Values.Where(d => d.Status == DuelStatus.Active))
                {
                    yield return new DuelSettled(duel.DuelId, DuelStatus.Drawn, DuelEnd.Deadline, null, null, 0, now);
                }

                break;
            default:
                break;
        }
    }

    /// <summary>The races past their end, drawn by the timer (the challenges burn by the invitations' timer, D-718).</summary>
    public static IEnumerable<IGameEvent> Timeouts(SeasonState state, DateTimeOffset now) =>
        state.Duels.Values
            .Where(d => d.Status == DuelStatus.Active && d.ExpiresAt <= now)
            .Select(d => new DuelSettled(d.DuelId, DuelStatus.Drawn, DuelEnd.TimeOut, null, null, 0, now));

    /// <summary>The earliest end of a race <paramref name="playerId"/> challenged to: on his timer column; null while duels are off (D-420).</summary>
    public static DateTimeOffset? Next(SeasonState state, Guid playerId) =>
        state.Ruleset is { Features.Duels: true }
            ? state.Duels.Values.Where(d => d.Status == DuelStatus.Active && d.ChallengerId == playerId).Select(d => (DateTimeOffset?)d.ExpiresAt).Min()
            : null;

    /// <summary>
    /// Whether switching duels off would strand something (D-420, D-730): a challenge waits, a race goes on, or a win stands on
    /// a run whose proof is not approved yet, so a reject could no longer take it back.
    /// </summary>
    public static bool InPlay(SeasonState state) =>
        state.Interactions.Values.Any(i => i is { Kind: InteractionKind.Duel, Status: InteractionStatus.Pending })
        || state.Duels.Values.Any(d => d.Status == DuelStatus.Active
            || (d is { Status: DuelStatus.Won, RunId: { } runId }
                && state.Runs.TryGetValue(runId, out var run) && run.Status == RunStatus.Completed && run.Proof?.Status is not ProofStatus.Approved));

    public static SeasonState Apply(SeasonState state, DuelStarted e) =>
        state with
        {
            Duels = state.Duels.Add(e.DuelId, new Duel(e.DuelId, e.ChallengerId, e.OpponentId, e.AcceptedAt, e.ExpiresAt, e.RunsAtAcceptance)),
        };

    public static SeasonState Apply(SeasonState state, DuelSettled e) =>
        Update(state, e.DuelId, d => d with
        {
            Status = e.Status,
            End = e.End,
            WinnerId = e.WinnerId,
            RunId = e.RunId,
            Coins = e.Coins,
            EndedAt = e.SettledAt,
        });

    // The win is gone: a draw, the winner and the run stay named for the log, the coins the duel holds are none (D-733)
    public static SeasonState Apply(SeasonState state, DuelWinRevoked e) =>
        Update(state, e.DuelId, d => d with { Status = DuelStatus.Drawn, End = DuelEnd.WinRejected, Coins = 0 });

    private static SeasonState Update(SeasonState state, Guid duelId, Func<Duel, Duel> change) =>
        state with { Duels = state.Duels.SetItem(duelId, change(state.Duels[duelId])) };

    private static IEnumerable<IGameEvent> Forfeit(SeasonState state, DuelRules rules, Guid loser, Guid runId, DateTimeOffset at)
    {
        var run = state.Runs[runId];
        foreach (var duel in Active(state, loser).Where(d => at < d.ExpiresAt && Counts(rules, d, run)))
        {
            var winner = duel.Other(loser);
            foreach (var next in Win(duel, DuelEnd.Forfeit, winner, runId: null, Coins(state, rules, winner, Wins(state, winner)), at))
            {
                yield return next;
            }
        }
    }

    private static IEnumerable<IGameEvent> Win(Duel duel, DuelEnd end, Guid winner, Guid? runId, int coins, DateTimeOffset at)
    {
        yield return new DuelSettled(duel.DuelId, DuelStatus.Won, end, winner, runId, coins, at);
        if (coins != 0)
        {
            yield return new CoinsChanged(winner, coins, CoinsReason.Duel, runId);
        }
    }

    // The first finisher wins without coins (D-414, D-422, the expert panel (6)); past the season's limit a win pays nothing
    private static int Coins(SeasonState state, DuelRules rules, Guid winner, int winsBefore) =>
        FinishLine.First(state) == winner || Finishes.IsFrozen(state.Players[winner]) || winsBefore >= rules.MaxWinsPerSeason
            ? 0
            : rules.RewardCoins;

    // A player leaving the race: his waiting challenges burn, his races are called off
    private static IEnumerable<IGameEvent> Leave(SeasonState state, Guid playerId, InteractionExpiry expiry, DuelEnd end, DateTimeOffset now)
    {
        foreach (var challenge in state.Interactions.Values.Where(i => i is { Kind: InteractionKind.Duel, Status: InteractionStatus.Pending }
            && (i.FromPlayerId == playerId || i.ToPlayerId == playerId)))
        {
            yield return new InteractionExpired(challenge.InteractionId, expiry, now);
        }

        foreach (var duel in Active(state, playerId))
        {
            yield return new DuelSettled(duel.DuelId, DuelStatus.Cancelled, end, null, null, 0, now);
        }
    }

    /// <summary>
    /// Whether <paramref name="run"/> races in <paramref name="duel"/>: a run of one of the two; with
    /// <c>duels.countRunsStartedAfterAccept</c> only one rolled after the acceptance and not played at it (D-731).
    /// </summary>
    private static bool Counts(DuelRules rules, Duel duel, RunState run) =>
        duel.Involves(run.PlayerId)
        && (!rules.CountRunsStartedAfterAccept || (run.RolledAt >= duel.AcceptedAt && !duel.RunsAtAcceptance.Contains(run.RunId)));

    private static IEnumerable<Duel> Active(SeasonState state, Guid playerId) =>
        state.Duels.Values.Where(d => d.Status == DuelStatus.Active && d.Involves(playerId));

    private static int Wins(SeasonState state, Guid playerId) =>
        state.Duels.Values.Count(d => d.Status == DuelStatus.Won && d.WinnerId == playerId);

    // A player's open duels: the challenges he sent that wait, and the races he is in (the expert panel (3)); challenges
    // sent to him do not count, so nobody is blocked by being challenged (D-732)
    private static int Open(SeasonState state, Guid playerId, DateTimeOffset now, Guid? except) =>
        PendingChallenges(state, now).Count(i => i.FromPlayerId == playerId && i.InteractionId != except)
        + Active(state, playerId).Count();

    private static IEnumerable<Interaction> PendingChallenges(SeasonState state, DateTimeOffset now) =>
        state.Interactions.Values.Where(i => i.Kind == InteractionKind.Duel && IsPending(i, now));

    // Pending by the engine's clock: past its time a challenge is over even before the timer fired (D-523)
    private static bool IsPending(Interaction invitation, DateTimeOffset now) =>
        invitation.Status == InteractionStatus.Pending && invitation.ExpiresAt > now;

    private static bool Pair(Guid a, Guid b, Guid x, Guid y) => (a == x && b == y) || (a == y && b == x);

    // Both still in the race: active, and neither is the first finisher (D-510)
    private static Decision? Players(SeasonState state, Guid challenger, Guid opponent)
    {
        if (state.Players[challenger].IsInactive || state.Players[opponent].IsInactive)
        {
            return Decision.Reject(RejectionCodes.DuelPlayerInactive, "An inactive player does not duel.");
        }

        return FinishLine.First(state) is { } first && (first == challenger || first == opponent)
            ? Decision.Reject(RejectionCodes.DuelFirstFinisher, "The first finisher plays out of the race: no duels (D-510).")
            : null;
    }

    // The open duels of both and their wins this season (the expert panel (3))
    private static Decision? Limits(SeasonState state, DuelRules rules, DateTimeOffset now, Guid challenger, Guid opponent, Guid? except)
    {
        if (Open(state, challenger, now, except) >= rules.MaxOpenPerPlayer)
        {
            return Decision.Reject(RejectionCodes.DuelTooMany, $"At most {rules.MaxOpenPerPlayer} open duels.");
        }

        if (Open(state, opponent, now, except) >= rules.MaxOpenPerPlayer)
        {
            return Decision.Reject(RejectionCodes.DuelOpponentBusy, $"The other player has {rules.MaxOpenPerPlayer} open duels.");
        }

        return Wins(state, challenger) >= rules.MaxWinsPerSeason || Wins(state, opponent) >= rules.MaxWinsPerSeason
            ? Decision.Reject(RejectionCodes.DuelWinLimit, $"{rules.MaxWinsPerSeason} duels won this season is the limit.")
            : null;
    }

    private static DuelRules Rules(SeasonState state) =>
        state.Rules.Duels ?? throw new InvalidOperationException("The validator requires the duels section with duels on.");

    // The mechanic, a season running before its deadline, a known player
    private static Decision? Guard(SeasonState state, Guid playerId, EngineContext context)
    {
        if (state.IsCreated && !state.Rules.Features.Duels)
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "Duels are off in this season.");
        }

        // A closing season is past its deadline: challenges are over (D-516)
        if (state.IsCreated && (state.Status == SeasonStatus.Closing || SeasonSetup.IsPastDeadline(state, context.Clock.UtcNow)))
        {
            return Decision.Reject(RejectionCodes.SeasonDeadlinePassed, "The deadline has passed: no more duels this season.");
        }

        if (SeasonSetup.RequireActive(state) is { } inactive)
        {
            return inactive;
        }

        return state.Players.ContainsKey(playerId)
            ? null
            : Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {playerId} is not in the season.");
    }
}

using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Runs;

/// <summary>
/// «Дроп»: give up the active run with a penalty (SPEC «Реролл, дроп, тех-реролл», D-09). Allowed any time while
/// Playing; <c>roll.minPlayMinutesBeforeDrop</c> is only a hint in the interface. The penalty dice take points and
/// position (<c>drop.affectsPoints</c>, <c>drop.affectsPosition</c>), never below the start; a mandatory bad event
/// follows (<c>drop.mandatoryEvent</c>); no coins. The game is excluded for the player and free for everyone else.
/// </summary>
/// <remarks><see cref="RunId"/> — the active run the player saw (D-600): another one active now refuses with <c>turn.stale</c>.</remarks>
public sealed record DropRun(Guid PlayerId, Guid? RunId = null) : ICommand;

/// <summary>Why a game does not start (D-11); <see cref="Other"/> needs a comment.</summary>
public enum TechRerollReason
{
    WeakPc,
    PaidUnavailable,
    DoesNotLaunch,
    EmulatorTooSlow,
    Other,

    /// <summary>
    /// «Реролл по желанию» (D-206, D-325): the game has a tag of <c>roll.wishRerollTags</c> that the roll did not impose;
    /// only within the window.
    /// </summary>
    Wish,
}

/// <summary>
/// «Тех-реролл»: give up the active run for a technical reason, free (D-11). The player may do it within
/// <c>roll.techRerollWindowHours</c> of the run's snapshot after the roll; later only the admin, on the player's behalf
/// (<see cref="ByAdmin"/>, set by the admin endpoint), with a mark and a comment in the log. The game is excluded for the player and a
/// new roll follows at once (SPEC: Playing → Rolling), with its own free rerolls (D-07).
/// </summary>
public sealed record TechReroll(Guid PlayerId, TechRerollReason Reason, string? Comment, bool ByAdmin = false) : ICommand;

/// <summary>
/// The admin turns a tech reroll into a drop (D-11): the drop penalty and the bad event hit the player's current points
/// and position; the exclusion's reason becomes a drop. Allowed until the season is finished.
/// </summary>
public sealed record ConvertTechRerollToDrop(Guid RunId, string Comment) : ICommand;

/// <summary>The run was dropped; <see cref="PenaltyDice"/> are the penalty roll, each die separately.</summary>
[EventType("run-dropped")]
public sealed record RunDropped(Guid RunId, Guid PlayerId, EquatableArray<Die> PenaltyDice, DateTimeOffset DroppedAt) : IGameEvent;

[EventType("run-tech-rerolled")]
public sealed record RunTechRerolled(
    Guid RunId, Guid PlayerId, TechRerollReason Reason, string? Comment, bool ByAdmin, DateTimeOffset RerolledAt) : IGameEvent;

/// <summary>A tech reroll became a drop; the penalty events follow in the same command.</summary>
[EventType("tech-reroll-converted-to-drop")]
public sealed record TechRerollConvertedToDrop(
    Guid RunId, Guid PlayerId, string Comment, EquatableArray<Die> PenaltyDice, DateTimeOffset ConvertedAt) : IGameEvent;

internal static class Drops
{
    public static Decision Decide(SeasonState state, DropRun command, EngineContext context)
    {
        if (TurnRules.Check(state, command.PlayerId, command, context.Clock.UtcNow) is { } rejection)
        {
            return rejection;
        }

        var player = state.Players[command.PlayerId];
        var run = ActiveRun(state, player);
        if (command.RunId is { } seen && seen != run.RunId)
        {
            return Decision.Reject(RejectionCodes.TurnStale, "The player saw another active run.");
        }

        var dice = PenaltyRoll(state, run, context);
        var dropped = new RunDropped(run.RunId, player.PlayerId, dice, context.Clock.UtcNow);

        // The coop ends first: the partner is free, the drop and its penalty are the roller's alone (D-522)
        var left = Interactions.Coop.OnRunGivenUp(state, run, Interactions.CoopLeaveReason.RollerDropped, context.Clock.UtcNow).ToList();

        // The penalty and its bad event play on the state after the drop: the run is over, the player idle (D-504)
        var after = Apply(left.Aggregate(state, SeasonEngine.Apply), dropped);
        return Decision.Accept([.. left, dropped, .. Penalty(after, after.Players[player.PlayerId], dice, run.RunId, context)]);
    }

    public static Decision Decide(SeasonState state, TechReroll command, EngineContext context)
    {
        if (TurnRules.Check(state, command.PlayerId, command, context.Clock.UtcNow) is { } rejection)
        {
            return rejection;
        }

        if (command.ByAdmin && string.IsNullOrWhiteSpace(command.Comment))
        {
            // Every admin change explains itself in the public log (D-89, D-94).
            return Decision.Reject(RejectionCodes.CommentRequired, "An admin tech reroll needs a comment.");
        }

        if (command.Reason == TechRerollReason.Other && string.IsNullOrWhiteSpace(command.Comment))
        {
            return Decision.Reject(RejectionCodes.ReasonCommentRequired, "The reason 'other' needs a comment.");
        }

        if (command.Comment?.Length > Limits.MaxCommentLength)
        {
            return Decision.Reject(RejectionCodes.CommentTooLong, $"The comment is limited to {Limits.MaxCommentLength} characters.");
        }

        var player = state.Players[command.PlayerId];
        var run = ActiveRun(state, player);
        var now = context.Clock.UtcNow;
        var window = TimeSpan.FromHours(run.Snapshot.TechRerollWindowHours);
        if ((!command.ByAdmin || command.Reason == TechRerollReason.Wish) && now - run.RolledAt > window)
        {
            // D-11: after the window only the admin, on the player's behalf; a wish reroll not at all (D-325).
            return Decision.Reject(
                RejectionCodes.TechRerollWindowClosed, $"The tech reroll window of {window.TotalHours} h after the roll has passed.");
        }

        if (command.Reason == TechRerollReason.Wish && WishRerolls.Refusal(run, context.Pool) is { } refusal)
        {
            return refusal;
        }

        var rerolled = new RunTechRerolled(run.RunId, player.PlayerId, command.Reason, command.Comment, command.ByAdmin, now);
        var excluded = new GameExcluded(player.PlayerId, run.GameId, ExclusionReason.TechRerolled);

        // The coop ends first: the partner is free without a penalty (D-522)
        var left = Interactions.Coop.OnRunGivenUp(state, run, Interactions.CoopLeaveReason.RollerTechRerolled, now).ToList();
        var after = Rolling.Apply(Apply(left.Aggregate(state, SeasonEngine.Apply), rerolled), excluded);

        // SPEC: Playing --> Rolling. The new roll is a roll of its own, with its own free rerolls (D-07, D-94) and the
        // special rolls and roll changes waiting for it (D-405).
        var (prepared, ready) = RollPreparation.BeforeRoll(after, player.PlayerId, context);
        return Rolling.Draw(ready, player.PlayerId, context, Rolling.Filters(ready, player.PlayerId)) is { } roll
            ? Decision.Accept([.. left, rerolled, excluded, .. prepared, roll])
            : Decision.Accept([.. left, rerolled, excluded]);
    }

    public static Decision Decide(SeasonState state, ConvertTechRerollToDrop command, EngineContext context)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "The season does not exist yet.");
        }

        if (SeasonSetup.IsOver(state))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: results are fixed.");
        }

        if (!state.Runs.TryGetValue(command.RunId, out var run))
        {
            return Decision.Reject(RejectionCodes.RunUnknown, $"Run {command.RunId} is not in the season.");
        }

        if (run.Status != RunStatus.TechRerolled)
        {
            return Decision.Reject(RejectionCodes.NotTechRerolled, $"Run {run.RunId} is {run.Status}, not a tech reroll.");
        }

        if (string.IsNullOrWhiteSpace(command.Comment))
        {
            return Decision.Reject(RejectionCodes.CommentRequired, "Every admin change explains itself in the public log.");
        }

        if (command.Comment.Length > Limits.MaxCommentLength)
        {
            return Decision.Reject(RejectionCodes.CommentTooLong, $"The comment is limited to {Limits.MaxCommentLength} characters.");
        }

        // The penalty hits the player's current points and position; their turn is left alone (D-11).
        var dice = PenaltyRoll(state, run, context);
        var converted = new TechRerollConvertedToDrop(run.RunId, run.PlayerId, command.Comment, dice, context.Clock.UtcNow);
        var after = Apply(state, converted);
        return Decision.Accept([converted, .. Penalty(after, after.Players[run.PlayerId], dice, run.RunId, context, exclude: false)]);
    }

    public static SeasonState Apply(SeasonState state, RunDropped e) => Finish(state, e.RunId, e.PlayerId, RunStatus.Dropped);

    // Not the player's fault: the run's conditions go back to wait for the new roll (Н-7, D-595)
    public static SeasonState Apply(SeasonState state, RunTechRerolled e) =>
        RunConditions.Return(Finish(state, e.RunId, e.PlayerId, RunStatus.TechRerolled), state.Runs[e.RunId]);

    public static SeasonState Apply(SeasonState state, TechRerollConvertedToDrop e)
    {
        var run = state.Runs[e.RunId];
        var player = state.Players[e.PlayerId];
        var exclusions = player.Exclusions
            .Select(x => x.GameId == run.GameId ? x with { Reason = ExclusionReason.Dropped } : x);
        return state with
        {
            Runs = state.Runs.SetItem(e.RunId, run with { Status = RunStatus.Dropped }),
            Players = state.Players.SetItem(e.PlayerId, player with { Exclusions = [.. exclusions] }),
        };
    }

    private static RunState ActiveRun(SeasonState state, SeasonPlayer player) =>
        state.Runs[player.ActiveRunId ?? throw new InvalidOperationException($"Player {player.PlayerId} is Playing without a run.")];

    // D-205: every drop in a row before this one adds its extra dice
    private static EquatableArray<Die> PenaltyRoll(SeasonState state, RunState run, EngineContext context) =>
        CompletionRoll.Roll(DropStreak.PenaltyDiceCount(state.Rules.Drop, DropStreak.Before(state, run)), state.Rules.Drop.PenaltyDice.Sides, context.Random);

    /// <summary>
    /// The drop penalty (D-09, D-94) on <paramref name="state"/>, which already holds the drop: points and position by the
    /// dice sum, never past the start; the stop the push ends on, an event cell included; the game excluded for a drop (a
    /// conversion changes the existing exclusion instead); then the mandatory bad event (D-504) — drawn from the bad deck
    /// with <c>features.events</c>, else a manual effect.
    /// </summary>
    internal static List<IGameEvent> Penalty(
        SeasonState state, SeasonPlayer player, EquatableArray<Die> dice, Guid runId, EngineContext context, bool exclude = true)
    {
        var rules = state.Rules.Drop;
        var sum = PenaltySum(dice, state.Runs[runId].Snapshot);
        var events = new List<IGameEvent>();

        // The frozen first loses nothing and is dealt no event; a finisher's position is fixed (D-09, D-99).
        if (Finishes.IsFrozen(player))
        {
            if (exclude)
            {
                events.Add(new GameExcluded(player.PlayerId, state.Runs[runId].GameId, ExclusionReason.Dropped));
            }

            return events;
        }

        if (rules.AffectsPoints && sum != 0)
        {
            events.Add(new PointsChanged(player.PlayerId, -sum, PointsReason.DropPenalty, runId));
        }

        if (rules.AffectsPosition && sum != 0 && player.Finish is null)
        {
            // A push back: not below a checkpoint (D-306); the cell it stops on triggers (D-303)
            var path = Movement.Backward(state.Map, player.Path, sum, checkpoints: true);
            if (path.Count > 0)
            {
                var moved = new PlayerMoved(player.PlayerId, player.CellId, path[^1], -sum, [.. path], MoveReason.DropPenalty, runId);
                events.Add(moved);
                events.AddRange(CellStops.After(state, moved, context));

                // An event cell of the stop draws before the drop's own event (D-504); a stop reacts to its move, so its
                // event runs at the chain's first level, as in the chain (D-500, D-559 (1))
                events.AddRange(Decks.EventCells.StopOfOwnMove(events.Aggregate(state, SeasonEngine.Apply), context, moved));
            }
        }

        if (exclude)
        {
            events.Add(new GameExcluded(player.PlayerId, state.Runs[runId].GameId, ExclusionReason.Dropped));
        }

        if (rules.MandatoryEvent == MandatoryEvent.Bad)
        {
            events.AddRange(Decks.EventDraws.Draw(
                events.Aggregate(state, SeasonEngine.Apply), context, player.PlayerId, Decks.EventDraws.NameOf(EventKind.Bad), Decks.EventSource.DropPenalty, runId));
        }

        return events;
    }

    /// <summary>
    /// The penalty in points and steps: the dice sum times the multiplier of the zone the run was rolled in, rounded to
    /// a whole number, halves away from zero (D-307).
    /// </summary>
    public static int PenaltySum(EquatableArray<Die> dice, RunSnapshot snapshot)
    {
        var sum = dice.Sum(d => d.Value);
        return snapshot.Zone?.DropPenaltyMultiplier is { } multiplier
            ? (int)Math.Round(sum * multiplier, MidpointRounding.AwayFromZero)
            : sum;
    }

    private static SeasonState Finish(SeasonState state, Guid runId, Guid playerId, RunStatus status) =>
        state with
        {
            Runs = state.Runs.SetItem(runId, state.Runs[runId] with { Status = status }),
            Players = state.Players.SetItem(playerId, state.Players[playerId] with { Phase = TurnPhase.Idle, ActiveRunId = null }),
        };
}

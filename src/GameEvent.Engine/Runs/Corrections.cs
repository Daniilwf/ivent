using GameEvent.Engine.Effects;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Runs;

/// <summary>
/// The admin corrects the hours of a completed run (SPEC «Каждый кубик хранится отдельно», D-14, D-97): the dice count by
/// the new hours; missing dice are rolled and added, extra dice are removed from the end; challenge dice stay. Points,
/// position and completion coins change by the difference.
/// </summary>
public sealed record CorrectRunHours(Guid RunId, decimal Hours, string Comment) : ICommand;

/// <summary>
/// The admin changes the difficulty of a completed run, usually by the proof (SPEC «Сложность засчитывается по пруфу»,
/// Q-5, D-97): every die, challenge dice too, becomes ⌈old × new sides / old sides⌉. Points and position change by the
/// difference; the difficulty's granted event follows the change.
/// </summary>
public sealed record ChangeRunDifficulty(Guid RunId, Difficulty Difficulty, string Comment) : ICommand;

/// <summary>One die before and after a correction (Q-5: the event keeps both).</summary>
public sealed record DieChange(Die Before, Die After);

/// <summary>
/// The hours of a run were corrected: <see cref="Added"/> dice were rolled and appended, the <see cref="Removed"/> dice
/// were taken off the end of the dice by hours (their values kept, like the dice of a difficulty change).
/// </summary>
[EventType("run-hours-corrected")]
public sealed record RunHoursCorrected(
    Guid RunId,
    Guid PlayerId,
    decimal OldHours,
    decimal NewHours,
    EquatableArray<Die> Added,
    EquatableArray<Die> Removed,
    string Comment,
    DateTimeOffset CorrectedAt) : IGameEvent;

/// <summary>The difficulty of a run changed; every die recalculated, both values kept (Q-5).</summary>
[EventType("run-difficulty-changed")]
public sealed record RunDifficultyChanged(
    Guid RunId,
    Guid PlayerId,
    Difficulty OldDifficulty,
    Difficulty NewDifficulty,
    EquatableArray<DieChange> Dice,
    EquatableArray<DieChange> ChallengeDice,
    string Comment,
    DateTimeOffset ChangedAt) : IGameEvent;

internal static class Corrections
{
    public static Decision Decide(SeasonState state, CorrectRunHours command, EngineContext context)
    {
        if (Guard(state, command.RunId, command.Comment) is { } rejection)
        {
            return rejection;
        }

        var run = state.Runs[command.RunId];
        var oldHours = run.Hours ?? throw new InvalidOperationException($"Completed run {run.RunId} has no hours.");
        if (command.Hours <= 0)
        {
            return Decision.Reject(RejectionCodes.InvalidHours, $"Hours must be positive, got {command.Hours}.");
        }

        if (command.Hours == oldHours)
        {
            return Decision.Reject(RejectionCodes.RunNothingToChange, $"Run {run.RunId} already has {oldHours} h.");
        }

        // The count stage only (D-14): missing dice are rolled and appended, extra ones leave from the end.
        var count = CompletionRoll.Count(command.Hours, run.Snapshot);
        var sides = run.Mods?.Sides ?? CompletionRoll.DieFor(run.Difficulty!.Value, run.Snapshot.DieByDifficulty).Sides;
        var added = count > run.Dice.Count ? CompletionRoll.Roll(count - run.Dice.Count, sides, context.Random) : [];
        EquatableArray<Die> removed = [.. run.Dice.Skip(count)];

        var corrected = new RunHoursCorrected(
            run.RunId, run.PlayerId, oldHours, command.Hours, added, removed, command.Comment, context.Clock.UtcNow);

        // By the run's total, so a zone's addition counts once and the total never goes below 0 (D-307)
        var delta = CompletionRoll.Total(SeasonEngine.Apply(state, corrected).Runs[run.RunId]) - CompletionRoll.Total(run);
        var coins = Finishes.IsFrozen(state.Players[run.PlayerId]) ? 0 : Coins(run.Snapshot, command.Hours) - Coins(run.Snapshot, oldHours);
        return Decision.Accept(
        [
            corrected,
            .. Difference(SeasonEngine.Apply(state, corrected), run.RunId, delta),
            .. coins != 0 ? [new CoinsChanged(run.PlayerId, coins, CoinsReason.RunCorrection, run.RunId)] : Array.Empty<IGameEvent>(),
        ]);
    }

    public static Decision Decide(SeasonState state, ChangeRunDifficulty command, EngineContext context)
    {
        if (Guard(state, command.RunId, command.Comment) is { } rejection)
        {
            return rejection;
        }

        if (!Enum.IsDefined(command.Difficulty))
        {
            return Decision.Reject(RejectionCodes.CommandInvalid, $"Unknown difficulty {command.Difficulty}.");
        }

        var run = state.Runs[command.RunId];
        var old = run.Difficulty ?? throw new InvalidOperationException($"Completed run {run.RunId} has no difficulty.");
        if (command.Difficulty == old)
        {
            return Decision.Reject(RejectionCodes.RunNothingToChange, $"Run {run.RunId} is already {old}.");
        }

        return Decision.Accept(DifficultyChange(state, run, command.Difficulty, command.Comment, context));
    }

    /// <summary>The events of a difficulty change (Q-5, D-97), also written by a proof approval at a lower difficulty (D-98).</summary>
    public static IReadOnlyList<IGameEvent> DifficultyChange(
        SeasonState state, RunState run, Difficulty difficulty, string comment, EngineContext context)
    {
        var old = run.Difficulty ?? throw new InvalidOperationException($"Completed run {run.RunId} has no difficulty.");

        // Q-5: each die ⌈old × new sides / old sides⌉; no randomness.
        var newRule = CompletionRoll.DieFor(difficulty, run.Snapshot.DieByDifficulty);
        EquatableArray<DieChange> Recalculate(EquatableArray<Die> dice) =>
            [.. dice.Select(d => new DieChange(d, new Die(newRule.Sides, (int)Math.Ceiling((decimal)d.Value * newRule.Sides / d.Sides))))];
        var dice = Recalculate(run.Dice);
        var challenge = Recalculate(run.ChallengeDice);
        var changed = new RunDifficultyChanged(
            run.RunId, run.PlayerId, old, difficulty, dice, challenge, comment, context.Clock.UtcNow);
        var delta = CompletionRoll.Total(SeasonEngine.Apply(state, changed).Runs[run.RunId]) - CompletionRoll.Total(run);
        var events = new List<IGameEvent> { changed };
        events.AddRange(Difference(SeasonEngine.Apply(state, changed), run.RunId, delta));
        var frozen = Finishes.IsFrozen(state.Players[run.PlayerId]);

        // The difficulty's own event follows the change: a pending one of the old difficulty is not applicable (one already
        // played out stays, the admin corrects by hand), the new difficulty grants its own (Q-5, D-97).
        events.AddRange(state.ManualEffects.Values
            .Where(e => e.RunId == run.RunId && e.Source == ManualEffectSource.Difficulty)
            .Select(e => new ManualEffectResolved(e.EffectId, e.PlayerId, e.RunId, ManualEffectOutcome.NotApplicable, comment)));

        // The frozen first gets no new event (the freeze amendment); the old one still does not apply.
        if (!frozen && newRule.GrantEvent is { } granted)
        {
            events.Add(new ManualEffectCreated(context.Ids.NewId(), run.PlayerId, granted, ManualEffectSource.Difficulty, run.RunId));
        }

        return events;
    }

    public static SeasonState Apply(SeasonState state, RunHoursCorrected e)
    {
        var run = state.Runs[e.RunId];
        EquatableArray<Die> dice = [.. run.Dice.Take(run.Dice.Count - e.Removed.Count), .. e.Added];
        return state with { Runs = state.Runs.SetItem(e.RunId, run with { Hours = e.NewHours, Dice = dice }) };
    }

    public static SeasonState Apply(SeasonState state, RunDifficultyChanged e)
    {
        var run = state.Runs[e.RunId] with
        {
            Difficulty = e.NewDifficulty,
            Dice = [.. e.Dice.Select(c => c.After)],
            ChallengeDice = [.. e.ChallengeDice.Select(c => c.After)],
        };
        return state with { Runs = state.Runs.SetItem(e.RunId, run) };
    }

    // Admin corrections of a completed run: a running or closing season, an explained change (D-97).
    private static Decision? Guard(SeasonState state, Guid runId, string comment)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "The season does not exist yet.");
        }

        if (state.Status is not (SeasonStatus.Active or SeasonStatus.Closing))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: results are fixed.");
        }

        if (!state.Runs.TryGetValue(runId, out var run))
        {
            return Decision.Reject(RejectionCodes.RunUnknown, $"Run {runId} is not in the season.");
        }

        if (run.Status != RunStatus.Completed)
        {
            return Decision.Reject(RejectionCodes.RunNotCompleted, $"Run {run.RunId} is {run.Status}.");
        }

        if (string.IsNullOrWhiteSpace(comment))
        {
            return Decision.Reject(RejectionCodes.CommentRequired, "Every admin change explains itself in the public log.");
        }

        return comment.Length > Limits.MaxCommentLength
            ? Decision.Reject(RejectionCodes.CommentTooLong, $"The comment is limited to {Limits.MaxCommentLength} characters.")
            : null;
    }

    // Points and position by the difference, from where the player stands now (D-97); also a change of the throw by an
    // item after the fact (D-408). state already holds the correction event, so the run carries its new dice.
    internal static IEnumerable<IGameEvent> Difference(
        SeasonState state, Guid runId, int delta, PointsReason pointsReason = PointsReason.RunCorrection, MoveReason moveReason = MoveReason.RunCorrection)
    {
        if (delta == 0)
        {
            yield break;
        }

        var run = state.Runs[runId];
        var player = state.Players[run.PlayerId];
        if (Finishes.IsFrozen(player))
        {
            yield break;
        }

        yield return new PointsChanged(player.PlayerId, delta, pointsReason, run.RunId);

        // A finisher's position is fixed; a change of a run up to the finish goes through the surplus (Q-3).
        if (player.Finish is not null)
        {
            if (Finishes.CountsForFinish(player, run))
            {
                var after = SeasonEngine.Apply(state, new PointsChanged(player.PlayerId, delta, pointsReason, run.RunId));
                var events = delta > 0
                    ? [new FinishSurplusChanged(player.PlayerId, delta)]
                    : Finishes.AfterReduction(
                        after, after.Players[player.PlayerId], run, Finishes.Reduction(player, run, -delta), moveReason);
                foreach (var e in events)
                {
                    yield return e;
                }
            }

            yield break;
        }

        // Forward: from where the player stands, extra steps burn at the finish. Back: only what the run really gave
        // beyond its new dice sum — steps that burned at the finish gave no cells to take back (D-47, D-97).
        var newSum = CompletionRoll.Total(run);
        var steps = delta > 0 ? delta : -Math.Max(0, run.Moved - newSum);
        var path = steps > 0 ? Movement.Forward(state.Map, player.CellId, steps) : Movement.Backward(state.Map, player.Path, -steps);

        // Only a run's own move reaches the finish; a correction stops a cell before it (RR8).
        if (steps > 0 && path.Count > 0 && state.Map.CellById(path[^1]).Type == CellType.Finish)
        {
            path = [.. path.Take(path.Count - 1)];
        }
        if (path.Count > 0)
        {
            yield return new PlayerMoved(player.PlayerId, player.CellId, path[^1], steps, [.. path], moveReason, run.RunId);
        }
    }

    private static int Coins(RunSnapshot snapshot, decimal hours) =>
        snapshot.Coins is { } reward ? RunLifecycle.CompletionCoins(reward, snapshot.DiceCount, hours) : 0;
}

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
        // An item's single die stays one die whatever the hours (D-481)
        var count = run.Mods?.SingleDie is not null ? 1 : CompletionRoll.Count(command.Hours, run.Snapshot);
        var sides = run.Mods?.SingleDie ?? run.Mods?.Sides ?? CompletionRoll.DieFor(run.Difficulty!.Value, run.Snapshot.DieByDifficulty).Sides;
        // Stryker disable once Equality : equivalent — with as many dice as wanted, rolling zero more adds none
        // A run without dice by its unmet condition throws none (D-592)
        var added = count > run.Dice.Count && !RunConditions.WithoutDice(run) ? CompletionRoll.Roll(count - run.Dice.Count, sides, context.Random) : [];
        EquatableArray<Die> removed = [.. run.Dice.Skip(count)];

        var corrected = new RunHoursCorrected(
            run.RunId, run.PlayerId, oldHours, command.Hours, added, removed, command.Comment, context.Clock.UtcNow);

        // By the run's total, so a zone's addition counts once and the total never goes below 0 (D-307); a coop run's new
        // total and coins split again by the shares of its completion (D-521)
        var events = new List<IGameEvent> { corrected };
        events.AddRange(Differences(SeasonEngine.Apply(state, corrected), run));
        foreach (var player in RunShares.Players(run))
        {
            var coins = Finishes.IsFrozen(state.Players[player])
                ? 0
                : RunShares.Coins(run, player, Coins(run.Snapshot, command.Hours)) - RunShares.Coins(run, player, Coins(run.Snapshot, oldHours));
            if (coins != 0)
            {
                events.Add(new CoinsChanged(player, coins, CoinsReason.RunCorrection, run.RunId));
            }
        }

        return Decision.Accept(events);
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

        // Dice whose sides an item set keep them (D-408): only the difficulty's event follows the change
        // and an item's single die stays that one die of its own kind (D-481); the challenge dice are not the item's
        var sides = run.Mods?.Sides ?? newRule.Sides;
        static EquatableArray<DieChange> Recalculate(EquatableArray<Die> dice, int sides) =>
            [.. dice.Select(d => new DieChange(d, new Die(sides, (int)Math.Ceiling((decimal)d.Value * sides / d.Sides))))];
        var dice = Recalculate(run.Dice, run.Mods?.SingleDie ?? sides);
        var challenge = Recalculate(run.ChallengeDice, sides);
        var changed = new RunDifficultyChanged(
            run.RunId, run.PlayerId, old, difficulty, dice, challenge, comment, context.Clock.UtcNow);
        var events = new List<IGameEvent> { changed };

        // The conditions are checked again at the new difficulty, both ways (D-507, D-546 (2)): one difference for both
        var afterChange = SeasonEngine.Apply(state, changed);
        if (RunConditions.Again(afterChange.Runs[run.RunId], context) is { } check)
        {
            events.Add(check);
            afterChange = SeasonEngine.Apply(afterChange, check);
        }

        events.AddRange(Differences(afterChange, run));
        var frozen = Finishes.IsFrozen(state.Players[run.PlayerId]);

        // The difficulty's own event follows the change: a pending one of the old difficulty is not applicable (one already
        // played out stays, the admin corrects by hand), the new difficulty grants its own (Q-5, D-97).
        events.AddRange(state.ManualEffects.Values
            .Where(e => e.RunId == run.RunId && e.Source == ManualEffectSource.Difficulty)
            .Select(e => new ManualEffectResolved(e.EffectId, e.PlayerId, e.RunId, ManualEffectOutcome.NotApplicable, comment)));

        // The frozen first gets no new event (the freeze amendment); the old one still does not apply. With events on it is
        // drawn now, as at the completion (D-504), but a run draws its difficulty's event once: one already drawn stays and
        // is not drawn again (Н-4, D-553, D-557)
        if (!frozen && state.Rules.Features.Events)
        {
            events.AddRange(Decks.EventDraws.ForDifficulty(events.Aggregate(state, SeasonEngine.Apply), context, run.RunId));
        }
        else if (!frozen && newRule.GrantEvent is { } granted && !RunConditions.WithoutDice(events.Aggregate(state, SeasonEngine.Apply).Runs[run.RunId]))
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
    internal static Decision? Guard(SeasonState state, Guid runId, string comment)
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

    /// <summary>
    /// The differences of every player the run gave something to (D-97, D-521): <paramref name="state"/> holds the change of
    /// the run's dice, <paramref name="before"/> is the run before it. Each player's share of the new total against his
    /// share of the old one, the roller's first.
    /// </summary>
    internal static List<IGameEvent> Differences(
        SeasonState state, RunState before, PointsReason pointsReason = PointsReason.RunCorrection, MoveReason moveReason = MoveReason.RunCorrection)
    {
        var events = new List<IGameEvent>();
        foreach (var player in RunShares.Players(before))
        {
            var delta = RunShares.Points(state.Runs[before.RunId], player) - RunShares.Points(before, player);
            var own = Difference(state, before.RunId, delta, player, pointsReason, moveReason).ToList();
            events.AddRange(own);
            state = own.Aggregate(state, SeasonEngine.Apply);
        }

        return events;
    }

    // Points and position by the difference, from where the player stands now (D-97); also a change of the throw by an
    // item after the fact (D-408). state already holds the correction event, so the run carries its new dice.
    private static IEnumerable<IGameEvent> Difference(
        SeasonState state, Guid runId, int delta, Guid playerId, PointsReason pointsReason, MoveReason moveReason)
    {
        if (delta == 0)
        {
            yield break;
        }

        // A coop partner's side of the run: his share, his cells (D-545)
        var run = state.Runs[runId].Side(playerId);
        var player = state.Players[playerId];
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
                // Stryker disable once Equality : equivalent — Difference never runs with a zero delta
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
        var newSum = RunShares.Points(state.Runs[runId], playerId);
        // Stryker disable once Equality : equivalent — Difference never runs with a zero delta
        var steps = delta > 0 ? delta : -Math.Max(0, run.Moved - newSum);
        // Only a run's own move reaches the finish: a correction is a push and stops a cell before it (RR8, D-322).
        // Stryker disable once Equality : equivalent — zero steps enter no cell either way
        var path = steps > 0 ? Movement.Push(state.Map, player.CellId, steps) : Movement.Backward(state.Map, player.Path, -steps);
        if (path.Count > 0)
        {
            yield return new PlayerMoved(player.PlayerId, player.CellId, path[^1], steps, [.. path], moveReason, run.RunId);
        }
    }

    private static int Coins(RunSnapshot snapshot, decimal hours) =>
        snapshot.Coins is { } reward ? RunLifecycle.CompletionCoins(reward, snapshot.DiceCount, hours) : 0;
}

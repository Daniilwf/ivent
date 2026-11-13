using GameEvent.Engine.Finish;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Runs;

/// <summary>
/// The admin marks the conditions of a completed run «не применимо» (D-546 (2), D-594): the run counts as if they were met,
/// and they go on to the player's next roll.
/// </summary>
public sealed record WaiveRunCondition(Guid RunId, string Comment) : ICommand;

/// <summary>
/// The conditions of a run were checked (D-507): at the completion, after an admin's change of its difficulty, or waived by
/// the admin (<see cref="Waived"/>). <see cref="Met"/> at <see cref="Difficulty"/>. When the check gives dice to a run that
/// had none by its condition, the thrown dice are <see cref="Dice"/> and <see cref="ChallengeDice"/> (written only then);
/// when it takes them, the run's dice go (the state before the event holds them).
/// </summary>
[EventType("run-condition-checked")]
public sealed record RunConditionChecked(
    Guid RunId,
    Guid PlayerId,
    Difficulty Difficulty,
    bool Met,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    EquatableArray<Die> Dice = default,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    EquatableArray<Die> ChallengeDice = default,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool Waived = false) : IGameEvent;

/// <summary>
/// Conditions of events on the next run (SPEC «Условия от ивентов», D-506, D-507): <c>modifyNextRoll.runCondition</c> waits
/// with the roll changes, the roll fixes it in the run's snapshot, the completion checks it by the difficulty that counts, and
/// an unmet one under <c>noDiceKeepCoins</c> leaves the run without dice but with its coins.
/// </summary>
internal static class RunConditions
{
    /// <summary>
    /// The roll's changes split (D-506): the conditions are taken in the order the changes apply; one that contradicts those
    /// taken before it (the highest «at least» above the lowest «at most») is left waiting for a later roll.
    /// </summary>
    /// <remarks>With <paramref name="events"/> off every condition waits: the mechanic is off, not its conditions lost (D-591).</remarks>
    public static (EquatableArray<RollModifier> Taken, EquatableArray<RollModifier> Postponed) Split(IEnumerable<RollModifier> modifiers, bool events)
    {
        var taken = new List<RollModifier>();
        var postponed = new List<RollModifier>();
        Difficulty? least = null;
        Difficulty? most = null;
        foreach (var modifier in modifiers)
        {
            if (modifier.RunCondition is not { } condition)
            {
                taken.Add(modifier);
                continue;
            }

            var newLeast = Max(least, condition.DifficultyAtLeast);
            var newMost = Min(most, condition.DifficultyAtMost);
            if (!events || newLeast > newMost)
            {
                postponed.Add(modifier);
                continue;
            }

            (least, most) = (newLeast, newMost);
            taken.Add(modifier);
        }

        // Nothing postponed stays unwritten: the rolls before conditions keep their format
        return ([.. taken], postponed.Count == 0 ? default : [.. postponed]);
    }

    /// <summary>The snapshot with the conditions of the roll in progress and the policy for them (D-507, D-546 (2)).</summary>
    public static RunSnapshot Fix(RunSnapshot snapshot, SeasonState state, Guid playerId)
    {
        if (!state.Rules.Features.Events || !state.Rules.Features.Items)
        {
            return snapshot;
        }

        EquatableArray<RunCondition> conditions = [.. state.Players[playerId].Wallet.CurrentRoll
            .Where(m => m.RunCondition is not null)
            .Select(m => new RunCondition(m.ObjectId, m.RunCondition!, m.Hostile))];
        return conditions.Count == 0
            ? snapshot
            : snapshot with { Conditions = conditions, ConditionPolicy = state.Rules.Reward.UnmetConditionPolicy };
    }

    /// <summary>Whether every condition holds at <paramref name="difficulty"/>.</summary>
    public static bool Hold(RunSnapshot snapshot, Difficulty difficulty) =>
        snapshot.Conditions.All(c =>
            !(difficulty < c.Condition.DifficultyAtLeast) && !(difficulty > c.Condition.DifficultyAtMost));

    /// <summary>A run its unmet condition leaves without dice (<c>noDiceKeepCoins</c> of its snapshot, not waived).</summary>
    public static bool WithoutDice(RunState run) =>
        run.Condition is { Met: false, Waived: false } && run.Snapshot.ConditionPolicy == UnmetConditionPolicy.NoDiceKeepCoins;

    /// <summary>The check at the completion, when the run has conditions.</summary>
    public static RunConditionChecked? AtCompletion(RunState run, Guid playerId, Difficulty difficulty) =>
        run.Snapshot.Conditions.Count == 0 ? null : new RunConditionChecked(run.RunId, playerId, difficulty, Hold(run.Snapshot, difficulty));

    /// <summary>
    /// The check after a change of the run (<paramref name="run"/> as the change left it, D-592): null without conditions or
    /// when waived; dice thrown by its hours when it becomes met after it had none.
    /// </summary>
    public static RunConditionChecked? Again(RunState run, EngineContext context, bool waive = false)
    {
        if (run.Snapshot.Conditions.Count == 0 || run.Condition is not { } state || (state.Waived && !waive))
        {
            return null;
        }

        var difficulty = run.Difficulty ?? throw new InvalidOperationException($"Completed run {run.RunId} has no difficulty.");
        var met = waive || Hold(run.Snapshot, difficulty);
        if (!WithoutDice(run) || !met)
        {
            return new RunConditionChecked(run.RunId, run.PlayerId, difficulty, met, Waived: waive);
        }

        var rule = CompletionRoll.DieFor(difficulty, run.Snapshot.DieByDifficulty).Sides;
        var count = CompletionRoll.Count(run.Hours!.Value, run.Snapshot);
        if (state.Held is { } held)
        {
            // The dice the condition took come back, each at the new difficulty by Q-5, items' sides and single die kept
            // (D-593); by the hours as they are now: missing ones thrown, extra ones off the end (D-14)
            var sides = held.Mods?.Sides ?? rule;
            static EquatableArray<Die> Recalculate(EquatableArray<Die> dice, int sides) =>
                [.. dice.Select(d => new Die(sides, (int)Math.Ceiling((decimal)d.Value * sides / d.Sides)))];
            var back = held.Mods?.SingleDie is { } single
                ? Recalculate(held.Dice, single)
                : (EquatableArray<Die>)[.. Recalculate(held.Dice, sides).Take(count), .. CompletionRoll.Roll(Math.Max(0, count - held.Dice.Count), sides, context.Random)];
            return new RunConditionChecked(run.RunId, run.PlayerId, difficulty, met, back, held.ChallengeDice.Count > 0 ? Recalculate(held.ChallengeDice, sides) : default, waive);
        }

        // Never thrown: dice by its hours at its die, as the completion would have thrown them, without the changes of
        // items that never took part (D-593)
        var dice = CompletionRoll.Roll(count, rule, context.Random);
        EquatableArray<Die> challenge = state.ChallengeDone && run.Snapshot.ChallengeExtraDice > 0
            ? CompletionRoll.Roll(run.Snapshot.ChallengeExtraDice, rule, context.Random)
            : default;
        return new RunConditionChecked(run.RunId, run.PlayerId, difficulty, met, dice, challenge, waive);
    }

    public static Decision Decide(SeasonState state, WaiveRunCondition command, EngineContext context)
    {
        if (Corrections.Guard(state, command.RunId, command.Comment) is { } rejection)
        {
            return rejection;
        }

        var run = state.Runs[command.RunId];
        if (run.Snapshot.Conditions.Count == 0 || run.Condition is not { Waived: false })
        {
            return Decision.Reject(RejectionCodes.RunNoCondition, $"Run {run.RunId} has no condition to waive.");
        }

        var check = Again(run, context, waive: true)!;
        var after = SeasonEngine.Apply(state, check);
        var events = new List<IGameEvent> { check };
        events.AddRange(Corrections.Difference(after, run.RunId, CompletionRoll.Total(after.Runs[run.RunId]) - CompletionRoll.Total(run)));
        if (!Finishes.IsFrozen(state.Players[run.PlayerId]))
        {
            events.AddRange(Decks.EventDraws.ForDifficulty(events.Aggregate(state, SeasonEngine.Apply), context, run.RunId));
        }

        // Not applicable to this game: the conditions wait for the next one (SPEC «ждёт следующей», D-546 (2))
        events.AddRange(run.Snapshot.Conditions.Select(c =>
            new NextRollModified(run.PlayerId, new RollModifier(c.ObjectId, null, null, c.Hostile) { RunCondition = c.Condition })));
        return Decision.Accept(events);
    }

    public static SeasonState Apply(SeasonState state, RunConditionChecked e)
    {
        var run = state.Runs[e.RunId];
        var before = WithoutDice(run);
        var checkedRun = run with
        {
            Condition = (run.Condition ?? new RunConditionState(true)) with { Met = e.Met, Waived = e.Waived || run.Condition?.Waived == true },
        };
        if (!before && WithoutDice(checkedRun))
        {
            // The dice are taken, not lost: held for the condition becoming met again (D-593)
            var held = run.Dice.Count > 0 || run.ChallengeDice.Count > 0 || run.Mods is not null
                ? new RunHeldDice(run.Dice, run.ChallengeDice, run.Mods)
                : null;
            checkedRun = checkedRun with { Dice = [], ChallengeDice = [], Mods = null, Condition = checkedRun.Condition! with { Held = held } };
        }
        else if (before && !WithoutDice(checkedRun))
        {
            var held = run.Condition?.Held;
            checkedRun = checkedRun with { Dice = e.Dice, ChallengeDice = e.ChallengeDice, Mods = held?.Mods, Condition = checkedRun.Condition! with { Held = null } };
        }

        return state with { Runs = state.Runs.SetItem(e.RunId, checkedRun) };
    }

    /// <summary>A tech reroll gives the run's conditions back to the next roll, ahead of the others (Н-7, D-595).</summary>
    public static SeasonState Return(SeasonState state, RunState run)
    {
        if (run.Snapshot.Conditions.Count == 0)
        {
            return state;
        }

        EquatableArray<RollModifier> back = [.. run.Snapshot.Conditions.Select(c =>
            new RollModifier(c.ObjectId, null, null, c.Hostile) { RunCondition = c.Condition })];
        return Inventories.Update(state, run.PlayerId, w => w with { NextRoll = [.. back, .. w.NextRoll] });
    }

    private static Difficulty? Max(Difficulty? a, Difficulty? b) => a is null ? b : b is null ? a : (Difficulty)Math.Max((int)a, (int)b);

    private static Difficulty? Min(Difficulty? a, Difficulty? b) => a is null ? b : b is null ? a : (Difficulty)Math.Min((int)a, (int)b);
}

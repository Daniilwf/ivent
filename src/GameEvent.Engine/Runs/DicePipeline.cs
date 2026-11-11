using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Runs;

/// <summary>A completion's throw after the pipeline (D-14, D-408): the dice by hours, the challenge dice, and what items did.</summary>
internal sealed record CompletionThrow(EquatableArray<Die> Dice, EquatableArray<Die> ChallengeDice, RunDiceMods? Mods, EquatableArray<Die> Rolled);

/// <summary>
/// The throw's pipeline with items and effects (SPEC «Конвейер броска», D-14, D-408): count and sides, the roll, then
/// additions, multipliers and bounds. Changes waiting for the next throw and those of <c>beforeDice</c> effects apply to a
/// completion; <c>current</c> changes after the throw change it after the fact, points and steps by the difference.
/// </summary>
internal static class DicePipeline
{
    /// <summary>
    /// The throw of a completion: <paramref name="count"/> dice by hours and <paramref name="challengeCount"/> challenge dice
    /// of <paramref name="die"/>, changed by <paramref name="changes"/> in the pipeline's order.
    /// </summary>
    public static CompletionThrow Throw(
        int count, DieRule die, int challengeCount, IReadOnlyList<ThrowChange> changes, IRandomSource random)
    {
        var sides = changes.LastOrDefault(c => c.Stage == DiceStage.Sides)?.Value.Number ?? die.Sides;

        // One die of its own instead of the run's dice by hours (D-481)
        var single = changes.LastOrDefault(c => c.Stage == DiceStage.SingleDie)?.Value.Number;
        var dice = single is { } singleSides ? CompletionRoll.Roll(1, singleSides, random) : CompletionRoll.Roll(count, sides, random);
        var challenge = CompletionRoll.Roll(challengeCount, sides, random);
        if (changes.Count == 0)
        {
            return new CompletionThrow(dice, challenge, null, []);
        }

        var mods = RunDiceMods.None with { Sources = [.. changes.Select(c => c.ObjectId).Distinct()] };
        if (sides != die.Sides)
        {
            mods = mods with { Sides = sides };
        }

        // Extra dice of the run's kind first, then those of their own kind in the order they came (D-475)
        var extra = changes.Where(c => c.Stage == DiceStage.Count && c.Sides is null).Sum(c => c.Value.Number);
        mods = mods with
        {
            ExtraDice =
            [
                .. CompletionRoll.Roll(Math.Max(0, extra), sides, random),
                .. changes.Where(c => c.Stage == DiceStage.Count && c.Sides is not null).SelectMany(c => CompletionRoll.Roll(Math.Max(0, c.Value.Number), c.Sides!.Value, random)),
            ],
        };
        var rolled = new List<Die>();
        if (single is not null)
        {
            mods = mods with { SingleDie = single };
        }

        foreach (var change in changes.Where(c => c.Stage is not (DiceStage.Count or DiceStage.Sides or DiceStage.Reroll or DiceStage.SingleDie))
            .OrderBy(c => c.Stage switch
            {
                DiceStage.Add => 0,
                DiceStage.Multiply or DiceStage.Percent => 1,
                DiceStage.Min or DiceStage.Max => 2,
                _ => 0,
            }))
        {
            mods = mods.With(change.Stage, Number(change.Value, random, rolled), change.ObjectId, change.From);
        }

        return new CompletionThrow(dice, challenge, mods, [.. rolled]);
    }

    /// <summary>
    /// A <c>current</c> change after the throw (the <c>afterDice</c> window or trigger): a reroll replaces the dice, a count
    /// adds dice, the other stages change the mods; points and position follow the difference like a correction (D-408).
    /// </summary>
    public static void AfterThrow(EffectRun effect, Guid runId, ThrowChange change)
    {
        var (stage, value, extraSides) = (change.Stage, change.Value, change.Sides);
        var run = effect.State.Runs[runId];

        // The throw's move waits at a fork: that step has begun, a change after the throw would move the token off it (D-416)
        if (effect.State.Players[run.PlayerId].Choice?.Kind == Turns.ChoiceKind.Branch)
        {
            return;
        }

        var sides = run.Mods?.Sides ?? (run.Dice.Count > 0 ? run.Dice[0].Sides : (int?)null) ?? CompletionRoll.DieFor(run.Difficulty!.Value, run.Snapshot.DieByDifficulty).Sides;
        var random = effect.Context.Random;
        var before = CompletionRoll.Total(run);
        switch (stage)
        {
            case DiceStage.Reroll when change.Filter == DieFilter.BelowAverage:
                // Only the dice below their average, each of its own kind (D-481)
                EquatableArray<Die> Again(EquatableArray<Die> dice) =>
                    [.. dice.Select(d => d.Value < d.Sides / 2 ? CompletionRoll.Roll(1, d.Sides, random)[0] : d)];
                var dice = Again(run.Dice);
                var challenge = Again(run.ChallengeDice);
                effect.Emit(new RunDiceRerolled(runId, run.PlayerId, effect.ObjectId, dice, challenge, Again(run.DiceMods.ExtraDice)));
                break;
            case DiceStage.Reroll:
                RunDiceRerolled? rerolled = null;
                for (var i = 0; i < Math.Max(1, value.Number); i++)
                {
                    rerolled = new RunDiceRerolled(
                        runId, run.PlayerId, effect.ObjectId,
                        CompletionRoll.Roll(run.Dice.Count, sides, random),
                        CompletionRoll.Roll(run.ChallengeDice.Count, sides, random),
                        CompletionRoll.Roll(run.DiceMods.ExtraDice.Count, sides, random));
                }

                effect.Emit(rerolled!);
                break;
            case DiceStage.Count:
                var added = CompletionRoll.Roll(Math.Max(0, value.Number), extraSides ?? sides, random);
                var withDice = run.DiceMods with
                {
                    ExtraDice = [.. run.DiceMods.ExtraDice, .. added],
                    Sources = run.DiceMods.Sources.Contains(effect.ObjectId) ? run.DiceMods.Sources : [.. run.DiceMods.Sources, effect.ObjectId],
                };
                effect.Emit(new RunDiceModified(runId, run.PlayerId, withDice, added));
                break;
            case DiceStage.Sides or DiceStage.SingleDie:
                // The dice are on the table: their sides and number do not change any more.
                return;
            default:
                var rolled = new List<Die>();
                var mods = run.DiceMods.With(stage, Number(value, random, rolled), effect.ObjectId, change.From);
                effect.Emit(new RunDiceModified(runId, run.PlayerId, mods, [.. rolled]));
                break;
        }

        var delta = CompletionRoll.Total(effect.State.Runs[runId]) - before;
        effect.Emit(Corrections.Difference(effect.State, runId, delta, PointsReason.DiceModified, MoveReason.DiceModified));
    }

    // A pipeline value as a number: dice are rolled into rolled, a minus before them subtracts.
    private static int Number(ContentValue value, IRandomSource random, List<Die> rolled)
    {
        if (value.Kind != ContentValueKind.Dice)
        {
            return value.Number;
        }

        var dice = CompletionRoll.Roll(value.Count, value.Sides, random);
        rolled.AddRange(dice);
        var sum = dice.Sum(d => d.Value);
        return value.Negative ? -sum : sum;
    }

    /// <summary>
    /// The changes of a completion's throw (D-408): those waiting for the next throw, then those of the owner's
    /// <c>beforeDice</c> effects firing now; the events of the firing and the state after it.
    /// </summary>
    public static (IReadOnlyList<IGameEvent> Events, SeasonState State, IReadOnlyList<ThrowChange> Changes) BeforeDice(
        SeasonState state, Guid playerId, Guid runId, EngineContext context)
    {
        if (!state.Rules.Features.Items || Targets.IsFirst(state, playerId))
        {
            return ([], state, []);
        }

        var changes = state.Players[playerId].Wallet.NextDice.Select(m => new ThrowChange(m.ObjectId, m.Stage, m.Value, m.Sides, m.From)).ToList();
        var events = new List<IGameEvent>();
        foreach (var item in Firing.Subscribed(state, playerId, Trigger.BeforeDice))
        {
            if (state.Players[playerId].Wallet.Find(item.InstanceId) is not { } held
                || Firing.Fire(state, context, playerId, held, Trigger.BeforeDice, runId, DiceScope.Throw) is not { } fired)
            {
                continue;
            }

            events.AddRange(fired.Events);
            changes.AddRange(fired.ThrowChanges);
            state = fired.State;
        }

        return (events, state, changes);
    }

    public static SeasonState Apply(SeasonState state, RunDiceModified e)
    {
        var run = state.Runs[e.RunId] with { Mods = e.Mods };
        state = state with { Runs = state.Runs.SetItem(e.RunId, run) };
        return e.SpentNext > 0 ? Inventories.Update(state, e.PlayerId, w => w with { NextDice = [.. w.NextDice.Skip(e.SpentNext)] }) : state;
    }

    public static SeasonState Apply(SeasonState state, RunDiceRerolled e)
    {
        var run = state.Runs[e.RunId];
        var mods = run.DiceMods with
        {
            ExtraDice = e.ExtraDice,
            Sources = run.DiceMods.Sources.Contains(e.ObjectId) ? run.DiceMods.Sources : [.. run.DiceMods.Sources, e.ObjectId],
        };
        return state with { Runs = state.Runs.SetItem(e.RunId, run with { Dice = e.Dice, ChallengeDice = e.ChallengeDice, Mods = mods }) };
    }
}

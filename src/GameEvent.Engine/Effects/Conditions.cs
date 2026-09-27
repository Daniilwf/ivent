using GameEvent.Engine.Content;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Effects;

/// <summary>
/// Whether an effect's condition holds (CONTENT.md «Условия и фильтры»): on the run of the trigger (its difficulty, its
/// game, its hours, its dice) or on the owner's statistics. A condition on a run is false without one.
/// </summary>
internal static class Conditions
{
    public static bool Hold(SeasonState state, EngineContext context, Guid ownerId, ConditionSpec condition, Guid? runId)
    {
        var run = runId is { } id ? state.Runs.GetValueOrDefault(id) : null;
        if (condition.DifficultyAtLeast is { } difficulty && !(run?.Difficulty >= difficulty))
        {
            return false;
        }

        if (condition.Game is { } filter
            && !(run is not null && context.Pool.Games.FirstOrDefault(g => g.Id == run.GameId) is { } game && Rolls.Rolling.Matches(filter, game)))
        {
            return false;
        }

        return condition.Stat switch
        {
            null => true,
            ContentStat.HostileReceived => state.Players[ownerId].Wallet.HostileReceived >= condition.Gte,
            ContentStat.RunHours => run?.Hours >= condition.Gte,
            ContentStat.AllDiceMax => run is not null && AllMax(run, condition.MinDice ?? 1),
            ContentStat.CompletedStreakWithTag => Streak(state, context, ownerId, condition.Tag!) >= condition.Gte,
            _ => false,
        };
    }

    // Every die of the run's throw at its maximum, when there are at least minDice of them.
    private static bool AllMax(RunState run, int minDice)
    {
        List<Die> dice = [.. run.Dice, .. run.ChallengeDice, .. run.DiceMods.ExtraDice];
        return dice.Count >= minDice && dice.All(d => d.Value == d.Sides);
    }

    // The owner's completed runs from the latest back, while their games have the tag.
    private static int Streak(SeasonState state, EngineContext context, Guid ownerId, string tag) =>
        state.Runs.Values
            .Where(r => r.PlayerId == ownerId && r.Status == RunStatus.Completed)
            .OrderByDescending(r => r.CompletedAt)
            .TakeWhile(r => context.Pool.Games.FirstOrDefault(g => g.Id == r.GameId)?.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase) == true)
            .Count();
}

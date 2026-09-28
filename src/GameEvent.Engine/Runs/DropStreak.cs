using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Runs;

/// <summary>
/// Drops in a row (D-205, D-324). The streak before a run is the player's drops rolled before it since their last counted
/// completion (completed and not rejected); tech reroll and rejected runs neither add to it nor end it. Derived from the
/// runs, so an undo or a reject needs no counter to fix; the dice of drops already thrown stay as logged.
/// </summary>
public static class DropStreak
{
    /// <summary>
    /// The drops in a row before the run <paramref name="runId"/> rolled at <paramref name="rolledAt"/>, among
    /// <paramref name="runs"/> — its player's runs (it may be among them); runs rolled at the same moment go by id.
    /// </summary>
    public static int Count(IEnumerable<(Guid RunId, DateTimeOffset RolledAt, RunStatus Status)> runs, Guid runId, DateTimeOffset rolledAt)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var streak = 0;
        var earlier = runs
            .Where(r => r.RunId != runId && (r.RolledAt, r.RunId).CompareTo((rolledAt, runId)) < 0)
            .OrderByDescending(r => (r.RolledAt, r.RunId));
        foreach (var (_, _, status) in earlier)
        {
            if (status == RunStatus.Completed)
            {
                break;
            }

            streak += status == RunStatus.Dropped ? 1 : 0;
        }

        return streak;
    }

    /// <summary>The drops in a row before <paramref name="run"/> of its player.</summary>
    public static int Before(SeasonState state, RunState run)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(run);
        return Count(state.Runs.Values.Where(r => r.PlayerId == run.PlayerId).Select(r => (r.RunId, r.RolledAt, r.Status)), run.RunId, run.RolledAt);
    }

    /// <summary>The penalty dice of a drop after <paramref name="streak"/> drops in a row.</summary>
    public static int PenaltyDiceCount(DropRules rules, int streak)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.PenaltyDice.Count + (streak * (rules.ConsecutiveExtraDice ?? 0));
    }
}

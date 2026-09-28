using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Finish;

/// <summary>
/// A player's finish (D-99, Q-3, Q-4): <see cref="Order"/> among the finishers (1, 2, 3…, never reused), the run whose
/// move brought them there, whether they are frozen, the finish bonus they hold now (at most one), and the
/// <see cref="Surplus"/> — steps that burned at the finish, which absorb a later reduction of a run up to the finish.
/// <see cref="BonusRules"/> — the bonus table in force when they finished (D-113): a later change of the rules does not
/// touch the bonus. <see cref="ApprovalRequired"/> — whether the first's approval was required when they finished (D-115):
/// it decides the freeze of this finisher as the first, whatever the rule says later. Both are null only in states made
/// before they were kept, which read the season's current rules.
/// </summary>
public sealed record FinishState(
    int Order, Guid RunId, DateTimeOffset FinishedAt, bool Frozen, int Bonus, int Surplus, FinishBonusRules? BonusRules = null, bool? ApprovalRequired = null);

/// <summary>The finish bonuses by place a finisher keeps from the moment of their finish (D-113).</summary>
public sealed record FinishBonusRules(EquatableArray<int> ByOrder, int AfterList)
{
    public static FinishBonusRules Of(Rulesets.FinishRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new FinishBonusRules(rules.BonusByOrder, rules.BonusAfterList);
    }
}

/// <summary>
/// The admin brought every standing finisher's bonus table to <see cref="Rules"/> — the season's rules of version
/// <see cref="RulesetVersion"/> (D-113); the bonus differences follow as points changes in the same command.
/// </summary>
[EventType("finish-bonus-rules-refreshed")]
public sealed record FinishBonusRulesRefreshed(int RulesetVersion, FinishBonusRules Rules) : IGameEvent;

/// <summary>The admin's deliberate recalculation of the finish bonuses by the current rules (D-113), written to the log.</summary>
public sealed record RecalculateFinishBonuses : ICommand;

/// <summary>
/// The player's token reached the finish with <see cref="Surplus"/> steps burned; the finish bonuses of all finishers
/// are recalculated in the same command (Q-4).
/// </summary>
[EventType("player-finished")]
public sealed record PlayerFinished(Guid PlayerId, Guid RunId, int Order, DateTimeOffset FinishedAt, int Surplus) : IGameEvent;

/// <summary>A reduction of a run up to the finish was absorbed by the surplus (or an increase added to it): the finish stands (Q-3).</summary>
[EventType("finish-surplus-changed")]
public sealed record FinishSurplusChanged(Guid PlayerId, int Delta) : IGameEvent;

/// <summary>The first finisher is frozen: points and coins no longer change (D-99, the freeze amendment).</summary>
[EventType("player-frozen")]
public sealed record PlayerFrozen(Guid PlayerId) : IGameEvent;

/// <summary>
/// The finish is revoked (D-15, Q-3, D-99): a reject or a reduction of a run up to the finish went beyond the surplus.
/// <see cref="RunId"/> is the run that had reached the finish, not necessarily the one rejected or cut.
/// </summary>
[EventType("player-finish-revoked")]
public sealed record PlayerFinishRevoked(Guid PlayerId, Guid RunId) : IGameEvent;

/// <summary>Who finished and who is first (D-99).</summary>
public static class FinishLine
{
    /// <summary>The first finisher: the lowest order among players whose finish stands; null when nobody finished.</summary>
    public static Guid? First(SeasonState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Players.Values
            .Where(p => p.Finish is not null)
            .OrderBy(p => p.Finish!.Order)
            .Select(p => (Guid?)p.PlayerId)
            .FirstOrDefault();
    }

    /// <summary>The finish bonus at <paramref name="place"/> among the standing finishers (1 — the first, no bonus; Q-4).</summary>
    public static int Bonus(Rulesets.FinishRules rules, int place) => Bonus(FinishBonusRules.Of(rules), place);

    /// <summary>The finish bonus at <paramref name="place"/> by a finisher's own table (D-113).</summary>
    public static int Bonus(FinishBonusRules rules, int place)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return place <= 1 ? 0 : place - 2 < rules.ByOrder.Count ? rules.ByOrder[place - 2] : rules.AfterList;
    }
}

/// <summary>The finish rules the other mechanics consult (D-99, Q-3, Q-4, the freeze amendment).</summary>
internal static class Finishes
{
    public static bool IsFrozen(SeasonPlayer player) => player.Finish?.Frozen == true;

    /// <summary>A run of a finished player that counts for the position: completed before the finish (Q-3).</summary>
    public static bool CountsForFinish(SeasonPlayer player, RunState run) => player.Finish is not null && !run.AfterFinish;

    /// <summary>A completion move landing on the finish of a player who has not finished: the finish, then Q-4 bonuses and the freeze.</summary>
    public static IEnumerable<IGameEvent> AfterCompletionMove(SeasonState state, SeasonPlayer player, RunState run, PlayerMoved move, DateTimeOffset now)
    {
        if (player.Finish is not null || state.Map.CellById(move.To).Type != CellType.Finish)
        {
            return [];
        }

        var finished = new PlayerFinished(player.PlayerId, run.RunId, state.FinishesSoFar + 1, now, move.Steps - move.Path.Count);
        return [finished, .. Settle(SeasonEngine.Apply(state, finished))];
    }

    /// <summary>
    /// How much a run up to the finish takes from the position when its dice drop by <paramref name="points"/>: the finishing
    /// run's dice all count (the surplus holds its burned steps); an earlier run gave only the cells it moved, so it takes
    /// back only what it moved beyond its new sum (D-98, RR8).
    /// </summary>
    public static int Reduction(SeasonPlayer player, RunState run, int points)
    {
        if (player.Finish?.RunId == run.RunId)
        {
            return points;
        }

        var newSum = Runs.CompletionRoll.Total(run);
        return Math.Max(0, run.Moved - newSum);
    }

    /// <summary>
    /// A run up to the finish loses <paramref name="points"/> (reject or reduction): the surplus absorbs it, or the finish
    /// is revoked and the token goes back by the rest (Q-3). An increase adds to the surplus. Then bonuses and the freeze.
    /// </summary>
    public static IEnumerable<IGameEvent> AfterReduction(SeasonState state, SeasonPlayer player, RunState run, int points, MoveReason reason)
    {
        var finish = player.Finish ?? throw new InvalidOperationException($"Player {player.PlayerId} has not finished.");
        if (points <= finish.Surplus)
        {
            return points == 0 ? [] : [new FinishSurplusChanged(player.PlayerId, -points)];
        }

        var events = new List<IGameEvent>();
        if (finish.Bonus != 0)
        {
            events.Add(new PointsChanged(player.PlayerId, -finish.Bonus, PointsReason.FinishBonusRevoked, RunId: null));
        }

        events.Add(new PlayerFinishRevoked(player.PlayerId, finish.RunId));
        var path = Movement.Backward(state.Map, player.Path, points - finish.Surplus);
        if (path.Count > 0)
        {
            events.Add(new PlayerMoved(player.PlayerId, player.CellId, path[^1], -path.Count, [.. path], reason, run.RunId));
        }

        var after = events.Aggregate(state, SeasonEngine.Apply);
        events.AddRange(Settle(after));
        return events;
    }

    /// <summary>
    /// Q-4 bonuses of all standing finishers by their order — each by the table they finished under (D-113), at the place
    /// they hold now — then the first's freeze if due.
    /// </summary>
    public static IEnumerable<IGameEvent> Settle(SeasonState state)
    {
        var events = BonusChanges(state);
        return [.. events, .. FreezeIfDue(events.Aggregate(state, SeasonEngine.Apply))];
    }

    /// <summary>The Q-4 bonus differences alone (D-113: the admin's recalculation freezes nobody).</summary>
    public static List<IGameEvent> BonusChanges(SeasonState state)
    {
        var current = FinishBonusRules.Of(state.Rules.Finish);
        var events = new List<IGameEvent>();
        var place = 0;
        foreach (var player in state.Players.Values.Where(p => p.Finish is not null).OrderBy(p => p.Finish!.Order))
        {
            place++;
            if (IsFrozen(player))
            {
                continue;
            }

            var diff = FinishLine.Bonus(player.Finish!.BonusRules ?? current, place) - player.Finish.Bonus;
            if (diff != 0)
            {
                events.Add(new PointsChanged(
                    player.PlayerId, diff, diff > 0 ? PointsReason.FinishBonus : PointsReason.FinishBonusRevoked, RunId: null));
            }
        }

        return events;
    }

    /// <summary>
    /// The standing finishers whose bonus depends on their table: all but the first, who holds no bonus at his place
    /// (D-113) — his table changes nothing, so it neither needs a recalculation nor warns.
    /// </summary>
    public static IEnumerable<SeasonPlayer> Bonused(SeasonState state) =>
        state.Players.Values.Where(p => p.Finish is not null).OrderBy(p => p.Finish!.Order).Skip(1);

    /// <summary>The first is frozen once every run up to the finish is approved (Q-3), or at once without required approval.</summary>
    public static IEnumerable<IGameEvent> FreezeIfDue(SeasonState state)
    {
        if (FinishLine.First(state) is not { } firstId || IsFrozen(state.Players[firstId]))
        {
            return [];
        }

        // D-115: the rule in force when the first finished, not the one now
        var approved = !(state.Players[firstId].Finish!.ApprovalRequired ?? state.Rules.Finish.RequireApprovalForFirst)
            || state.Runs.Values
                .Where(r => r.PlayerId == firstId && r.Status == RunStatus.Completed && !r.AfterFinish)
                .All(r => r.Proof?.Status == ProofStatus.Approved);
        return approved ? [new PlayerFrozen(firstId)] : [];
    }
}

internal static class Finishing
{
    /// <summary>
    /// The admin recalculates the finish bonuses by the current rules (D-113): every standing finisher takes the current
    /// table, bonuses follow at their places. Nothing to do — no finisher, or all of them already on the current table.
    /// </summary>
    public static Decision Decide(SeasonState state, RecalculateFinishBonuses command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "The season does not exist yet.");
        }

        if (state.Status == SeasonStatus.Draft)
        {
            return Decision.Reject(RejectionCodes.SeasonNotActive, "The season has not started: nobody has finished.");
        }

        if (state.Status is not (SeasonStatus.Active or SeasonStatus.Closing))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: results are fixed.");
        }

        var current = FinishBonusRules.Of(state.Rules.Finish);
        if (Finishes.Bonused(state).All(p => p.Finish!.BonusRules == current))
        {
            return Decision.Reject(RejectionCodes.FinishNothingToRecalculate, "Every finisher with a bonus already holds the bonus table of the current rules.");
        }

        // Bonuses only: the recalculation is about the bonus table, it does not settle the first's freeze
        var refreshed = new FinishBonusRulesRefreshed(state.RulesetVersion, current);
        return Decision.Accept([refreshed, .. Finishes.BonusChanges(Apply(state, refreshed))]);
    }

    public static SeasonState Apply(SeasonState state, PlayerFinished e) =>
        Update(state, e.PlayerId, p => p with
        {
            Finish = new FinishState(
                e.Order, e.RunId, e.FinishedAt, Frozen: false, Bonus: 0, e.Surplus, FinishBonusRules.Of(state.Rules.Finish), state.Rules.Finish.RequireApprovalForFirst),
        })
            with
        { FinishesSoFar = Math.Max(state.FinishesSoFar, e.Order) };

    public static SeasonState Apply(SeasonState state, FinishBonusRulesRefreshed e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var players = state.Players;
        foreach (var player in state.Players.Values.Where(p => p.Finish is not null))
        {
            players = players.SetItem(player.PlayerId, player with { Finish = player.Finish! with { BonusRules = e.Rules } });
        }

        return state with { Players = players };
    }

    public static SeasonState Apply(SeasonState state, PlayerFrozen e) =>
        Update(state, e.PlayerId, p => p with { Finish = p.Finish! with { Frozen = true } });

    // With the finish gone, the runs completed after it count as ordinary runs of an unfinished player (they did not move
    // the token); games played in free mode stay available to the others (D-99). Cells need no fix: the backward move
    // that follows the revoke brings the cut run's cells down to its new dice.
    public static SeasonState Apply(SeasonState state, PlayerFinishRevoked e)
    {
        var runs = state.Runs;
        foreach (var run in state.Runs.Values.Where(r => r.PlayerId == e.PlayerId && r.AfterFinish))
        {
            runs = runs.SetItem(run.RunId, run with { AfterFinish = false });
        }

        return Update(state with { Runs = runs }, e.PlayerId, p => p with { Finish = null });
    }

    public static SeasonState Apply(SeasonState state, FinishSurplusChanged e) =>
        Update(state, e.PlayerId, p => p with { Finish = p.Finish! with { Surplus = p.Finish.Surplus + e.Delta } });

    private static SeasonState Update(SeasonState state, Guid playerId, Func<SeasonPlayer, SeasonPlayer> change) =>
        state with { Players = state.Players.SetItem(playerId, change(state.Players[playerId])) };
}

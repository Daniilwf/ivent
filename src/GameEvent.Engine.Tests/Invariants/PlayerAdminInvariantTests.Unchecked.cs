using FsCheck.Xunit;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Invariants;

/// <summary>
/// D-134: the limit of unchecked runs in random games. The season plays with <c>season.maxUncheckedRuns</c> of 1–3 by
/// the seed, proofs, approvals, rejects, the lifecycle and (in the combined variant) undos, and after every command:
/// a roll from Idle is accepted only under the limit and refused with <c>roll.tooManyUnchecked</c> only at or over it;
/// no other command is ever refused with that code. A player's unchecked runs are the completed runs whose proof is not
/// approved. Players who ever finished are left out of the count: the first finisher's free-mode runs do not count, and
/// which runs of a revoked or provisional finisher are free mode is the scenario tests' business.
/// </summary>
public partial class PlayerAdminInvariantTests
{
    private static Func<Ruleset, Ruleset> UncheckedLimitFor(int seed) =>
        r => r with { Season = r.Season with { MaxUncheckedRuns = (((seed % 3) + 3) % 3) + 1 } };

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_a_limit_of_unchecked_runs(int seed, byte[] script) =>
        Play(seed, script, CheckUncheckedLimitInvariants, withDrops: true, withCorrections: true, withProofs: true, finishes: true, lifecycle: true, rules: UncheckedLimitFor(seed));

    [Property(MaxTest = 100)]
    public void Invariants_hold_with_a_limit_of_unchecked_runs_and_undos(int seed, byte[] script) =>
        Play(seed, script, CheckUncheckedLimitCombined, withChoice: true, rerolls: RerollMode.Coins, withDrops: true, withCorrections: true, withProofs: true, finishes: true, lifecycle: true, withUndo: true, withRulesetChanges: true, rules: UncheckedLimitFor(seed));

    [Fact]
    public void Limit_variant_meets_a_refusal_a_roll_at_the_boundary_and_a_freed_slot()
    {
        // The invariant must meet its cases over fixed scripts: a roll refused by the limit, a roll accepted one run
        // under it, and a roll accepted after the same player was refused
        var refused = 0;
        var atBoundary = 0;
        var freed = 0;
        for (var seed = 0; seed < 60; seed++)
        {
            var x = (uint)seed + 17;
            var script = Enumerable.Range(0, 400).Select(_ => (byte)((x = (x * 1103515245) + 12345) >> 16)).ToArray();
            var blocked = new HashSet<Guid>();
            Play(seed, script, (s, command, before, _) =>
            {
                if (command is not RollGame roll || !before.Players.ContainsKey(roll.PlayerId))
                {
                    return;
                }

                if (!s.Last.IsAccepted && s.Last.Rejection!.Code == RejectionCodes.TooManyUncheckedRuns)
                {
                    refused++;
                    blocked.Add(roll.PlayerId);
                }
                else if (s.Last.IsAccepted)
                {
                    atBoundary += UncheckedRuns(before, roll.PlayerId) == before.Rules.Season.MaxUncheckedRuns - 1 ? 1 : 0;
                    freed += blocked.Remove(roll.PlayerId) ? 1 : 0;
                }
            }, withDrops: true, withCorrections: true, withProofs: true, finishes: true, lifecycle: true, rules: UncheckedLimitFor(seed));
        }

        Assert.True(refused > 0, "No roll was refused by the limit.");
        Assert.True(atBoundary > 0, "No roll was accepted one unchecked run under the limit.");
        Assert.True(freed > 0, "No refused player rolled again after a check.");
    }

    private static void CheckUncheckedLimitInvariants(Scenario s, ICommand command, SeasonState before, int logLengthBefore)
    {
        CheckFinishInvariants(s, command, before, logLengthBefore);
        CheckUncheckedLimit(s, command, before);
    }

    private static void CheckUncheckedLimitCombined(Scenario s, ICommand command, SeasonState before, int logLengthBefore)
    {
        CheckCombinedInvariants(s, command, before, logLengthBefore);
        CheckUncheckedLimit(s, command, before);
    }

    private static void CheckUncheckedLimit(Scenario s, ICommand command, SeasonState before)
    {
        var refusedByLimit = !s.Last.IsAccepted && s.Last.Rejection!.Code == RejectionCodes.TooManyUncheckedRuns;
        if (command is not RollGame roll)
        {
            Assert.False(refusedByLimit, $"{command.GetType().Name} was refused by the limit of unchecked runs; only a roll is.");
            return;
        }

        if (!before.IsCreated || !before.Players.ContainsKey(roll.PlayerId))
        {
            return;
        }

        var limit = before.Rules.Season.MaxUncheckedRuns;
        var count = UncheckedRuns(before, roll.PlayerId);
        if (s.Last.IsAccepted)
        {
            Assert.True(limit is null || count < limit, $"A roll was accepted with {count} unchecked runs at the limit {limit}.");
        }

        if (refusedByLimit)
        {
            Assert.True(limit is not null && count >= limit, $"A roll was refused by the limit {limit} with {count} unchecked runs.");
        }
    }

    /// <summary>
    /// The reference count, written apart from the engine's: the player's completed runs whose proof is neither approved
    /// nor rejected (sent or not). The frozen first waits for nothing: a check takes nothing from him (D-134, Q-3); a
    /// provisional first and later finishers count like everyone.
    /// </summary>
    private static int UncheckedRuns(SeasonState state, Guid playerId) =>
        state.Players[playerId].Finish?.Frozen == true
            ? 0
            : state.Runs.Values.Count(r => r.PlayerId == playerId && r.Status == RunStatus.Completed
                && r.Proof?.Status is not (ProofStatus.Approved or ProofStatus.Rejected));
}

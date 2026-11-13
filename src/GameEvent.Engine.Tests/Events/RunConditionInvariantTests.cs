using FsCheck.Xunit;
using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// Random seasons with conditions of runs (task 5.5): vows of every bound, completions on every difficulty, drops, tech
/// rerolls, admin difficulty and hours corrections, approvals on a lower difficulty, «условие неприменимо» and policy changes.
/// Each byte of <c>script</c> is one command, valid or not; after every accepted command:
/// (1) the conditions of every snapshot (runs and offers) are compatible — the highest «не ниже» is not above the lowest
/// «не выше» (D-506);
/// (2) a run without conditions in its snapshot has no condition state, and no snapshot without conditions has a policy;
/// (3) a completed run whose condition is unmet and not waived under <c>noDiceKeepCoins</c> of its snapshot has no dice and
/// no challenge dice, after any correction (D-507);
/// (4) a completed run with a condition that is not waived is met exactly when every condition holds at its difficulty;
/// (5) the last <c>run-condition-checked</c> of a run in a command is what the state holds (the replay does not recompute);
/// (6) a command writes at most 50 events. At the end the log replays to the state, and the same seed and script give the
/// same log (invariant 14). Belongs to the generator of task 5.15; kept here until it lands there.
/// </summary>
public class RunConditionInvariantTests
{
    private static readonly string[] s_players = ["Вася", "Петя"];

    private static readonly string[] s_vows = ["vow-hard", "vow-normal", "vow-extreme", "vow-easy", "vow-at-most-hard", "vow-normal-to-hard", "more-dice"];

    private static Scenario NewSeason(int seed) =>
        RunConditionTests.Season(
            rules: r => r with { Features = r.Features with { Challenges = true } },
            games: [("Alan Wake", 6), ("Silent Hill", 3), ("Fatal Frame", 3), ("Siren", 9), ("Prey", 12), ("Soma", 6), ("Limbo", 3), ("Inside", 3)],
            seed: seed);

    private static RunState? LatestCompleted(Scenario s, Guid player) =>
        s.State.Runs.Values.Where(r => r.PlayerId == player && r.Status == RunStatus.Completed).OrderBy(r => r.RunId).LastOrDefault();

    private static ICommand? CommandFor(Scenario s, byte b)
    {
        var player = s.PlayerId(s_players[b % s_players.Length]);
        var difficulty = (Difficulty)((b / 2) % 4);
        var run = LatestCompleted(s, player);
        switch ((b / 8) % 12)
        {
            case 0: return new RollGame(player);
            case 1: return new StartRun(player);
            case 2:
            case 3: return new CompleteRun(player, difficulty, ChallengeDone: b % 3 == 0);
            case 4: return new DropRun(player);
            case 5: return new TechReroll(player, TechRerollReason.DoesNotLaunch, null);
            case 6:
            case 7:
                var vow = s_vows[(b / 3) % s_vows.Length];
                s.Act(new AdjustInventory(player, vow, null, "Для партии"));
                if (!s.Last.IsAccepted)
                {
                    return null;
                }

                return new UseItem(player, s.LastEvents<ObjectGiven>().Single().Item.InstanceId, null);
            case 8: return run is null ? null : new ChangeRunDifficulty(run.RunId, difficulty, "По пруфу");
            case 9: return run is null ? null : new CorrectRunHours(run.RunId, 3 + (b % 10), "Часы по HLTB");
            case 10: return run is null ? null : new WaiveRunCondition(run.RunId, "Неприменимо");
            default:
                if (b % 5 == 0)
                {
                    var policy = b % 2 == 0 ? UnmetConditionPolicy.Ignore : UnmetConditionPolicy.NoDiceKeepCoins;
                    return new ChangeRuleset(s.Ruleset with { Reward = s.Ruleset.Reward with { UnmetConditionPolicy = policy } });
                }

                return run is null ? null : new ApproveProof(run.RunId, difficulty, "По пруфу");
        }
    }

    private static Scenario Play(int seed, byte[] script, bool check = true)
    {
        var s = NewSeason(seed);
        foreach (var b in script ?? [])
        {
            s.Advance(TimeSpan.FromMinutes(37));
            var command = CommandFor(s, b);
            if (command is null)
            {
                continue;
            }

            s.Act(command);
            if (check && s.Last.IsAccepted)
            {
                CheckAfter(s);
            }
        }

        return s;
    }

    private static bool Holds(ConditionSpec c, Difficulty d) =>
        (c.DifficultyAtLeast is not { } atLeast || d >= atLeast) && (c.DifficultyAtMost is not { } atMost || d <= atMost);

    private static void Compatible(RunSnapshot snapshot)
    {
        var conditions = snapshot.Conditions.Select(c => c.Condition).ToList();
        if (conditions.Count == 0)
        {
            Assert.Null(snapshot.ConditionPolicy);
            return;
        }

        Assert.NotNull(snapshot.ConditionPolicy);
        List<Difficulty> atLeast = [.. conditions.Where(c => c.DifficultyAtLeast is not null).Select(c => c.DifficultyAtLeast!.Value)];
        List<Difficulty> atMost = [.. conditions.Where(c => c.DifficultyAtMost is not null).Select(c => c.DifficultyAtMost!.Value)];
        Assert.True(
            atLeast.Count == 0 || atMost.Count == 0 || atLeast.Max() <= atMost.Min(),
            $"Contradicting conditions in one snapshot: not lower than {string.Join("/", atLeast)}, not higher than {string.Join("/", atMost)}.");
    }

    private static void CheckAfter(Scenario s)
    {
        Assert.True(s.Last.Events.Count <= 50, $"{s.Last.Events.Count} events in one command.");

        foreach (var player in s.State.Players.Values)
        {
            if (player.Offer is { } offer)
            {
                Compatible(offer.Snapshot);
            }
        }

        foreach (var run in s.State.Runs.Values)
        {
            Compatible(run.Snapshot);
            if (run.Snapshot.Conditions.Count == 0)
            {
                Assert.Null(run.Condition);
                continue;
            }

            if (run.Status != RunStatus.Completed || run.Condition is not { } condition)
            {
                continue;
            }

            if (!condition.Waived && run.Difficulty is { } difficulty)
            {
                Assert.Equal(run.Snapshot.Conditions.All(c => Holds(c.Condition, difficulty)), condition.Met);
            }

            if (!condition.Met && !condition.Waived && run.Snapshot.ConditionPolicy == UnmetConditionPolicy.NoDiceKeepCoins)
            {
                Assert.Empty(run.Dice);
                Assert.Empty(run.ChallengeDice);
            }
        }

        foreach (var check in s.Last.Events.OfType<RunConditionChecked>().GroupBy(c => c.RunId).Select(g => g.Last()))
        {
            var condition = s.State.Runs[check.RunId].Condition;
            Assert.NotNull(condition);
            Assert.Equal(check.Met, condition.Met);
            Assert.True(!check.Waived || condition.Waived, "A waived check left the run not waived.");
        }
    }

    [Property(MaxTest = 150)]
    public void Invariants_of_run_conditions_hold_after_every_command(int seed, byte[] script)
    {
        var s = Play(seed, script);

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Property(MaxTest = 40)]
    public void Same_seed_and_commands_give_the_same_log_with_run_conditions(int seed, byte[] script)
    {
        var first = Play(seed, script, check: false);
        var second = Play(seed, script, check: false);

        Assert.Equal(first.Log, second.Log);
    }
}

using FsCheck.Xunit;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Invariants;

/// <summary>
/// Random seasons where the admin changes the rules between players' actions (S1, S2, C3).
/// After every command: the version in the state counts the ruleset events of the log, every offer and run
/// holds the rules of the version it was rolled under, and a run's snapshot never changes.
/// Each byte of <c>script</c> is one command.
/// </summary>
public class RulesetInvariantTests
{
    private static readonly string[] s_players = ["Вася", "Петя"];

    private static Scenario NewSeason(int seed) =>
        Scenario.New(seed: seed)
            .WithMapLength(30)
            .WithCategory("Horror", weight: 2)
            .WithGame("Silent Hill", 12, "Horror")
            .WithGame("Alan Wake", 15, "Horror")
            .WithGame("Unknown Length", null, "Horror")
            .WithCategory("Puzzle", weight: 1)
            .WithGame("Tetris", 2, "Puzzle")
            .WithGame("Baba Is You", 7.5m, "Puzzle")
            .WithPlayers(s_players);

    private static ICommand CommandFor(Scenario s, byte b)
    {
        var player = s.PlayerId(s_players[b % s_players.Length]);
        var r = s.Ruleset;
        return ((b / 2) % 5) switch
        {
            0 => new RollGame(player),
            1 => new StartRun(player),
            2 => new CompleteRun(player, (Difficulty)((b / 10) % 4), EstimatedHours: 1 + ((b / 40) % 6), HoursSource: "HLTB"),
            // The admin changes numbers that go into the snapshot; some changes repeat the current value (unchanged)
            3 => new ChangeRuleset(r with
            {
                Reward = r.Reward with { DiceCount = r.Reward.DiceCount with { HoursPerDie = 1 + ((b / 10) % 4) } },
            }),
            _ => new ChangeRuleset(r with
            {
                Reward = r.Reward with
                {
                    DieByDifficulty = r.Reward.DieByDifficulty with { Normal = new DieRule { Sides = 2 + ((b / 10) % 5) } },
                },
            }),
        };
    }

    [Property(MaxTest = 200)]
    public void Snapshots_hold_the_rules_of_their_version_after_every_command(int seed, byte[] script)
    {
        var s = NewSeason(seed);
        foreach (var b in script)
        {
            var before = s.State;
            s.Act(CommandFor(s, b));
            Check(s, before);
        }
    }

    private static void Check(Scenario s, SeasonState before)
    {
        // Versions are the season's ruleset events: 1 at creation, +1 per change
        var versions = new Dictionary<int, Ruleset>();
        foreach (var e in s.Log)
        {
            switch (e)
            {
                case SeasonCreated created:
                    versions[1] = created.Ruleset;
                    break;
                case RulesetChanged changed:
                    Assert.Equal(versions.Count + 1, changed.Version);
                    versions[changed.Version] = changed.Ruleset;
                    break;
            }
        }

        Assert.Equal(versions.Count, s.State.RulesetVersion);
        Assert.Equal(versions[s.State.RulesetVersion], s.State.Rules);

        // Consecutive versions differ: an unchanged ruleset is never stored
        for (var v = 2; v <= versions.Count; v++)
        {
            Assert.NotEqual(versions[v - 1], versions[v]);
        }

        // Every offer and run plays by the rules of the version it was rolled under
        var snapshots = s.State.Players.Values.Where(p => p.Offer is not null).Select(p => p.Offer!.Snapshot)
            .Concat(s.State.Runs.Values.Select(r => r.Snapshot));
        foreach (var snapshot in snapshots)
        {
            Assert.InRange(snapshot.RulesetVersion, 1, s.State.RulesetVersion);
            var rules = versions[snapshot.RulesetVersion];
            Assert.Equal(rules.Reward.DiceCount, snapshot.DiceCount);
            Assert.Equal(rules.Reward.DieByDifficulty, snapshot.DieByDifficulty);
        }

        // A run's snapshot never changes after it was created
        foreach (var run in before.Runs.Values)
        {
            Assert.Equal(run.Snapshot, s.State.Runs[run.RunId].Snapshot);
        }

        // Dice of a completed run have the sides of its snapshot
        foreach (var run in s.State.Runs.Values.Where(r => r.Difficulty is not null))
        {
            var die = run.Difficulty switch
            {
                Difficulty.Easy => run.Snapshot.DieByDifficulty.Easy,
                Difficulty.Normal => run.Snapshot.DieByDifficulty.Normal,
                Difficulty.Hard => run.Snapshot.DieByDifficulty.Hard,
                _ => run.Snapshot.DieByDifficulty.Extreme,
            };
            Assert.All(run.Dice, d => Assert.Equal(die.Sides, d.Sides));
        }
    }
}

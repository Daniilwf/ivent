using FsCheck.Xunit;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Invariants;

/// <summary>
/// Random seasons of the slice: players roll, start and complete in any order (including invalid commands),
/// and after every command the invariants of docs/TESTING.md that the slice can break must hold.
/// Each byte of <c>script</c> is one command; <c>seed</c> drives the engine's random source.
/// </summary>
public class SliceInvariantTests
{
    private const int MapLength = 25;

    private static readonly string[] s_players = ["Вася", "Петя", "Маша"];

    private static Scenario NewSeason(int seed) =>
        Scenario.New(seed: seed)
            .WithMapLength(MapLength)
            .WithCategory("Horror", weight: 3)
            .WithGame("Silent Hill", 12, "Horror")
            .WithGame("Alan Wake", 15, "Horror", "Action")
            .WithDeletedGame("Deleted Horror", 5, "Horror")
            .WithCategory("Puzzle", weight: 2)
            .WithGame("Tetris", 2, "Puzzle")
            .WithGame("Unknown Length", null, "Puzzle")
            .WithGame("Baba Is You", 7.5m, "Puzzle")
            .WithCategory("Action", weight: 1)
            .WithGame("Doom", 4, "Action")
            .WithCategory("Empty", weight: 5)
            .WithPlayers(s_players);

    private static ICommand CommandFor(Scenario s, byte b)
    {
        var player = s.PlayerId(s_players[b % s_players.Length]);
        var difficulty = (Difficulty)((b / 12) % 4);
        return ((b / 3) % 4) switch
        {
            0 => new RollGame(player),
            1 => new StartRun(player),
            2 => new CompleteRun(player, difficulty, EstimatedHours: 1 + ((b / 48) % 9)), // bits independent of the player
            _ => new CompleteRun(player, difficulty),
        };
    }

    private static Scenario Play(int seed, byte[] script, Action<Scenario, SeasonState, int>? afterEach = null)
    {
        var s = NewSeason(seed);
        foreach (var b in script)
        {
            var before = s.State;
            var logLength = s.Log.Count;
            s.Act(CommandFor(s, b));
            afterEach?.Invoke(s, before, logLength);
        }

        return s;
    }

    [Property(MaxTest = 200)]
    public void Invariants_hold_after_every_command(int seed, byte[] script) =>
        Play(seed, script, CheckInvariants);

    [Property(MaxTest = 50)]
    public void Same_seed_and_commands_give_the_same_log(int seed, byte[] script)
    {
        // Invariant 14
        var first = Play(seed, script);
        var second = Play(seed, script);

        Assert.Equal(first.Log, second.Log);
    }

    private static void CheckInvariants(Scenario s, SeasonState before, int logLengthBefore)
    {
        // A rejected command has no events and changes nothing (TEST_MATRIX, stage 1 extra)
        if (!s.Last.IsAccepted)
        {
            ScenarioAssert.Rejected(s, before, logLengthBefore, s.Last.Rejection!.Code);
        }

        // 1. Replaying the log gives the stored state
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));

        foreach (var player in s.State.Players.Values)
        {
            // 2. Points equal the sum of point deltas in the log
            var deltas = s.Log.OfType<PointsChanged>().Where(e => e.PlayerId == player.PlayerId).Sum(e => e.Delta);
            Assert.Equal(deltas, player.Points);

            // 3. No more active runs than the ruleset allows; the active run is the playing one
            var playing = s.State.Runs.Values.Where(r => r.PlayerId == player.PlayerId && r.Status == RunStatus.Playing).ToList();
            Assert.True(playing.Count <= s.Ruleset.Season.MaxActiveRunsPerPlayer);
            Assert.Equal(playing.SingleOrDefault()?.RunId, player.ActiveRunId);
            Assert.Equal(player.Phase == TurnPhase.Playing, player.ActiveRunId is not null);
            Assert.Equal(player.Phase == TurnPhase.Rolling, player.Offer is not null);

            // 7. The token is on an existing cell; on the linear map it stands at min(sum of steps, length)
            var cellIndex = s.State.Map.Cells.ToList().FindIndex(c => c.Id == player.CellId);
            Assert.True(cellIndex >= 0, $"Cell {player.CellId} is not on the map.");
            var dice = s.Log.OfType<CompletionRolled>().Where(e => e.PlayerId == player.PlayerId).Sum(e => e.Dice.Sum(d => d.Value));
            Assert.Equal(Math.Min(dice, MapLength), cellIndex);

            // Slice: points equal the completion dice sum (no other point sources yet)
            Assert.Equal(dice, player.Points);
        }

        // 4 / G9. A game is busy for at most one player: offered or played
        var busy = s.State.Players.Values.Where(p => p.Offer is not null).Select(p => p.Offer!.GameId)
            .Concat(s.State.Runs.Values.Where(r => r.Status == RunStatus.Playing).Select(r => r.GameId))
            .ToList();
        Assert.Equal(busy.Count, busy.Distinct().Count());

        // 5. A game completed in the season is never rolled afterwards; G3: a deleted game is never rolled
        var completedGames = new HashSet<Guid>();
        var runs = new Dictionary<Guid, Guid>();
        foreach (var e in s.Log)
        {
            switch (e)
            {
                case GameRolled rolled:
                    Assert.DoesNotContain(rolled.GameId, completedGames);
                    Assert.NotEqual(s.GameId("Deleted Horror"), rolled.GameId);
                    Assert.All(rolled.Misses, m => Assert.NotEqual(rolled.GameId, m.GameId));
                    break;
                case RunStarted started:
                    runs[started.RunId] = started.GameId;
                    break;
                case RunCompleted completed:
                    completedGames.Add(runs[completed.RunId]);
                    break;
            }
        }

        // Stage 1 extra: dice of every completed run match its hours and roll-time snapshot
        foreach (var run in s.State.Runs.Values.Where(r => r.Status == RunStatus.Completed))
        {
            Assert.NotNull(run.Hours);
            Assert.NotNull(run.Difficulty);
            Assert.Equal(ExpectedDiceCount(run.Snapshot.DiceCount, run.Hours.Value), run.Dice.Count);
            var sides = SidesFor(run.Snapshot.DieByDifficulty, run.Difficulty.Value);
            Assert.All(run.Dice, d =>
            {
                Assert.Equal(sides, d.Sides);
                Assert.InRange(d.Value, 1, sides);
            });
        }
    }

    /// <summary>Reference formula from D-13, independent of the engine.</summary>
    private static int ExpectedDiceCount(DiceCountRule rule, decimal hours)
    {
        var raw = hours / rule.HoursPerDie;
        var rounded = rule.Rounding switch
        {
            Rounding.Nearest => Math.Round(raw, MidpointRounding.AwayFromZero),
            Rounding.Floor => Math.Floor(raw),
            Rounding.Ceil => Math.Ceiling(raw),
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };
        return Math.Clamp((int)rounded, rule.Min, rule.Max);
    }

    private static int SidesFor(DieByDifficulty rule, Difficulty difficulty) =>
        difficulty switch
        {
            Difficulty.Easy => rule.Easy.Sides,
            Difficulty.Normal => rule.Normal.Sides,
            Difficulty.Hard => rule.Hard.Sides,
            Difficulty.Extreme => rule.Extreme.Sides,
            _ => throw new ArgumentOutOfRangeException(nameof(difficulty)),
        };
}

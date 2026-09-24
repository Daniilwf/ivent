using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Seasons;

/// <summary>
/// The thin slice end to end: roll → start → complete → dice → the token moves.
/// The log is the source of truth: replaying it gives the same state, the same seed and commands give
/// the same log, and every event survives the JSON round trip (SPEC «Архитектура движка правил», L2, invariant 14).
/// </summary>
public class SliceScenarioTests
{
    private static Scenario PlayedSeason(int seed = 42)
    {
        var s = Scenario.New(seed: seed)
            .WithMapLength(20)
            .WithCategory("Horror", weight: 2).WithGame("Silent Hill", 12, "Horror").WithGame("Alan Wake", 15, "Horror")
            .WithCategory("Puzzle", weight: 1).WithGame("Tetris", 2, "Puzzle").WithGame("Unknown Length", null, "Puzzle")
            .WithPlayers("Вася", "Петя", "Маша");

        s.Roll("Вася").Advance(TimeSpan.FromMinutes(5))
            .Roll("Петя").Start("Вася").Start("Петя")
            .Advance(TimeSpan.FromHours(3))
            .Act(CompleteWithHours(s, "Вася", Difficulty.Hard))
            .Roll("Маша") // may land anywhere still available
            .Act(CompleteWithHours(s, "Петя", Difficulty.Easy))
            .ExpectRejection().Roll("Вася") // rejected if nothing is left for him; the log stays consistent either way
            .ExpectRejection().Complete("Маша"); // rejected: Маша is Rolling, not Playing
        return s;
    }

    /// <summary>Completes the active run, giving an estimate only when the snapshot has no hours.</summary>
    private static CompleteRun CompleteWithHours(Scenario s, string player, Difficulty difficulty)
    {
        var runId = s.Player(player).ActiveRunId;
        var needsEstimate = runId is { } id && s.State.Runs[id].Snapshot.Hours is null;
        return new CompleteRun(s.PlayerId(player), difficulty, needsEstimate ? 4 : null);
    }

    [Fact]
    public void Roll_start_complete_moves_the_token_by_the_dice()
    {
        // Given a 20-step map, a 6-hour game and two players
        var s = Scenario.New()
            .WithMapLength(20)
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Alan Wake", 9, "Horror")
            .WithPlayers("Вася", "Петя");

        // When Вася rolls, starts and completes on hard with dice 6 and 5
        s.Roll("Вася");
        ScenarioAssert.Accepted(s);
        var game = s.Player("Вася").Offer!.GameId;
        s.Start("Вася");
        ScenarioAssert.Accepted(s);
        var runId = s.Player("Вася").ActiveRunId!.Value;
        var diceCount = s.GameId("Silent Hill") == game ? 2 : 3;
        var faces = new[] { 6, 5, 4 }.Take(diceCount).ToArray();
        s.NextRandom(faces).Complete("Вася", Difficulty.Hard);

        // Then the log tells the whole story in order and the token stands on the cell of the dice sum
        ScenarioAssert.Accepted(s);
        var sum = faces.Sum();
        Assert.Equal(
            [typeof(SeasonCreated), typeof(SeasonStatusChanged), typeof(SeasonPlayerAdded), typeof(SeasonPlayerAdded), typeof(GameRolled), typeof(RunStarted)],
            s.Log.Take(6).Select(e => e.GetType()));
        Assert.Equal(sum, s.Player("Вася").Points);
        Assert.Equal($"c{sum}", s.Player("Вася").CellId);
        Assert.Equal(RunStatus.Completed, s.State.Runs[runId].Status);
        Assert.Equal(faces, s.State.Runs[runId].Dice.Select(d => d.Value));
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);

        // And Петя is untouched
        Assert.Equal(0, s.Player("Петя").Points);
        Assert.Equal("start", s.Player("Петя").CellId);
        Assert.Equal(TurnPhase.Idle, s.Player("Петя").Phase);
    }

    // ---- Replay (L2, L4, invariant 1) ----

    [Fact]
    public void Replaying_the_log_gives_the_current_state()
    {
        var s = PlayedSeason();

        Assert.Contains(s.Log, e => e is CompletionRolled);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void Replaying_every_prefix_of_the_log_matches_the_state_after_each_command()
    {
        // Given a scenario where the state is recorded after each accepted command
        var s = Scenario.New()
            .WithMapLength(10)
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Alan Wake", 9, "Horror")
            .WithPlayers("Вася", "Петя");
        var checkpoints = new List<(int LogLength, SeasonState State)> { (s.Log.Count, s.State) };
        foreach (var step in new Func<Scenario, Scenario>[]
        {
            x => x.Roll("Вася"),
            x => x.Roll("Петя"),
            x => x.Start("Петя"),
            x => x.Start("Вася"),
            x => x.Complete("Петя", Difficulty.Hard),
            x => x.Complete("Вася", Difficulty.Easy),
        })
        {
            step(s);
            ScenarioAssert.Accepted(s);
            checkpoints.Add((s.Log.Count, s.State));
        }

        // Then replaying each prefix gives exactly that state
        Assert.All(checkpoints, c => Assert.Equal(c.State, SeasonEngine.Replay(s.Log.Take(c.LogLength))));
    }

    [Fact]
    public void Replaying_the_log_read_back_from_storage_gives_the_same_state()
    {
        // Given a played season whose log goes through the storage format (type, version, JSON data)
        var s = PlayedSeason();
        var stored = s.Log.Select(EventCodec.Encode).ToList();

        // When the log is decoded and replayed without the pool or the ruleset
        var replayed = SeasonEngine.Replay(stored.Select(EventCodec.Decode));

        // Then it is the same state: events hold results, not intentions
        Assert.Equal(s.State, replayed);
    }

    // ---- Determinism (invariant 14) ----

    [Fact]
    public void Same_seed_and_commands_give_the_same_log()
    {
        var first = PlayedSeason(seed: 7);
        var second = PlayedSeason(seed: 7);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.Log.Select(EventCodec.Encode), second.Log.Select(EventCodec.Encode));
        Assert.Equal(first.State, second.State);
    }

    // ---- Event serialization (L6 groundwork) ----

    [Fact]
    public void Every_event_of_the_slice_survives_the_json_round_trip()
    {
        var s = PlayedSeason();

        // The log covers every event type of the slice
        Assert.Contains(s.Log, e => e is GameRolled);
        Assert.Contains(s.Log, e => e is RunStarted);
        Assert.Contains(s.Log, e => e is RunCompleted);
        Assert.Contains(s.Log, e => e is CompletionRolled);
        Assert.Contains(s.Log, e => e is PointsChanged);
        Assert.Contains(s.Log, e => e is PlayerMoved);

        Assert.All(s.Log, e =>
        {
            var stored = EventCodec.Encode(e);
            var back = EventCodec.Decode(stored);
            Assert.Equal(e.GetType(), back.GetType());
            Assert.Equal(e, back);
            Assert.Equal(stored, EventCodec.Encode(back));
            Assert.Equal(EventCatalog.Describe(e.GetType()).Name, stored.Type);
            Assert.Equal(EventCatalog.Describe(e.GetType()).Version, stored.Version);
        });
    }

    [Fact]
    public void Roll_misses_survive_the_json_round_trip()
    {
        var ruleset = Scenario.New().Ruleset;
        var rolled = new GameRolled(
            SequentialIds.Make(1, 1),
            "Horror",
            [new RollMiss(SequentialIds.Make(2, 1), RollMissReason.BeingPlayed, SequentialIds.Make(1, 2)),
             new RollMiss(SequentialIds.Make(2, 2), RollMissReason.CompletedInSeason, SequentialIds.Make(1, 3))],
            SequentialIds.Make(2, 3),
            new RunSnapshot(ruleset.Version, 7.5m, ruleset.Reward.DiceCount, ruleset.Reward.DieByDifficulty),
            FixedClock.SeasonStart);

        Assert.Equal(rolled, EventCodec.Decode(EventCodec.Encode(rolled)));
    }
}

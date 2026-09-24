using FsCheck.Xunit;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Invariants;

/// <summary>
/// Random seasons of the slice: players roll, start and complete in any order (including invalid commands),
/// and after every command the invariants of docs/TESTING.md that the slice can break must hold.
/// Each byte of <c>script</c> is one command; <c>seed</c> drives the engine's random source.
/// The choice variant (C4, D-91) plays with <c>roll.choiceCount</c> 3 and adds <see cref="MakeChoice"/> to the script.
/// The «Уже проходил» variant (C5, D-92) adds <see cref="DeclareAlreadyPlayed"/>, with and without a choice.
/// </summary>
public class SliceInvariantTests
{
    private const int MapLength = 25;

    private static readonly string[] s_players = ["Вася", "Петя", "Маша"];

    private static readonly string[] s_games = ["Silent Hill", "Alan Wake", "Deleted Horror", "Tetris", "Unknown Length", "Baba Is You", "Doom"];

    private const int ChoiceCount = 3;

    private static Scenario NewSeason(int seed, bool withChoice = false) =>
        Scenario.New(seed: seed)
            .WithRuleset(r => withChoice ? r with { Roll = r.Roll with { ChoiceCount = ChoiceCount } } : r)
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

    /// <summary>
    /// With a choice every fifth kind of command is MakeChoice: the pending choice's id when there is one (bits 5–7
    /// pick the option, one past the last is an unknown option), otherwise a made-up id.
    /// </summary>
    private static ICommand ChoiceCommandFor(Scenario s, byte b)
    {
        var player = s.PlayerId(s_players[b % s_players.Length]);
        if ((b / 3) % 5 != 4)
        {
            return CommandFor(s, b);
        }

        if (s.State.Players[player].Choice is not { } choice)
        {
            return new MakeChoice(player, SequentialIds.Make(0x50000000, b), "none");
        }

        var index = (b / 32) % (choice.Options.Count + 1);
        return new MakeChoice(player, choice.ChoiceId, index < choice.Options.Count ? choice.Options[index].Id : "unknown");
    }

    /// <summary>
    /// Every sixth kind of command is «Уже проходил»: bits 5–7 pick the offered game or an option (most often), or a
    /// pool game that is usually not offered (a refusal to check).
    /// </summary>
    private static ICommand ExclusionCommandFor(Scenario s, byte b, bool withChoice)
    {
        var player = s.PlayerId(s_players[b % s_players.Length]);
        if ((b / 3) % 6 != 5)
        {
            return withChoice ? ChoiceCommandFor(s, b) : CommandFor(s, b);
        }

        var p = s.State.Players[player];
        List<Guid> offered = p.Offer is { } offer
            ? [offer.GameId]
            : p.Choice?.Options.Select(o => o.Game!.GameId).ToList() ?? [];
        var pick = b / 32;
        var game = pick < 5 && offered.Count > 0 ? offered[pick % offered.Count] : s.GameId(s_games[pick % s_games.Length]);
        return new DeclareAlreadyPlayed(player, game);
    }

    private static Scenario Play(
        int seed, byte[] script, Action<Scenario, SeasonState, int>? afterEach = null, bool withChoice = false, bool withExclusions = false)
    {
        var s = NewSeason(seed, withChoice);
        foreach (var b in script)
        {
            var before = s.State;
            var logLength = s.Log.Count;
            s.Act(withExclusions ? ExclusionCommandFor(s, b, withChoice) : withChoice ? ChoiceCommandFor(s, b) : CommandFor(s, b));
            afterEach?.Invoke(s, before, logLength);
        }

        return s;
    }

    [Property(MaxTest = 200)]
    public void Invariants_hold_after_every_command(int seed, byte[] script) =>
        Play(seed, script, CheckInvariants);

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_a_choice_of_games(int seed, byte[] script) =>
        Play(seed, script, CheckInvariants, withChoice: true);

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_already_played(int seed, byte[] script) =>
        Play(seed, script, CheckInvariants, withExclusions: true);

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_already_played_and_a_choice_of_games(int seed, byte[] script) =>
        Play(seed, script, CheckInvariants, withChoice: true, withExclusions: true);

    [Property(MaxTest = 50)]
    public void Same_seed_and_commands_give_the_same_log_with_already_played(int seed, byte[] script)
    {
        // Invariant 14: the free roll after «Уже проходил» comes from the seeded random source only
        var first = Play(seed, script, withChoice: true, withExclusions: true);
        var second = Play(seed, script, withChoice: true, withExclusions: true);

        Assert.Equal(first.Log, second.Log);
    }

    [Property(MaxTest = 50)]
    public void Same_seed_and_commands_give_the_same_log_with_a_choice_of_games(int seed, byte[] script)
    {
        // Invariant 14: the options come from the seeded random source only
        var first = Play(seed, script, withChoice: true);
        var second = Play(seed, script, withChoice: true);

        Assert.Equal(first.Log, second.Log);
    }

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

        // T2 (D-91, Choosing --> Playing): an accepted choice is exactly ChoiceMade + RunStarted of the chosen option,
        // with the option's roll-time snapshot and roll time
        if (s.Last.IsAccepted && s.Last.Events.OfType<ChoiceMade>().SingleOrDefault() is { } made)
        {
            Assert.Equal(2, s.Last.Events.Count);
            Assert.Same(made, s.Last.Events[0]);
            var started = Assert.IsType<RunStarted>(s.Last.Events[1]);
            var option = before.Players[made.PlayerId].Choice!.Options.Single(o => o.Id == made.OptionId).Game!;
            Assert.Equal(made.PlayerId, started.PlayerId);
            Assert.Equal(option.GameId, started.GameId);
            Assert.Equal(option.Snapshot, started.Snapshot);
            Assert.Equal(option.RolledAt, started.RolledAt);
            Assert.Equal(TurnPhase.Playing, s.State.Players[made.PlayerId].Phase);
        }

        // G8 (D-92): an accepted «Уже проходил» is GameExcluded of an offered game or option, then at most one free roll
        // of the same player; it spends nothing
        if (s.Last.IsAccepted && s.Last.Events.OfType<GameExcluded>().SingleOrDefault() is { } excluded)
        {
            Assert.Same(excluded, s.Last.Events[0]);
            Assert.Equal(ExclusionReason.AlreadyPlayed, excluded.Reason);
            var was = before.Players[excluded.PlayerId];
            List<Guid> offeredBefore = was.Offer is { } o ? [o.GameId] : was.Choice?.Options.Select(x => x.Game!.GameId).ToList() ?? [];
            Assert.Contains(excluded.GameId, offeredBefore);
            Assert.InRange(s.Last.Events.Count, 1, 2);
            var now = s.State.Players[excluded.PlayerId];
            if (s.Last.Events.Count == 2)
            {
                Assert.True(
                    (s.Last.Events[1] is GameRolled r && r.PlayerId == excluded.PlayerId)
                    || (s.Last.Events[1] is GameChoiceRolled c && c.PlayerId == excluded.PlayerId),
                    $"Unexpected {s.Last.Events[1]}.");
                Assert.Equal(TurnPhase.Rolling, now.Phase);
            }
            else
            {
                Assert.Equal(TurnPhase.Idle, now.Phase);
            }

            Assert.Equal((was.Coins, was.Points, was.Resources, was.CellId), (now.Coins, now.Points, now.Resources, now.CellId));
        }

        // G10 / D-92: one check «is there anything to roll». An accepted roll from Idle means CanRoll was true before it
        var pool = s.Context().Pool;
        Guid? roller = s.Last.IsAccepted
            ? (s.Last.Events.Count > 0 ? s.Last.Events[0] : null) switch
            {
                GameRolled r => r.PlayerId,
                GameChoiceRolled c => c.PlayerId,
                _ => null,
            }
            : null;
        if (roller is { } rollerId && before.Players.TryGetValue(rollerId, out var rollerBefore) && rollerBefore.Phase == TurnPhase.Idle)
        {
            Assert.True(Rolling.CanRoll(before, rollerId, pool, []), "A roll was accepted although CanRoll said there was nothing.");
        }

        // G10 / D-92: the admin signal lists exactly the active idle players of a running season whose roll finds nothing
        var expectedWithout = s.State.Status == SeasonStatus.Active
            ? s.State.Players.Values
                .Where(p => p.Phase == TurnPhase.Idle && !p.IsInactive && !Rolling.CanRoll(s.State, p.PlayerId, pool, []))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => p.PlayerId)
                .ToList()
            : [];
        Assert.Equal(expectedWithout, PoolStats.PlayersWithoutGames(s.State, pool));

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
            // T2 (D-91): Rolling means an offer or a pending choice, never both; a choice only while Rolling
            Assert.Equal(player.Phase == TurnPhase.Rolling, player.Offer is not null || player.Choice is not null);
            Assert.False(player.Offer is not null && player.Choice is not null, "Both an offer and a pending choice.");
            if (player.Choice is { } choice)
            {
                Assert.Equal(TurnPhase.Rolling, player.Phase);
                Assert.Equal(ChoiceKind.Game, choice.Kind);
                Assert.InRange(choice.Options.Count, 2, s.Ruleset.Roll.ChoiceCount);
                Assert.All(choice.Options, o => Assert.Equal(o.Game!.GameId.ToString("N"), o.Id));
            }

            // 7. The token is on an existing cell; on the linear map it stands at min(sum of steps, length)
            var cellIndex = s.State.Map.Cells.ToList().FindIndex(c => c.Id == player.CellId);
            Assert.True(cellIndex >= 0, $"Cell {player.CellId} is not on the map.");
            var dice = s.Log.OfType<CompletionRolled>().Where(e => e.PlayerId == player.PlayerId).Sum(e => e.Dice.Sum(d => d.Value));
            Assert.Equal(Math.Min(dice, MapLength), cellIndex);

            // Slice: points equal the completion dice sum (no other point sources yet)
            Assert.Equal(dice, player.Points);

            // G8 / D-92: exclusions are the player's GameExcluded events, one per game, ordered by game id
            var logged = s.Log.OfType<GameExcluded>().Where(e => e.PlayerId == player.PlayerId).ToList();
            Assert.Equal(logged.Count, logged.Select(e => e.GameId).Distinct().Count());
            Assert.Equal(
                logged.Select(e => new GameExclusion(e.GameId, e.Reason)).OrderBy(x => x.GameId),
                player.Exclusions);

            // Exclusions never shrink
            if (before.Players.TryGetValue(player.PlayerId, out var earlier))
            {
                Assert.All(earlier.Exclusions, x => Assert.Contains(x, player.Exclusions));
            }

            // An excluded game is never the player's offer, option or active run
            var excludedNow = player.Exclusions.Select(x => x.GameId).ToHashSet();
            Assert.False(player.Offer is { } offer && excludedNow.Contains(offer.GameId), "An excluded game is offered.");
            Assert.DoesNotContain(player.Choice?.Options.Select(o => o.Game!.GameId) ?? [], excludedNow.Contains);
            Assert.False(player.ActiveRunId is { } active && excludedNow.Contains(s.State.Runs[active].GameId), "An excluded game is played.");
        }

        // 4 / G9. A game is busy for at most one player: offered, among pending options (D-06) or played
        var busy = s.State.Players.Values.Where(p => p.Offer is not null).Select(p => p.Offer!.GameId)
            .Concat(s.State.Players.Values.Where(p => p.Choice is not null).SelectMany(p => p.Choice!.Options.Select(o => o.Game!.GameId)))
            .Concat(s.State.Runs.Values.Where(r => r.Status == RunStatus.Playing).Select(r => r.GameId))
            .ToList();
        Assert.Equal(busy.Count, busy.Distinct().Count());

        // 5. A game completed in the season is never rolled afterwards; G3: a deleted game is never rolled
        // G8 / D-05: after «Уже проходил» the game never comes to that player again, not even as a miss
        var completedGames = new HashSet<Guid>();
        var runs = new Dictionary<Guid, Guid>();
        var excludedFor = new Dictionary<Guid, HashSet<Guid>>();
        HashSet<Guid> ExcludedFor(Guid player) => excludedFor.TryGetValue(player, out var set) ? set : [];
        foreach (var e in s.Log)
        {
            switch (e)
            {
                case GameExcluded gameExcluded:
                    if (!excludedFor.TryGetValue(gameExcluded.PlayerId, out var games))
                    {
                        excludedFor[gameExcluded.PlayerId] = games = [];
                    }

                    games.Add(gameExcluded.GameId);
                    break;
                case GameRolled rolled:
                    Assert.DoesNotContain(rolled.GameId, ExcludedFor(rolled.PlayerId));
                    Assert.DoesNotContain(rolled.Misses, m => ExcludedFor(rolled.PlayerId).Contains(m.GameId));
                    Assert.DoesNotContain(rolled.GameId, completedGames);
                    Assert.NotEqual(s.GameId("Deleted Horror"), rolled.GameId);
                    Assert.All(rolled.Misses, m => Assert.NotEqual(rolled.GameId, m.GameId));
                    break;
                case GameChoiceRolled choiceRolled:
                    // D-06: 2..N distinct games, none completed or deleted, none among the misses
                    var offered = choiceRolled.Offers.Select(o => o.GameId).ToList();
                    Assert.DoesNotContain(offered, ExcludedFor(choiceRolled.PlayerId).Contains);
                    Assert.DoesNotContain(choiceRolled.Misses, m => ExcludedFor(choiceRolled.PlayerId).Contains(m.GameId));
                    Assert.InRange(offered.Count, 2, ChoiceCount);
                    Assert.Equal(offered.Count, offered.Distinct().Count());
                    Assert.All(offered, g =>
                    {
                        Assert.DoesNotContain(g, completedGames);
                        Assert.NotEqual(s.GameId("Deleted Horror"), g);
                        Assert.DoesNotContain(g, choiceRolled.Misses.Select(m => m.GameId));
                    });
                    break;
                case RunStarted started:
                    Assert.DoesNotContain(started.GameId, ExcludedFor(started.PlayerId));
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

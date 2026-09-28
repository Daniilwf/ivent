using FsCheck.Xunit;
using GameEvent.Engine.Effects;
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

    /// <remarks>The script claims challenges, so the season turns <c>features.challenges</c> on (D-96 (1)) unless stated.</remarks>
    private static Scenario NewSeason(int seed, bool withChoice = false, bool challenges = true) =>
        Scenario.New(seed: seed)
            .WithRuleset(r => withChoice ? r with { Roll = r.Roll with { ChoiceCount = ChoiceCount } } : r)
            .WithRuleset(r => r with { Features = r.Features with { Challenges = challenges } })
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
            // bits independent of the player; odd bytes claim the challenge (D-96)
            2 => new CompleteRun(player, difficulty, EstimatedHours: 1 + ((b / 48) % 9), HoursSource: "HLTB", ChallengeDone: b % 2 == 1),

            // half of these carry a review, its rating 0..11 is sometimes out of range and then refuses the completion
            _ => new CompleteRun(player, difficulty, ChallengeDone: b % 2 == 0, Review: b % 4 < 2 ? new RunReview(b % 12, (b % 8) switch { 0 => " ", 4 => " отзыв  ", _ => "отзыв" }) : null),
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
        int seed,
        byte[] script,
        Action<Scenario, SeasonState, int>? afterEach = null,
        bool withChoice = false,
        bool withExclusions = false,
        bool challenges = true)
    {
        // C13: a failure prints the (shrunk) game as builder code
        return NewSeason(seed, withChoice, challenges).Explained(s =>
        {
            foreach (var b in script)
            {
                var before = s.State;
                var logLength = s.Log.Count;
                var command = withExclusions ? ExclusionCommandFor(s, b, withChoice) : withChoice ? ChoiceCommandFor(s, b) : CommandFor(s, b);
                s.Act(command);
                afterEach?.Invoke(s, before, logLength);

                // W3 / D-96 (1): with features.challenges off a claim is never accepted
                if (!challenges && command is CompleteRun { ChallengeDone: true })
                {
                    Assert.False(s.Last.IsAccepted, "A challenge was claimed while features.challenges is off.");
                }
            }

            if (!challenges)
            {
                Assert.DoesNotContain(s.Log.OfType<RunCompleted>(), e => e.ChallengeDone);
                Assert.All(s.Log.OfType<CompletionRolled>(), e => Assert.Empty(e.ChallengeDice));
            }
        });
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

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_challenges_off(int seed, byte[] script) =>
        Play(seed, script, CheckInvariants, challenges: false);

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

            // RR1 / D-93: no rerolls in this script, and «Уже проходил» is not one: the counter stays 0
            Assert.Equal(0, player.RerollsThisRoll);
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
            // W3 / D-96: the challenge dice count in points and steps as well
            var dice = s.Log.OfType<CompletionRolled>().Where(e => e.PlayerId == player.PlayerId)
                .Sum(e => e.Dice.Sum(d => d.Value) + e.ChallengeDice.Sum(d => d.Value));
            Assert.Equal(Math.Min(dice, MapLength), cellIndex);

            // Slice: points come only from completions — each counted one gives its dice sum (a frozen first's give none,
            // D-99) — and from finish bonuses (C9a, Q-4)
            var changes = s.Log.OfType<PointsChanged>().Where(e => e.PlayerId == player.PlayerId).ToList();
            Assert.All(changes, e => Assert.Contains(e.Reason, new[] { PointsReason.CompletionRoll, PointsReason.FinishBonus, PointsReason.FinishBonusRevoked }));
            var counted = changes.Where(e => e.Reason == PointsReason.CompletionRoll).Select(e => e.RunId).ToHashSet();
            var countedDice = s.Log.OfType<CompletionRolled>().Where(e => e.PlayerId == player.PlayerId && counted.Contains(e.RunId))
                .Sum(e => e.Dice.Sum(d => d.Value) + e.ChallengeDice.Sum(d => d.Value));
            var bonuses = changes.Where(e => e.Reason != PointsReason.CompletionRoll).Sum(e => e.Delta);
            Assert.Equal(countedDice + bonuses, player.Points);
            Assert.True(player.Finish?.Frozen == true || counted.Count == s.Log.OfType<CompletionRolled>().Count(e => e.PlayerId == player.PlayerId), "A completion gave no points to a player who is not frozen.");

            // W10 / Q-2 / D-96: coins equal their logged changes, and in the slice they come only from completions,
            // each by the formula from the run's roll-time snapshot and its counted hours
            Assert.Equal(s.Log.OfType<CoinsChanged>().Where(e => e.PlayerId == player.PlayerId).Sum(e => e.Delta), player.Coins);
            var expectedCoins = s.State.Runs.Values
                .Where(r => r.PlayerId == player.PlayerId && r.Status == RunStatus.Completed)
                .Sum(r => ExpectedCoins(r.Snapshot, r.Hours!.Value));
            Assert.Equal(expectedCoins, player.Coins);

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

        // D-16 / D-99: a game the first finisher completed in free mode stays available to others, not to himself
        var freeModeFor = new Dictionary<Guid, HashSet<Guid>>();
        HashSet<Guid> FreeModeFor(Guid player) => freeModeFor.TryGetValue(player, out var set) ? set : [];
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
                    Assert.DoesNotContain(rolled.GameId, FreeModeFor(rolled.PlayerId));
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
                        Assert.DoesNotContain(g, FreeModeFor(choiceRolled.PlayerId));
                        Assert.NotEqual(s.GameId("Deleted Horror"), g);
                        Assert.DoesNotContain(g, choiceRolled.Misses.Select(m => m.GameId));
                    });
                    break;
                case RunStarted started:
                    Assert.DoesNotContain(started.GameId, ExcludedFor(started.PlayerId));
                    runs[started.RunId] = started.GameId;
                    break;
                case RunCompleted { FreeMode: true } freeMode:
                    if (!freeModeFor.TryGetValue(freeMode.PlayerId, out var own))
                    {
                        freeModeFor[freeMode.PlayerId] = own = [];
                    }

                    own.Add(runs[freeMode.RunId]);
                    break;
                case RunCompleted completed:
                    completedGames.Add(runs[completed.RunId]);
                    break;
            }
        }

        // Stage 1 extra: dice of every completed run match its hours and roll-time snapshot
        var claimed = s.Log.OfType<RunCompleted>().ToDictionary(e => e.RunId, e => e.ChallengeDone);
        var reviews = new Dictionary<Guid, RunReview>();
        foreach (var reviewed in s.Log.OfType<RunReviewed>())
        {
            reviews[reviewed.RunId] = new RunReview(reviewed.Rating, reviewed.Text);
        }

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

            // W3 / D-96: challenge dice only when claimed, as many as the snapshot says, of the same type
            Assert.Equal(claimed[run.RunId] ? run.Snapshot.ChallengeExtraDice : 0, run.ChallengeDice.Count);
            Assert.All(run.ChallengeDice, d =>
            {
                Assert.Equal(sides, d.Sides);
                Assert.InRange(d.Value, 1, sides);
            });

            // W6 / D-96: a source only for an estimate, i.e. when the snapshot had no hours
            Assert.Equal(run.Snapshot.Hours is > 0 ? null : "HLTB", run.HoursSource);

            // W2 / D-96: a difficulty with grantEvent leaves exactly one manual effect for the run
            var grant = GrantFor(run.Snapshot.DieByDifficulty, run.Difficulty.Value);
            var effects = s.State.ManualEffects.Values.Where(e => e.RunId == run.RunId).ToList();
            if (grant is { } kind)
            {
                var effect = Assert.Single(effects);
                Assert.Equal(new PendingManualEffect(effect.EffectId, run.PlayerId, kind, ManualEffectSource.Difficulty, run.RunId), effect);
            }
            else
            {
                Assert.Empty(effects);
            }

            // W9 / D-96: the review is the latest logged one; the rating in 1..10, blank text is no text
            Assert.Equal(reviews.GetValueOrDefault(run.RunId), run.Review);
            if (run.Review is { } review)
            {
                Assert.InRange(review.Rating, 1, 10);
                Assert.False(review.Text is { } text && string.IsNullOrWhiteSpace(text), "A blank review text is stored.");
                Assert.Equal(review.Text?.Trim(), review.Text); // D-96 (4): stored trimmed
            }
        }

        // Nothing else is reviewed, and in the slice the only manual effects are the difficulty's
        Assert.All(reviews.Keys, id => Assert.Equal(RunStatus.Completed, s.State.Runs[id].Status));
        Assert.All(s.State.ManualEffects.Values, e => Assert.Equal(ManualEffectSource.Difficulty, e.Source));
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

    /// <summary>Reference formula from D-96 (2): <c>max(min, ⌊min(hours, diceCount.max × hoursPerDie) × perHour⌋)</c>.</summary>
    private static int ExpectedCoins(RunSnapshot snapshot, decimal hours)
    {
        var rule = snapshot.Coins;
        Assert.NotNull(rule);
        var counted = Math.Min(hours, snapshot.DiceCount.Max * snapshot.DiceCount.HoursPerDie);
        return Math.Max(rule.Min, (int)Math.Floor(counted * rule.PerHour));
    }

    private static EventKind? GrantFor(DieByDifficulty rule, Difficulty difficulty) =>
        difficulty switch
        {
            Difficulty.Easy => rule.Easy.GrantEvent,
            Difficulty.Normal => rule.Normal.GrantEvent,
            Difficulty.Hard => rule.Hard.GrantEvent,
            Difficulty.Extreme => rule.Extreme.GrantEvent,
            _ => throw new ArgumentOutOfRangeException(nameof(difficulty)),
        };

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

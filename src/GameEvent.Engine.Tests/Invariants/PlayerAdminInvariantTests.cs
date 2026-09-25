using FsCheck.Xunit;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Invariants;

/// <summary>
/// Random seasons with the admin in the game (C2): players roll, start and complete while the admin adjusts
/// points, coins, resources and positions, discards offers, toggles the inactive flag, adds a player mid-season,
/// moves the deadline and, rarely, the season status. After every command the invariants of docs/TESTING.md
/// this can break must hold; the expected values are folded from the log independently of the engine.
/// Each byte of <c>script</c> is one command; <c>seed</c> drives the engine's random source.
/// The choice variant (C4, D-91) plays with <c>roll.choiceCount</c> 3 and adds <see cref="MakeChoice"/>, so the admin
/// discard (D-89) meets pending choices as well as offers.
/// The reroll variants (C6a, D-93) add <see cref="Reroll"/> and give the admin reroll coupons (<c>freeRerolls</c>) to hand
/// out, with the paid reroll costing coins or a bad event and 0–2 free rerolls per roll picked by the seed.
/// The drop variants (C6b, D-94) add <see cref="DropRun"/>, <see cref="TechReroll"/> (by the player and by the admin) and
/// <see cref="ConvertTechRerollToDrop"/>, and move the clock forward between commands, so the tech reroll window
/// (<c>roll.techRerollWindowHours</c>) both holds and runs out.
/// The correction variants (C7b, D-97) add the admin's <see cref="CorrectRunHours"/> and <see cref="ChangeRunDifficulty"/>
/// of completed runs (and of runs that are not, and of made-up ones), so dice are appended, taken off the end and
/// recalculated, and the difficulty's good event is created and resolved.
/// </summary>
public class PlayerAdminInvariantTests
{
    private const int MapLength = 25;

    private static readonly string[] s_players = ["Вася", "Петя", "Маша"];
    private static readonly Guid s_late = SequentialIds.Make(0x10000000, 0x99);
    private static readonly Guid s_lateUser = SequentialIds.Make(0x40000000, 0x99);

    private const int ChoiceCount = 3;

    private const string Coupon = "freeRerolls";

    private const int RerollCoins = 5;

    /// <summary>How the variant plays rerolls: not at all, or with the paid reroll costing coins or a bad event.</summary>
    public enum RerollMode
    {
        None,
        Coins,
        BadEvent,
    }

    private static Ruleset WithRerolls(Ruleset r, int seed, RerollMode rerolls) =>
        rerolls == RerollMode.None
            ? r
            : r with
            {
                Roll = r.Roll with
                {
                    FreeRerollsPerRoll = ((seed % 3) + 3) % 3,
                    RerollCost = rerolls == RerollMode.Coins
                        ? new RerollCost { Kind = RerollCostKind.Coins, Amount = RerollCoins }
                        : new RerollCost { Kind = RerollCostKind.BadEvent },
                },
            };

    /// <remarks>
    /// The script claims challenges; <c>features.challenges</c> (D-96 (1)) is on for even seeds and off for odd ones, so
    /// both the claim and its refusal are exercised.
    /// </remarks>
    private static Scenario NewSeason(int seed, bool withChoice = false, RerollMode rerolls = RerollMode.None) =>
        Scenario.New(seed: seed)
            .WithRuleset(r => WithRerolls(withChoice ? r with { Roll = r.Roll with { ChoiceCount = ChoiceCount } } : r, seed, rerolls))
            .WithRuleset(r => r with { Features = r.Features with { Challenges = seed % 2 == 0 } })
            .WithMapLength(MapLength)
            .WithCategory("Horror", weight: 3)
            .WithGame("Silent Hill", 12, "Horror")
            .WithGame("Alan Wake", 15, "Horror")
            .WithCategory("Puzzle", weight: 2)
            .WithGame("Tetris", 2, "Puzzle")
            .WithGame("Unknown Length", null, "Puzzle")
            .WithCategory("Action", weight: 1)
            .WithGame("Doom", 4, "Action")
            .WithPlayers(s_players);

    /// <summary>
    /// Bits 0–1 pick the player (the fourth is the late one, maybe not added yet), bits 2–4 the kind of command,
    /// bits 5–7 its argument, so all three vary independently. With a choice, a start with an odd argument is
    /// MakeChoice instead: the player's pending choice (the argument picks the option, one past the last is unknown)
    /// or a made-up id. With rerolls, a points adjustment is a <see cref="Reroll"/> instead, and so is the inactive flag
    /// with a small argument; the resource the admin hands out is the reroll coupon.
    /// </summary>
    private static ICommand CommandFor(Scenario s, byte b, bool withChoice = false, bool withRerolls = false)
    {
        var index = b % 4;
        var player = index < s_players.Length ? s.PlayerId(s_players[index]) : s_late;
        var arg = b / 32;
        var comment = arg == 7 ? "" : "правка";
        if (withChoice && (b / 4) % 8 == 1 && arg % 2 == 1)
        {
            return ChoiceFor(s, player, arg / 2);
        }

        if (withRerolls && ((b / 4) % 8 == 3 || ((b / 4) % 8 == 6 && arg < 4)))
        {
            return new Reroll(player);
        }

        return ((b / 4) % 8) switch
        {
            0 => new RollGame(player),
            1 => new StartRun(player),
            // D-96: the upper arguments claim the challenge, 5 adds a review (its rating sometimes out of range), 6 reviews later
            2 when arg == 6 => ReviewFor(s, player, b),
            2 => new CompleteRun(
                player,
                (Difficulty)(arg % 4),
                EstimatedHours: 1 + arg,
                HoursSource: "HLTB",
                ChallengeDone: arg >= 4,
                Review: arg == 5 ? new RunReview(b % 12, (b % 3) switch { 0 => " ", 1 => "  отзыв ", _ => "отзыв" }) : null),
            3 => new AdjustPlayer(player, comment, PointsDelta: arg - 3),
            4 => new AdjustPlayer(player, comment, CoinsDelta: 3 - arg, ResourceDeltas: [new ResourceDelta(withRerolls ? Coupon : "tickets", (arg % 3) - 1)]),
            5 => new AdjustPlayer(player, comment, CellId: CellAt(s, arg * 4), DiscardOffer: arg % 2 == 1),
            6 => new SetPlayerInactive(player, arg % 2 == 1),
            _ => arg switch
            {
                7 => new ChangeSeasonStatus(s.State.Status + 1),
                6 => new AdjustPlayer(player, comment, DiscardOffer: true),
                5 => new SetSeasonDeadline(null),
                4 => new SetSeasonDeadline(FixedClock.SeasonStart.AddDays(10 + arg)),
                _ => new AddSeasonPlayer(s_late, s_lateUser, "Лёша", CellId: CellAt(s, arg * 5), Points: arg, Coins: 2 - arg),
            },
        };
    }

    /// <summary>
    /// A later review (D-96): of the player's latest run whatever its status, or of another player's run, or of a made-up
    /// one; the rating 0..11 is sometimes out of range, the text sometimes blank.
    /// </summary>
    private static ReviewRun ReviewFor(Scenario s, Guid player, byte b)
    {
        var runs = s.State.Runs.Values.ToList();
        var own = runs.LastOrDefault(r => r.PlayerId == player);
        var runId = (b % 3) switch
        {
            0 when own is not null => own.RunId,
            1 when runs.Count > 0 => runs[b % runs.Count].RunId,
            _ => own?.RunId ?? SequentialIds.Make(0x60000000, b),
        };
        return new ReviewRun(player, runId, new RunReview(b % 12, (b % 5) switch { 0 => "  ", 1 => " перепрошёл  ", _ => "перепрошёл" }));
    }

    private static MakeChoice ChoiceFor(Scenario s, Guid player, int arg)
    {
        if (!s.State.Players.TryGetValue(player, out var p) || p.Choice is not { } choice)
        {
            return new MakeChoice(player, SequentialIds.Make(0x50000000, arg), "none");
        }

        var index = arg % (choice.Options.Count + 1);
        return new MakeChoice(player, choice.ChoiceId, index < choice.Options.Count ? choice.Options[index].Id : "unknown");
    }

    private static string CellAt(Scenario s, int index) => s.State.Map.Cells[Math.Min(index, s.State.Map.Cells.Count - 1)].Id;

    /// <summary>
    /// With drops, a cell transfer with a small argument is a drop-family command instead (bits 5–7): 0–1 a drop,
    /// 2 a tech reroll «other» without a comment (refused), 3 a tech reroll «other» with one, 4 a listed reason, 5 an admin
    /// tech reroll, 6 the admin converting the player's latest tech-rerolled run (or any run of theirs, or a made-up one)
    /// into a drop; a completion with the top argument is a drop as well.
    /// </summary>
    private static ICommand DropCommandFor(Scenario s, byte b, bool withChoice, bool withRerolls)
    {
        var index = b % 4;
        var player = index < s_players.Length ? s.PlayerId(s_players[index]) : s_late;
        var arg = b / 32;
        var kind = (b / 4) % 8;
        if (kind == 2 && arg == 7)
        {
            return new DropRun(player);
        }

        if (kind != 5 || arg == 7)
        {
            return CommandFor(s, b, withChoice, withRerolls);
        }

        return arg switch
        {
            0 => new DropRun(player),
            1 => new TechReroll(player, (TechRerollReason)(b % 5), "попросил в чате", ByAdmin: true),
            2 => new TechReroll(player, TechRerollReason.Other, "  "),
            3 => new TechReroll(player, TechRerollReason.Other, "не тянет шейдеры"),
            4 => new TechReroll(player, (TechRerollReason)(b % 4), null),
            5 => new TechReroll(player, TechRerollReason.DoesNotLaunch, null, ByAdmin: true),
            _ => new ConvertTechRerollToDrop(RunToConvert(s, player, b), "это был дроп"),
        };
    }

    private static readonly decimal[] s_correctedHours = [0, 1, 3, 6, 7.5m, 12, 30, 100];

    /// <summary>
    /// With corrections, the inactive flag with an argument of 4 and up is a correction instead (bits 5–7): 4 and 6 correct
    /// the hours (one of <see cref="s_correctedHours"/>, 0 is refused; 6 sometimes with a blank comment), 5 and 7 change
    /// the difficulty (7 of a made-up run). The run is the player's latest completed one, or any run of the season, or a
    /// made-up one.
    /// </summary>
    private static ICommand CorrectionCommandFor(Scenario s, byte b, Func<ICommand> otherwise)
    {
        var arg = b / 32;
        if ((b / 4) % 8 != 6 || arg < 4)
        {
            return otherwise();
        }

        var runs = s.State.Runs.Values.ToList();
        var index = b % 4;
        var player = index < s_players.Length ? s.PlayerId(s_players[index]) : s_late;
        var runId = (b % 3) switch
        {
            _ when arg == 7 => SequentialIds.Make(0x60000000, b),
            1 when runs.Count > 0 => runs[b % runs.Count].RunId,
            _ => runs.LastOrDefault(r => r.PlayerId == player && r.Status == RunStatus.Completed)?.RunId
                ?? SequentialIds.Make(0x60000000, b),
        };
        return arg switch
        {
            4 => new CorrectRunHours(runId, s_correctedHours[b % 8], "часы по HLTB"),
            6 => new CorrectRunHours(runId, s_correctedHours[(b / 2) % 8], b % 2 == 0 ? " " : "часы по пруфу"),
            _ => new ChangeRunDifficulty(runId, (Difficulty)(b % 4), "сложность по пруфу"),
        };
    }

    private static Guid RunToConvert(Scenario s, Guid player, byte b)
    {
        var runs = s.State.Runs.Values.Where(r => r.PlayerId == player).ToList();
        var techRerolled = runs.LastOrDefault(r => r.Status == RunStatus.TechRerolled);
        return techRerolled?.RunId ?? runs.LastOrDefault()?.RunId ?? SequentialIds.Make(0x60000000, b);
    }

    private static Scenario Play(
        int seed,
        byte[] script,
        Action<Scenario, ICommand, SeasonState, int>? afterEach = null,
        bool withChoice = false,
        RerollMode rerolls = RerollMode.None,
        bool withDrops = false,
        bool withCorrections = false)
    {
        var s = NewSeason(seed, withChoice, rerolls);
        foreach (var b in script)
        {
            // With drops the clock runs: 0–21 hours before each command, so the 48-hour window both holds and closes
            if (withDrops)
            {
                s.Advance(TimeSpan.FromHours(3 * ((b / 8) % 8)));
            }

            var before = s.State;
            var logLength = s.Log.Count;
            ICommand Other() => withDrops
                ? DropCommandFor(s, b, withChoice, rerolls != RerollMode.None)
                : CommandFor(s, b, withChoice, rerolls != RerollMode.None);
            var command = withCorrections ? CorrectionCommandFor(s, b, Other) : Other();
            s.Act(command);
            afterEach?.Invoke(s, command, before, logLength);
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
    public void Invariants_hold_with_rerolls_paid_in_coins(int seed, byte[] script) =>
        Play(seed, script, CheckInvariants, rerolls: RerollMode.Coins);

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_rerolls_paid_by_a_bad_event(int seed, byte[] script) =>
        Play(seed, script, CheckInvariants, rerolls: RerollMode.BadEvent);

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_rerolls_and_a_choice_of_games(int seed, byte[] script) =>
        Play(seed, script, CheckInvariants, withChoice: true, rerolls: RerollMode.Coins);

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_drops_and_tech_rerolls(int seed, byte[] script) =>
        Play(seed, script, CheckInvariants, withDrops: true);

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_drops_rerolls_and_a_choice_of_games(int seed, byte[] script) =>
        Play(seed, script, CheckInvariants, withChoice: true, rerolls: RerollMode.BadEvent, withDrops: true);

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_run_corrections(int seed, byte[] script) =>
        Play(seed, script, CheckInvariants, withCorrections: true);

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_run_corrections_drops_and_rerolls(int seed, byte[] script) =>
        Play(seed, script, CheckInvariants, withChoice: true, rerolls: RerollMode.BadEvent, withDrops: true, withCorrections: true);

    [Property(MaxTest = 50)]
    public void Same_seed_and_commands_give_the_same_log_with_run_corrections(int seed, byte[] script)
    {
        // Invariant 14: the dice added by an hours correction come from the seeded random source only
        var first = Play(seed, script, withDrops: true, withCorrections: true);
        var second = Play(seed, script, withDrops: true, withCorrections: true);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Property(MaxTest = 50)]
    public void Same_seed_and_commands_give_the_same_log_with_drops(int seed, byte[] script)
    {
        // Invariant 14: penalty dice and the new roll after a tech reroll come from the seeded random source only
        var first = Play(seed, script, withChoice: true, rerolls: RerollMode.Coins, withDrops: true);
        var second = Play(seed, script, withChoice: true, rerolls: RerollMode.Coins, withDrops: true);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Property(MaxTest = 50)]
    public void Same_seed_and_commands_give_the_same_log_with_rerolls(int seed, byte[] script)
    {
        // Invariant 14: the new roll of a reroll comes from the seeded random source only
        var first = Play(seed, script, withChoice: true, rerolls: RerollMode.BadEvent);
        var second = Play(seed, script, withChoice: true, rerolls: RerollMode.BadEvent);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Property(MaxTest = 50)]
    public void Same_seed_and_commands_give_the_same_log_with_a_choice_of_games(int seed, byte[] script)
    {
        var first = Play(seed, script, withChoice: true);
        var second = Play(seed, script, withChoice: true);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Property(MaxTest = 50)]
    public void Same_seed_and_commands_give_the_same_log(int seed, byte[] script)
    {
        // Invariant 14 with admin commands in the mix
        var first = Play(seed, script);
        var second = Play(seed, script);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Property(MaxTest = 100)]
    public void Every_move_fires_its_entered_cells_and_a_transfer_fires_none(int seed, byte[] script) =>
        Play(seed, script, (s, _, _, _) =>
        {
            // D-90: a MoveStep per entered cell, a Stop on the last one only if the move had steps
            foreach (var moved in s.LastEvents<PlayerMoved>())
            {
                var visits = Movement.Visits(moved);
                if (moved.Steps == 0)
                {
                    Assert.Empty(visits);
                    continue;
                }

                Assert.Equal(moved.Path, visits.Where(v => v.Kind == CellVisitKind.MoveStep).Select(v => v.CellId));
                Assert.Equal(new CellVisit(moved.To, CellVisitKind.Stop), Assert.Single(visits, v => v.Kind == CellVisitKind.Stop));
                Assert.Equal(new CellVisit(moved.To, CellVisitKind.Stop), visits[^1]);
            }
        });

    private static void CheckInvariants(Scenario s, ICommand command, SeasonState before, int logLengthBefore)
    {
        // A rejected command has no events and changes nothing
        if (!s.Last.IsAccepted)
        {
            ScenarioAssert.Rejected(s, before, logLengthBefore, s.Last.Rejection!.Code);
            CheckRejectedReroll(s, command, before);
            CheckRejectedTechReroll(s, command, before);
        }
        else
        {
            CheckAcceptedCommand(s, command, before);
        }

        // 1. Replaying the log gives the stored state
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));

        var expected = Fold(s.Log);
        Assert.Equal(expected.Status, s.State.Status);
        Assert.Equal(expected.Players.Keys.Order(), s.State.Players.Keys.Order());

        foreach (var player in s.State.Players.Values)
        {
            var reference = expected.Players[player.PlayerId];

            // 2. Points and coins equal the sum of their logged changes; other resources too, without zero entries
            Assert.Equal(reference.Points, player.Points);
            Assert.Equal(reference.Coins, player.Coins);
            Assert.Equal(ResourceBag.From(reference.Resources), player.Resources);
            Assert.DoesNotContain(player.Resources, r => r.Value == 0);

            // P1. The token moves only by PlayerMoved events, each starting where the previous one ended
            Assert.Equal(reference.CellId, player.CellId);

            // 7. The token is on an existing cell
            Assert.Contains(s.State.Map.Cells, c => c.Id == player.CellId);

            // M4. The path ends where the token stands; inside a segment every pair of cells is a map edge;
            // one segment per placement (joining the season) and per transfer (Steps = 0)
            Assert.Equal(player.CellId, player.Path.Current);
            Assert.All(player.Path.Segments, segment =>
            {
                Assert.NotEmpty(segment.Cells);
                Assert.All(segment.Cells, cell => Assert.Contains(s.State.Map.Cells, c => c.Id == cell));
                Assert.All(
                    segment.Cells.Zip(segment.Cells.Skip(1)),
                    step => Assert.Contains(s.State.Map.Edges, e => e.From == step.First && e.To == step.Second));
            });
            var transfers = s.Log.OfType<PlayerMoved>().Count(e => e.PlayerId == player.PlayerId && e.Steps == 0);
            Assert.Equal(1 + transfers, player.Path.Segments.Count);

            // SE5. The flag is what the admin last set
            Assert.Equal(reference.IsInactive, player.IsInactive);

            // G8 / D-94: exclusions are folded from GameExcluded, a conversion turns TechRerolled into Dropped
            Assert.Equal(
                reference.Exclusions.Select(x => new GameExclusion(x.Key, x.Value)).OrderBy(x => x.GameId),
                player.Exclusions);

            // D-94: run statuses agree with the exclusions: a dropped run's game is excluded as dropped, a tech-rerolled
            // one as tech-rerolled; the player's excluded game is never offered, an option or played by them
            foreach (var run in s.State.Runs.Values.Where(r => r.PlayerId == player.PlayerId))
            {
                switch (run.Status)
                {
                    case RunStatus.Dropped:
                        Assert.Contains(new GameExclusion(run.GameId, ExclusionReason.Dropped), player.Exclusions);
                        break;
                    case RunStatus.TechRerolled:
                        Assert.Contains(new GameExclusion(run.GameId, ExclusionReason.TechRerolled), player.Exclusions);
                        break;
                }
            }

            var excludedNow = player.Exclusions.Select(x => x.GameId).ToHashSet();
            Assert.False(player.Offer is { } offered && excludedNow.Contains(offered.GameId), "An excluded game is offered.");
            Assert.DoesNotContain(player.Choice?.Options.Select(o => o.Game!.GameId) ?? [], excludedNow.Contains);
            Assert.False(player.ActiveRunId is { } active && excludedNow.Contains(s.State.Runs[active].GameId), "An excluded game is played.");

            // 3. No more active runs than allowed; the phase matches the offer or pending choice and the active run
            var playing = s.State.Runs.Values.Where(r => r.PlayerId == player.PlayerId && r.Status == RunStatus.Playing).ToList();
            Assert.True(playing.Count <= s.Ruleset.Season.MaxActiveRunsPerPlayer);
            Assert.Equal(playing.SingleOrDefault()?.RunId, player.ActiveRunId);
            Assert.Equal(player.Phase == TurnPhase.Playing, player.ActiveRunId is not null);
            Assert.Equal(player.Phase == TurnPhase.Rolling, player.Offer is not null || player.Choice is not null);
            Assert.False(player.Offer is not null && player.Choice is not null, "Both an offer and a pending choice.");

            // RR1 / D-93: the reroll counter is 0 whenever the player is not Rolling, and never negative
            Assert.True(player.RerollsThisRoll >= 0, $"Negative reroll counter {player.RerollsThisRoll}.");
            Assert.True(
                player.Phase == TurnPhase.Rolling || player.RerollsThisRoll == 0,
                $"{player.Name} is {player.Phase} with {player.RerollsThisRoll} rerolls.");
        }

        // RR1 / D-93 / D-97: pending manual effects are exactly the created ones minus the resolved ones
        var resolved = s.Log.OfType<ManualEffectResolved>().Select(e => e.EffectId).ToHashSet();
        Assert.Equal(
            s.Log.OfType<ManualEffectCreated>()
                .Where(e => !resolved.Contains(e.EffectId))
                .Select(e => new PendingManualEffect(e.EffectId, e.PlayerId, e.DrawEvent, e.Source, e.RunId))
                .OrderBy(e => e.EffectId),
            s.State.ManualEffects.Values);

        // W7 / W8 / D-13 / D-14 / D-97: a completed run has as many dice as the snapshot's rule gives for its current
        // hours, all of the die of its current difficulty (the challenge dice too), each within its sides
        foreach (var run in s.State.Runs.Values.Where(r => r.Status == RunStatus.Completed))
        {
            Assert.NotNull(run.Hours);
            Assert.NotNull(run.Difficulty);
            Assert.Equal(ExpectedDiceCount(run.Hours.Value, run.Snapshot.DiceCount), run.Dice.Count);
            var sides = DieOf(run.Difficulty.Value, run.Snapshot.DieByDifficulty).Sides;
            Assert.All(run.Dice.Concat(run.ChallengeDice), d =>
            {
                Assert.Equal(sides, d.Sides);
                Assert.InRange(d.Value, 1, d.Sides);
            });
        }
        Assert.All(s.State.ManualEffects, e => Assert.Equal(e.Key, e.Value.EffectId));

        // 4 / G9. A game is busy for at most one player: offered, among pending options (D-06) or played
        // (a discarded offer or choice frees it)
        var busy = s.State.Players.Values.Where(p => p.Offer is not null).Select(p => p.Offer!.GameId)
            .Concat(s.State.Players.Values.Where(p => p.Choice is not null).SelectMany(p => p.Choice!.Options.Select(o => o.Game!.GameId)))
            .Concat(s.State.Runs.Values.Where(r => r.Status == RunStatus.Playing).Select(r => r.GameId))
            .ToList();
        Assert.Equal(busy.Count, busy.Distinct().Count());
    }

    /// <summary>Per-command rules for accepted administration commands.</summary>
    private static void CheckAcceptedCommand(Scenario s, ICommand command, SeasonState before)
    {
        var events = s.Last.Events;
        switch (command)
        {
            case AdjustPlayer adjust:
                // D-21: exactly one comment, at least one real change, every change is about this player
                var adjusted = Assert.Single(events.OfType<PlayerAdjusted>());
                Assert.Equal(adjust.PlayerId, adjusted.PlayerId);
                Assert.False(string.IsNullOrWhiteSpace(adjusted.Comment));
                Assert.True(events.Count >= 2, "An adjustment must change something.");
                Assert.All(events, e => Assert.Equal(adjust.PlayerId, PlayerOf(e)));
                Assert.All(events.OfType<PointsChanged>(), e => Assert.Equal(PointsReason.AdminAdjustment, e.Reason));
                Assert.All(events.OfType<CoinsChanged>(), e => Assert.Equal(CoinsReason.AdminAdjustment, e.Reason));
                Assert.All(events.OfType<PlayerMoved>(), e => Assert.Equal(MoveReason.AdminAdjustment, e.Reason));
                // D-89, D-91: a discard while Rolling drops exactly what was pending: the offer or the choice
                var was = before.Players[adjust.PlayerId];
                var discards = events.Where(e => e is OfferDiscarded or ChoiceDiscarded).ToList();
                if (adjust.DiscardOffer && was.Phase == TurnPhase.Rolling)
                {
                    var discarded = Assert.Single(discards);
                    IGameEvent expectedDiscard = was.Choice is { } pending
                        ? new ChoiceDiscarded(adjust.PlayerId, pending.ChoiceId)
                        : new OfferDiscarded(adjust.PlayerId, was.Offer!.GameId);
                    Assert.Equal(expectedDiscard, discarded);
                    Assert.Null(s.State.Players[adjust.PlayerId].Choice);
                    Assert.Null(s.State.Players[adjust.PlayerId].Offer);
                }
                else
                {
                    Assert.Empty(discards);
                }

                if (adjust.DiscardOffer)
                {
                    // Accepted with the flag: the player was idle or rolling, never playing, and is idle now
                    Assert.NotEqual(TurnPhase.Playing, before.Players[adjust.PlayerId].Phase);
                    Assert.Equal(TurnPhase.Idle, s.State.Players[adjust.PlayerId].Phase);
                }

                break;
            case AddSeasonPlayer add:
                Assert.Equal(new SeasonPlayerAdded(add.PlayerId, add.UserId, add.Name, s.State.Map.Start.Id), events[0]);
                Assert.All(events, e => Assert.Equal(add.PlayerId, PlayerOf(e)));
                Assert.All(events.OfType<PointsChanged>(), e => Assert.Equal(PointsReason.StartingBalance, e.Reason));
                Assert.All(events.OfType<CoinsChanged>(), e => Assert.Equal(CoinsReason.StartingBalance, e.Reason));
                Assert.All(events.OfType<PlayerMoved>(), e => Assert.Equal(MoveReason.StartingCell, e.Reason));
                break;
            case ChangeSeasonStatus change:
                Assert.Equal([new SeasonStatusChanged(before.Status, change.To)], events);
                Assert.Equal(before.Status + 1, change.To);
                break;
            case SetPlayerInactive inactive:
                Assert.Equal([new PlayerInactivitySet(inactive.PlayerId, inactive.IsInactive)], events);
                break;
            case SetSeasonDeadline deadline:
                Assert.Equal([new SeasonDeadlineSet(deadline.Deadline)], events);
                break;
            case RollGame or StartRun:
                // Game actions only in a running season
                Assert.Equal(SeasonStatus.Active, before.Status);
                break;
            case CompleteRun complete:
                CheckAcceptedCompletion(s, complete, before);
                break;
            case ReviewRun review:
                CheckAcceptedReview(s, review, before);
                break;
            case Reroll reroll:
                CheckAcceptedReroll(s, reroll, before);
                break;
            case DropRun drop:
                CheckAcceptedDrop(s, drop, before);
                break;
            case TechReroll techReroll:
                CheckAcceptedTechReroll(s, techReroll, before);
                break;
            case ConvertTechRerollToDrop convert:
                CheckAcceptedConversion(s, convert, before);
                break;
            case CorrectRunHours correct:
                CheckAcceptedHoursCorrection(s, correct, before);
                break;
            case ChangeRunDifficulty change:
                CheckAcceptedDifficultyChange(s, change, before);
                break;
            case MakeChoice choose:
                // Choosing --> Playing (D-91): the chosen option starts at once with its roll-time snapshot
                Assert.Equal(SeasonStatus.Active, before.Status);
                var option = before.Players[choose.PlayerId].Choice!.Options.Single(o => o.Id == choose.OptionId).Game!;
                Assert.Equal(2, events.Count);
                Assert.Equal(new ChoiceMade(choose.PlayerId, choose.ChoiceId, choose.OptionId), events[0]);
                var started = Assert.IsType<RunStarted>(events[1]);
                Assert.Equal(
                    new RunStarted(started.RunId, choose.PlayerId, option.GameId, option.Snapshot, option.RolledAt, s.Clock.UtcNow),
                    started);
                Assert.Equal(TurnPhase.Playing, s.State.Players[choose.PlayerId].Phase);
                Assert.Null(s.State.Players[choose.PlayerId].Choice);
                break;
        }
    }

    /// <summary>
    /// The payment a reroll must take by D-07 / D-93: a free reroll of this roll, then a coupon, then the cost —
    /// null when the cost is coins and the player cannot pay.
    /// </summary>
    private static RerollPayment? ExpectedPayment(SeasonPlayer was, Ruleset rules)
    {
        if (was.RerollsThisRoll < rules.Roll.FreeRerollsPerRoll)
        {
            return RerollPayment.FreeThisRoll;
        }

        if (was.Resources[Coupon] >= 1)
        {
            return RerollPayment.FreeRerollResource;
        }

        if (rules.Roll.RerollCost.Kind == RerollCostKind.BadEvent)
        {
            return RerollPayment.BadEvent;
        }

        return was.Coins >= rules.Roll.RerollCost.Amount ? RerollPayment.Coins : null;
    }

    private static List<Guid> Pending(SeasonPlayer p) =>
        p.Offer is { } offer ? [offer.GameId] : p.Choice?.Options.Select(o => o.Game!.GameId).ToList() ?? [];

    /// <summary>RR1 / D-93: an accepted reroll gives up exactly what was pending, pays in order, and rolls anew without it.</summary>
    private static void CheckAcceptedReroll(Scenario s, Reroll reroll, SeasonState before)
    {
        var events = s.Last.Events;
        var was = before.Players[reroll.PlayerId];
        var now = s.State.Players[reroll.PlayerId];
        Assert.Equal(SeasonStatus.Active, before.Status);
        Assert.Equal(TurnPhase.Rolling, was.Phase);

        var givenUp = Pending(was);
        var rerolled = Assert.IsType<GameRerolled>(events[0]);
        Assert.Equal(reroll.PlayerId, rerolled.PlayerId);
        Assert.Equal(givenUp.Order(), rerolled.GameIds.Order());

        // Payment strictly in order; coins and coupons never go negative for a reroll
        var payment = ExpectedPayment(was, before.Rules);
        Assert.NotNull(payment);
        Assert.Equal(payment, rerolled.Payment);
        var paymentEvents = events.Skip(1).Take(events.Count - 2).ToList();
        switch (rerolled.Payment)
        {
            case RerollPayment.FreeThisRoll:
                Assert.Empty(paymentEvents);
                break;
            case RerollPayment.FreeRerollResource:
                Assert.Equal([new ResourceChanged(reroll.PlayerId, Coupon, -1, ResourceReason.Reroll)], paymentEvents);
                break;
            case RerollPayment.Coins:
                Assert.Equal([new CoinsChanged(reroll.PlayerId, -RerollCoins, CoinsReason.Reroll, null)], paymentEvents);
                break;
            case RerollPayment.BadEvent:
                var created = Assert.IsType<ManualEffectCreated>(Assert.Single(paymentEvents));
                Assert.Equal(new ManualEffectCreated(created.EffectId, reroll.PlayerId, EventKind.Bad, ManualEffectSource.PaidReroll, null), created);
                Assert.DoesNotContain(created.EffectId, before.ManualEffects.Keys);
                Assert.True(s.State.ManualEffects.ContainsKey(created.EffectId), "The bad event is not pending.");
                break;
        }

        Assert.Equal(rerolled.Payment == RerollPayment.Coins ? was.Coins - RerollCoins : was.Coins, now.Coins);
        Assert.Equal(
            rerolled.Payment == RerollPayment.FreeRerollResource ? was.Resources[Coupon] - 1 : was.Resources[Coupon],
            now.Resources[Coupon]);
        Assert.True(rerolled.Payment != RerollPayment.Coins || now.Coins >= 0, $"A reroll drove coins from {was.Coins} to {now.Coins}.");
        Assert.True(now.Resources[Coupon] >= 0 || was.Resources[Coupon] < 0, "A reroll drove the coupons negative.");

        // The new roll: the same player, never a game just given up (not even a miss), still Rolling, one more reroll
        List<Guid> newGames = events[^1] switch
        {
            GameRolled r when r.PlayerId == reroll.PlayerId => [r.GameId, .. r.Misses.Select(m => m.GameId)],
            GameChoiceRolled c when c.PlayerId == reroll.PlayerId => [.. c.Offers.Select(o => o.GameId), .. c.Misses.Select(m => m.GameId)],
            var other => throw new Xunit.Sdk.XunitException($"A reroll must end with the new roll, got {other}."),
        };
        Assert.DoesNotContain(newGames, givenUp.Contains);
        Assert.Equal(TurnPhase.Rolling, now.Phase);
        Assert.Equal(was.RerollsThisRoll + 1, now.RerollsThisRoll);
        Assert.Equal((was.Points, was.CellId), (now.Points, now.CellId));
    }

    private static readonly Type[] s_completionOrder =
    [
        typeof(RunCompleted), typeof(CompletionRolled), typeof(PointsChanged), typeof(PlayerMoved),
        typeof(CoinsChanged), typeof(ManualEffectCreated), typeof(RunReviewed),
    ];

    /// <summary>
    /// W2, W3, W6, W9, W10 / D-96: a completion logs its parts in the decided order; the challenge dice come from the
    /// snapshot, points grow by all dice, coins by the formula from the snapshot, a granted event and the review follow.
    /// </summary>
    private static void CheckAcceptedCompletion(Scenario s, CompleteRun complete, SeasonState before)
    {
        var events = s.Last.Events;
        Assert.Equal(SeasonStatus.Active, before.Status);
        var was = before.Players[complete.PlayerId];
        var run = before.Runs[was.ActiveRunId!.Value];

        // Order: each kind at most once, in the order of D-96
        var positions = events.Select(e => Array.IndexOf(s_completionOrder, e.GetType())).ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Distinct().Order(), positions);
        var completed = Assert.IsType<RunCompleted>(events[0]);
        var rolled = Assert.IsType<CompletionRolled>(events[1]);

        // W6: the estimate and its source count only without hours in the snapshot
        var fromPool = run.Snapshot.Hours is > 0;
        Assert.Equal(fromPool ? run.Snapshot.Hours : complete.EstimatedHours, completed.Hours);
        Assert.Equal(fromPool ? null : complete.HoursSource, completed.HoursSource);

        // W3 / D-96 (1): a claim is accepted only with features.challenges on
        Assert.False(complete.ChallengeDone && !before.Rules.Features.Challenges, "A challenge was claimed while features.challenges is off.");

        // W3: as many challenge dice as the snapshot says, of the difficulty's type
        Assert.Equal(complete.ChallengeDone, completed.ChallengeDone);
        Assert.Equal(complete.ChallengeDone ? run.Snapshot.ChallengeExtraDice : 0, rolled.ChallengeDice.Count);
        var sides = Assert.Single(rolled.Dice.Select(d => d.Sides).Distinct());
        Assert.All(rolled.ChallengeDice, d => Assert.Equal(sides, d.Sides));
        var sum = rolled.Dice.Sum(d => d.Value) + rolled.ChallengeDice.Sum(d => d.Value);
        Assert.Equal(sum, events.OfType<PointsChanged>().Sum(e => e.Delta));
        Assert.Equal(was.Points + sum, s.State.Players[complete.PlayerId].Points);

        // W10 / Q-2 / D-96 (2): coins by the snapshot's rule and the counted hours, capped by the dice ceiling
        Assert.NotNull(run.Snapshot.Coins);
        var counted = Math.Min(completed.Hours, run.Snapshot.DiceCount.Max * run.Snapshot.DiceCount.HoursPerDie);
        var coins = Math.Max(run.Snapshot.Coins.Min, (int)Math.Floor(counted * run.Snapshot.Coins.PerHour));
        Assert.Equal(
            coins == 0 ? [] : [new CoinsChanged(complete.PlayerId, coins, CoinsReason.CompletionReward, run.RunId)],
            events.OfType<CoinsChanged>());
        Assert.Equal(was.Coins + coins, s.State.Players[complete.PlayerId].Coins);

        // W2: the difficulty's event from the snapshot, a new manual effect of this run
        var rule = complete.Difficulty switch
        {
            Difficulty.Easy => run.Snapshot.DieByDifficulty.Easy,
            Difficulty.Normal => run.Snapshot.DieByDifficulty.Normal,
            Difficulty.Hard => run.Snapshot.DieByDifficulty.Hard,
            _ => run.Snapshot.DieByDifficulty.Extreme,
        };
        var effects = events.OfType<ManualEffectCreated>().ToList();
        if (rule.GrantEvent is { } kind)
        {
            var created = Assert.Single(effects);
            Assert.Equal(new ManualEffectCreated(created.EffectId, complete.PlayerId, kind, ManualEffectSource.Difficulty, run.RunId), created);
            Assert.DoesNotContain(created.EffectId, before.ManualEffects.Keys);
        }
        else
        {
            Assert.Empty(effects);
        }

        // W9 / D-96 (4): the review, if any, is valid and stored trimmed, with blank text as none
        var reviews = events.OfType<RunReviewed>().ToList();
        if (complete.Review is { } review)
        {
            Assert.InRange(review.Rating, 1, 10);
            var text = string.IsNullOrWhiteSpace(review.Text) ? null : review.Text.Trim();
            Assert.Equal([new RunReviewed(run.RunId, complete.PlayerId, review.Rating, text, s.Clock.UtcNow)], reviews);
            Assert.Equal(new RunReview(review.Rating, text), s.State.Runs[run.RunId].Review);
        }
        else
        {
            Assert.Empty(reviews);
        }
    }

    /// <summary>W9 / D-96: a later review is only of one's own completed run, until the archive, and changes only the review.</summary>
    private static void CheckAcceptedReview(Scenario s, ReviewRun review, SeasonState before)
    {
        Assert.NotEqual(SeasonStatus.Archived, before.Status);
        var run = before.Runs[review.RunId];
        Assert.Equal(review.PlayerId, run.PlayerId);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.InRange(review.Review.Rating, 1, 10);
        var text = string.IsNullOrWhiteSpace(review.Review.Text) ? null : review.Review.Text.Trim();
        Assert.Equal([new RunReviewed(run.RunId, review.PlayerId, review.Review.Rating, text, s.Clock.UtcNow)], s.Last.Events);
        Assert.Equal(before.Players, s.State.Players);
        Assert.Equal(run with { Review = new RunReview(review.Review.Rating, text) }, s.State.Runs[run.RunId]);
    }

    /// <summary>D-13: hours / hoursPerDie, rounded by the rule (nearest: half away from zero), then clamped to min..max.</summary>
    private static int ExpectedDiceCount(decimal hours, DiceCountRule rule)
    {
        var raw = hours / rule.HoursPerDie;
        var rounded = rule.Rounding switch
        {
            Rounding.Floor => Math.Floor(raw),
            Rounding.Ceil => Math.Ceiling(raw),
            _ => Math.Round(raw, MidpointRounding.AwayFromZero),
        };
        return (int)Math.Clamp(rounded, rule.Min, rule.Max);
    }

    private static DieRule DieOf(Difficulty difficulty, DieByDifficulty dice) =>
        difficulty switch
        {
            Difficulty.Easy => dice.Easy,
            Difficulty.Normal => dice.Normal,
            Difficulty.Hard => dice.Hard,
            _ => dice.Extreme,
        };

    /// <summary>W10 / D-96 (2): coins of a completion by the snapshot's rule, capped by the dice ceiling.</summary>
    private static int ExpectedCoins(decimal hours, RunSnapshot snapshot)
    {
        var counted = Math.Min(hours, snapshot.DiceCount.Max * snapshot.DiceCount.HoursPerDie);
        return Math.Max(snapshot.Coins!.Min, (int)Math.Floor(counted * snapshot.Coins.PerHour));
    }

    /// <summary>
    /// D-97: the difference of a correction: points, then a move from the current cell (forward along the arrows, back
    /// along the walked path; none when no cell is entered), each only when not zero, all linked to the run. The owner's
    /// turn is untouched and nobody else changes.
    /// </summary>
    private static void CheckCorrectionDifference(Scenario s, RunState run, int diff, SeasonState before)
    {
        var events = s.Last.Events;
        var was = before.Players[run.PlayerId];
        Assert.Equal(
            diff == 0 ? [] : [new PointsChanged(run.PlayerId, diff, PointsReason.RunCorrection, run.RunId)],
            events.OfType<PointsChanged>());
        Assert.Equal(was.Points + diff, s.State.Players[run.PlayerId].Points);

        var path = diff switch
        {
            > 0 => Movement.Forward(before.Map, was.CellId, diff),
            < 0 => Movement.Backward(before.Map, was.Path, -diff),
            _ => [],
        };
        var moves = events.OfType<PlayerMoved>().ToList();
        if (path.Count == 0)
        {
            Assert.Empty(moves);
        }
        else
        {
            Assert.Equal(
                [new PlayerMoved(run.PlayerId, was.CellId, path[^1], diff, [.. path], MoveReason.RunCorrection, run.RunId)],
                moves);
        }

        var now = s.State.Players[run.PlayerId];
        Assert.Equal(
            (was.Phase, was.Offer, was.Choice, was.ActiveRunId, was.RerollsThisRoll, was.Resources, was.Exclusions),
            (now.Phase, now.Offer, now.Choice, now.ActiveRunId, now.RerollsThisRoll, now.Resources, now.Exclusions));
        Assert.All(s.State.Players.Values.Where(p => p.PlayerId != run.PlayerId), p => Assert.Equal(before.Players[p.PlayerId], p));
    }

    /// <summary>W7 / D-14 / D-97: only a completed run, until the season is finished; dice by count, appended or taken off the end.</summary>
    private static void CheckAcceptedHoursCorrection(Scenario s, CorrectRunHours correct, SeasonState before)
    {
        var events = s.Last.Events;
        Assert.True(before.Status is SeasonStatus.Active or SeasonStatus.Closing, $"Corrected while {before.Status}.");
        Assert.True(correct.Hours > 0, "Hours not above zero were accepted.");
        Assert.False(string.IsNullOrWhiteSpace(correct.Comment), "A correction without a comment.");
        var run = before.Runs[correct.RunId];
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.NotEqual(run.Hours, correct.Hours);

        var oldCount = run.Dice.Count;
        var newCount = ExpectedDiceCount(correct.Hours, run.Snapshot.DiceCount);
        var corrected = Assert.IsType<RunHoursCorrected>(events[0]);
        Assert.Equal(
            (run.RunId, run.PlayerId, run.Hours!.Value, correct.Hours, Math.Max(0, oldCount - newCount), correct.Comment, s.Clock.UtcNow),
            (corrected.RunId, corrected.PlayerId, corrected.OldHours, corrected.NewHours, corrected.Removed, corrected.Comment, corrected.CorrectedAt));
        Assert.Equal(Math.Max(0, newCount - oldCount), corrected.Added.Count);
        var sides = DieOf(run.Difficulty!.Value, run.Snapshot.DieByDifficulty).Sides;
        Assert.All(corrected.Added, d => Assert.Equal(sides, d.Sides));

        // The dice by hours: the old ones kept from the start, the new ones appended; the challenge dice untouched
        EquatableArray<Die> dice = [.. run.Dice.Take(Math.Min(oldCount, newCount)), .. corrected.Added];
        var after = s.State.Runs[run.RunId];
        Assert.Equal(dice, after.Dice);
        Assert.Equal(run.ChallengeDice, after.ChallengeDice);
        Assert.Equal(correct.Hours, after.Hours);
        Assert.Equal(run.Difficulty, after.Difficulty);

        // Order: the correction, points, move, coins; nothing else
        var order = new[] { typeof(RunHoursCorrected), typeof(PointsChanged), typeof(PlayerMoved), typeof(CoinsChanged) };
        var positions = events.Select(e => Array.IndexOf(order, e.GetType())).ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Distinct().Order(), positions);

        CheckCorrectionDifference(s, run, dice.Sum(d => d.Value) - run.Dice.Sum(d => d.Value), before);

        // Coins by the snapshot's formula for the new hours
        var coins = ExpectedCoins(correct.Hours, run.Snapshot) - ExpectedCoins(run.Hours.Value, run.Snapshot);
        Assert.Equal(
            coins == 0 ? [] : [new CoinsChanged(run.PlayerId, coins, CoinsReason.RunCorrection, run.RunId)],
            events.OfType<CoinsChanged>());
        Assert.Equal(before.Players[run.PlayerId].Coins + coins, s.State.Players[run.PlayerId].Coins);
    }

    /// <summary>W8 / Q-5 / D-97: every die ⌈old × new sides / old sides⌉, both kept; the difficulty's event follows.</summary>
    private static void CheckAcceptedDifficultyChange(Scenario s, ChangeRunDifficulty change, SeasonState before)
    {
        var events = s.Last.Events;
        Assert.True(before.Status is SeasonStatus.Active or SeasonStatus.Closing, $"Changed while {before.Status}.");
        Assert.False(string.IsNullOrWhiteSpace(change.Comment), "A difficulty change without a comment.");
        var run = before.Runs[change.RunId];
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.NotEqual(run.Difficulty, change.Difficulty);
        var oldRule = DieOf(run.Difficulty!.Value, run.Snapshot.DieByDifficulty);
        var newRule = DieOf(change.Difficulty, run.Snapshot.DieByDifficulty);

        DieChange Recalculate(Die d) => new(d, new Die(newRule.Sides, ((d.Value * newRule.Sides) + d.Sides - 1) / d.Sides));
        EquatableArray<DieChange> dice = [.. run.Dice.Select(Recalculate)];
        EquatableArray<DieChange> challenge = [.. run.ChallengeDice.Select(Recalculate)];
        Assert.Equal(
            new RunDifficultyChanged(
                run.RunId, run.PlayerId, run.Difficulty.Value, change.Difficulty, dice, challenge, change.Comment, s.Clock.UtcNow),
            events[0]);

        var after = s.State.Runs[run.RunId];
        Assert.Equal(change.Difficulty, after.Difficulty);
        Assert.Equal(dice.Select(d => d.After), after.Dice);
        Assert.Equal(challenge.Select(d => d.After), after.ChallengeDice);
        Assert.Equal(run.Hours, after.Hours);

        Type[] allowed = [typeof(RunDifficultyChanged), typeof(PointsChanged), typeof(PlayerMoved), typeof(ManualEffectResolved), typeof(ManualEffectCreated)];
        Assert.All(events, e => Assert.Contains(e.GetType(), allowed));
        CheckCorrectionDifference(s, run, dice.Concat(challenge).Sum(d => d.After.Value - d.Before.Value), before);

        // The old difficulty's pending event is resolved «not applicable» with the comment; the new one's is created
        var pending = before.ManualEffects.Values.Where(e => e.RunId == run.RunId && e.Source == ManualEffectSource.Difficulty);
        Assert.Equal(
            oldRule.GrantEvent is null ? [] : pending.Select(e => new ManualEffectResolved(e.EffectId, ManualEffectOutcome.NotApplicable, change.Comment)),
            events.OfType<ManualEffectResolved>());
        var created = events.OfType<ManualEffectCreated>().ToList();
        if (newRule.GrantEvent is { } kind)
        {
            var effect = Assert.Single(created);
            Assert.Equal(new ManualEffectCreated(effect.EffectId, run.PlayerId, kind, ManualEffectSource.Difficulty, run.RunId), effect);
            Assert.DoesNotContain(effect.EffectId, before.ManualEffects.Keys);
        }
        else
        {
            Assert.Empty(created);
        }
    }

    /// <summary>RR1 / D-93: «not enough coins» only when the free rerolls and coupons are used up and the coins fall short.</summary>
    private static void CheckRejectedReroll(Scenario s, ICommand command, SeasonState before)
    {
        if (command is not Reroll reroll || s.Last.Rejection!.Code != RejectionCodes.NotEnoughCoins)
        {
            return;
        }

        var was = before.Players[reroll.PlayerId];
        Assert.Equal(TurnPhase.Rolling, was.Phase);
        Assert.Equal(RerollCostKind.Coins, before.Rules.Roll.RerollCost.Kind);
        Assert.Null(ExpectedPayment(was, before.Rules));
    }

    /// <summary>
    /// RR2 / RR3 / D-94: the penalty events: points and a move back by the dice sum (each part only when the rules say
    /// so), and the mandatory bad event; never coins. The move starts where the token stood and retraces the path.
    /// </summary>
    private static void CheckPenalty(
        Scenario s, IReadOnlyList<IGameEvent> penalty, Guid playerId, Guid runId, EquatableArray<Die> dice, SeasonState before)
    {
        var rules = before.Rules.Drop;
        Assert.Equal(rules.PenaltyDice.Count, dice.Count);
        Assert.All(dice, d =>
        {
            Assert.Equal(rules.PenaltyDice.Sides, d.Sides);
            Assert.InRange(d.Value, 1, d.Sides);
        });
        var sum = dice.Sum(d => d.Value);
        var was = before.Players[playerId];

        var points = penalty.OfType<PointsChanged>().ToList();
        Assert.Equal(
            rules.AffectsPoints && sum > 0 ? [new PointsChanged(playerId, -sum, PointsReason.DropPenalty, runId)] : [],
            points);

        var moves = penalty.OfType<PlayerMoved>().ToList();
        if (!rules.AffectsPosition || was.CellId == before.Map.Start.Id || sum == 0)
        {
            Assert.Empty(moves);
        }
        else
        {
            var moved = Assert.Single(moves);
            Assert.Equal(
                (playerId, was.CellId, -sum, MoveReason.DropPenalty, (Guid?)runId),
                (moved.PlayerId, moved.From, moved.Steps, moved.Reason, moved.RunId));
            Assert.Equal(Movement.Backward(before.Map, was.Path, sum), moved.Path);
            Assert.InRange(moved.Path.Count, 1, sum);
        }

        var effects = penalty.OfType<ManualEffectCreated>().ToList();
        if (rules.MandatoryEvent == MandatoryEvent.Bad)
        {
            var created = Assert.Single(effects);
            Assert.Equal(new ManualEffectCreated(created.EffectId, playerId, EventKind.Bad, ManualEffectSource.Drop, runId), created);
            Assert.DoesNotContain(created.EffectId, before.ManualEffects.Keys);
        }
        else
        {
            Assert.Empty(effects);
        }

        // No coins and no other resources from a drop
        Assert.Empty(penalty.OfType<CoinsChanged>());
        Assert.Empty(penalty.OfType<ResourceChanged>());
        Assert.Equal((was.Coins, was.Resources), (s.State.Players[playerId].Coins, s.State.Players[playerId].Resources));
    }

    /// <summary>RR2 / D-94: RunDropped of the active run, the penalty, GameExcluded(Dropped); the player is Idle.</summary>
    private static void CheckAcceptedDrop(Scenario s, DropRun drop, SeasonState before)
    {
        var events = s.Last.Events;
        var was = before.Players[drop.PlayerId];
        Assert.Equal(SeasonStatus.Active, before.Status);
        Assert.Equal(TurnPhase.Playing, was.Phase);
        var runId = was.ActiveRunId!.Value;
        var dropped = Assert.IsType<RunDropped>(events[0]);
        Assert.Equal((runId, drop.PlayerId, s.Clock.UtcNow), (dropped.RunId, dropped.PlayerId, dropped.DroppedAt));
        Assert.Single(events, e => e is GameExcluded);
        Assert.Contains(new GameExcluded(drop.PlayerId, before.Runs[runId].GameId, ExclusionReason.Dropped), events);
        Assert.All(events, e => Assert.True(
            e is RunDropped or GameExcluded or PointsChanged or PlayerMoved or ManualEffectCreated, $"Unexpected {e}."));
        CheckPenalty(s, [.. events.Where(e => e is not (RunDropped or GameExcluded))], drop.PlayerId, runId, dropped.PenaltyDice, before);

        var now = s.State.Players[drop.PlayerId];
        Assert.Equal(TurnPhase.Idle, now.Phase);
        Assert.Null(now.ActiveRunId);
        Assert.Equal(RunStatus.Dropped, s.State.Runs[runId].Status);
    }

    /// <summary>RR5 / D-94: within the window (or by the admin), free, the game excluded, then at most a new roll of the same player.</summary>
    private static void CheckAcceptedTechReroll(Scenario s, TechReroll techReroll, SeasonState before)
    {
        var events = s.Last.Events;
        var was = before.Players[techReroll.PlayerId];
        Assert.Equal(SeasonStatus.Active, before.Status);
        Assert.Equal(TurnPhase.Playing, was.Phase);
        var run = before.Runs[was.ActiveRunId!.Value];
        Assert.True(
            techReroll.ByAdmin || s.Clock.UtcNow - run.RolledAt <= TimeSpan.FromHours(run.Snapshot.TechRerollWindowHours),
            "A player tech-rerolled after the window fixed at the roll.");
        Assert.True(
            !techReroll.ByAdmin || !string.IsNullOrWhiteSpace(techReroll.Comment), "An admin tech reroll without a comment (D-94 (2)).");
        Assert.True(
            techReroll.Reason != TechRerollReason.Other || !string.IsNullOrWhiteSpace(techReroll.Comment), "«Other» without a comment.");
        Assert.Equal(
            new RunTechRerolled(run.RunId, techReroll.PlayerId, techReroll.Reason, techReroll.Comment, techReroll.ByAdmin, s.Clock.UtcNow),
            events[0]);
        Assert.Equal(new GameExcluded(techReroll.PlayerId, run.GameId, ExclusionReason.TechRerolled), events[1]);
        Assert.InRange(events.Count, 2, 3);

        var now = s.State.Players[techReroll.PlayerId];
        if (events.Count == 3)
        {
            Assert.True(
                (events[2] is GameRolled r && r.PlayerId == techReroll.PlayerId && r.GameId != run.GameId)
                || (events[2] is GameChoiceRolled c && c.PlayerId == techReroll.PlayerId && c.Offers.All(o => o.GameId != run.GameId)),
                $"Unexpected {events[2]}.");
            Assert.Equal(TurnPhase.Rolling, now.Phase);
        }
        else
        {
            Assert.Equal(TurnPhase.Idle, now.Phase);
        }

        // A new roll with its own free rerolls; free: points, position, coins and resources stay
        Assert.Equal(0, now.RerollsThisRoll);
        Assert.Equal((was.Points, was.CellId, was.Coins, was.Resources), (now.Points, now.CellId, now.Coins, now.Resources));
        Assert.Equal(RunStatus.TechRerolled, s.State.Runs[run.RunId].Status);
    }

    /// <summary>RR6 / D-94: only a tech-rerolled run, until the season is finished; the penalty by the current standing; the turn untouched.</summary>
    private static void CheckAcceptedConversion(Scenario s, ConvertTechRerollToDrop convert, SeasonState before)
    {
        var events = s.Last.Events;
        Assert.True(before.Status is SeasonStatus.Active or SeasonStatus.Closing, $"Converted while {before.Status}.");
        var run = before.Runs[convert.RunId];
        Assert.Equal(RunStatus.TechRerolled, run.Status);
        var converted = Assert.IsType<TechRerollConvertedToDrop>(events[0]);
        Assert.Equal(
            (convert.RunId, run.PlayerId, convert.Comment, s.Clock.UtcNow),
            (converted.RunId, converted.PlayerId, converted.Comment, converted.ConvertedAt));
        Assert.Empty(events.OfType<GameExcluded>());
        CheckPenalty(s, [.. events.Skip(1)], run.PlayerId, run.RunId, converted.PenaltyDice, before);
        Assert.Equal(RunStatus.Dropped, s.State.Runs[run.RunId].Status);

        var was = before.Players[run.PlayerId];
        var now = s.State.Players[run.PlayerId];
        Assert.Equal(
            (was.Phase, was.Offer, was.Choice, was.ActiveRunId, was.RerollsThisRoll),
            (now.Phase, now.Offer, now.Choice, now.ActiveRunId, now.RerollsThisRoll));
    }

    /// <summary>RR5: «window closed» only for the player and only after the window.</summary>
    private static void CheckRejectedTechReroll(Scenario s, ICommand command, SeasonState before)
    {
        if (command is not TechReroll techReroll || s.Last.Rejection!.Code != RejectionCodes.TechRerollWindowClosed)
        {
            return;
        }

        Assert.False(techReroll.ByAdmin, "The admin is not bound by the tech reroll window.");
        var was = before.Players[techReroll.PlayerId];
        Assert.Equal(TurnPhase.Playing, was.Phase);
        var run = before.Runs[was.ActiveRunId!.Value];
        Assert.True(s.Clock.UtcNow - run.RolledAt > TimeSpan.FromHours(run.Snapshot.TechRerollWindowHours));
    }

    private static Guid? PlayerOf(IGameEvent e) =>
        e switch
        {
            SeasonPlayerAdded x => x.PlayerId,
            PlayerAdjusted x => x.PlayerId,
            OfferDiscarded x => x.PlayerId,
            ChoiceDiscarded x => x.PlayerId,
            GameRolled x => x.PlayerId,
            GameChoiceRolled x => x.PlayerId,
            GameRerolled x => x.PlayerId,
            ManualEffectCreated x => x.PlayerId,
            ChoiceMade x => x.PlayerId,
            RunStarted x => x.PlayerId,
            PointsChanged x => x.PlayerId,
            CoinsChanged x => x.PlayerId,
            ResourceChanged x => x.PlayerId,
            PlayerMoved x => x.PlayerId,
            PlayerInactivitySet x => x.PlayerId,
            RunDropped x => x.PlayerId,
            RunTechRerolled x => x.PlayerId,
            TechRerollConvertedToDrop x => x.PlayerId,
            GameExcluded x => x.PlayerId,
            RunReviewed x => x.PlayerId,
            RunHoursCorrected x => x.PlayerId,
            RunDifficultyChanged x => x.PlayerId,
            _ => null,
        };

    private sealed class ReferencePlayer
    {
        public required string CellId { get; set; }

        public int Points { get; set; }

        public int Coins { get; set; }

        public Dictionary<string, int> Resources { get; } = new(StringComparer.Ordinal);

        public bool IsInactive { get; set; }

        public Dictionary<Guid, ExclusionReason> Exclusions { get; } = [];
    }

    private sealed record Reference(SeasonStatus Status, Dictionary<Guid, ReferencePlayer> Players);

    /// <summary>Folds the log the way the rules say, independently of the engine's Apply, checking each event on the way.</summary>
    private static Reference Fold(IEnumerable<IGameEvent> log)
    {
        var status = SeasonStatus.Draft;
        var players = new Dictionary<Guid, ReferencePlayer>();
        var runs = new Dictionary<Guid, (Guid Player, Guid Game)>();
        var pendingEffects = new HashSet<Guid>();
        foreach (var e in log)
        {
            switch (e)
            {
                case SeasonStatusChanged changed:
                    Assert.Equal(status, changed.From);
                    Assert.Equal(status + 1, changed.To);
                    status = changed.To;
                    break;
                case SeasonPlayerAdded added:
                    Assert.True(status is SeasonStatus.Draft or SeasonStatus.Active, $"Player added while {status}.");
                    Assert.DoesNotContain(added.PlayerId, players.Keys);
                    players[added.PlayerId] = new ReferencePlayer { CellId = added.CellId };
                    break;
                case GameRolled rolled:
                    // SE1/SE2: no game actions outside a running season; G8: never a game excluded for the player, not even as a miss
                    Assert.Equal(SeasonStatus.Active, status);
                    Assert.DoesNotContain(rolled.GameId, players[rolled.PlayerId].Exclusions.Keys);
                    Assert.DoesNotContain(rolled.Misses, m => players[rolled.PlayerId].Exclusions.ContainsKey(m.GameId));
                    break;
                case GameChoiceRolled choiceRolled:
                    Assert.Equal(SeasonStatus.Active, status);
                    Assert.DoesNotContain(choiceRolled.Offers, o => players[choiceRolled.PlayerId].Exclusions.ContainsKey(o.GameId));
                    Assert.DoesNotContain(choiceRolled.Misses, m => players[choiceRolled.PlayerId].Exclusions.ContainsKey(m.GameId));
                    break;
                case RunStarted started:
                    Assert.Equal(SeasonStatus.Active, status);
                    Assert.DoesNotContain(started.GameId, players[started.PlayerId].Exclusions.Keys);
                    runs[started.RunId] = (started.PlayerId, started.GameId);
                    break;
                case GameRerolled or ChoiceMade or RunCompleted or CompletionRolled or RunDropped or RunTechRerolled:
                    // SE1/SE2: no game actions outside a running season
                    Assert.Equal(SeasonStatus.Active, status);
                    break;
                case RunReviewed reviewed:
                    // W9 / D-96: reviews until the archive, only of one's own runs
                    Assert.NotEqual(SeasonStatus.Archived, status);
                    Assert.Equal(runs[reviewed.RunId].Player, reviewed.PlayerId);
                    break;
                case RunHoursCorrected or RunDifficultyChanged:
                    // D-97: corrections until the season is finished
                    Assert.True(status is SeasonStatus.Active or SeasonStatus.Closing, $"Corrected while {status}.");
                    break;
                case ManualEffectCreated created:
                    Assert.True(pendingEffects.Add(created.EffectId), "An effect created twice.");
                    break;
                case ManualEffectResolved resolvedEffect:
                    // D-97: only a pending effect is resolved, once, with a comment
                    Assert.True(pendingEffects.Remove(resolvedEffect.EffectId), "A resolved effect was not pending.");
                    Assert.False(string.IsNullOrWhiteSpace(resolvedEffect.Comment), "A resolution without a comment.");
                    break;
                case GameExcluded excluded:
                    Assert.Equal(SeasonStatus.Active, status);
                    Assert.True(players[excluded.PlayerId].Exclusions.TryAdd(excluded.GameId, excluded.Reason), "A game excluded twice.");
                    break;
                case TechRerollConvertedToDrop converted:
                    // D-11: until the season is finished; the exclusion's reason becomes a drop
                    Assert.True(status is SeasonStatus.Active or SeasonStatus.Closing, $"Converted while {status}.");
                    var (owner, game) = runs[converted.RunId];
                    Assert.Equal(owner, converted.PlayerId);
                    Assert.Equal(ExclusionReason.TechRerolled, players[owner].Exclusions[game]);
                    players[owner].Exclusions[game] = ExclusionReason.Dropped;
                    break;
                case PointsChanged points:
                    Assert.True(points.Reason == PointsReason.CompletionRoll || points.Delta != 0, "Zero changes are not logged.");
                    players[points.PlayerId].Points += points.Delta;
                    break;
                case CoinsChanged coins:
                    // RR2: no coins from drops (coins have no drop reason at all)
                    Assert.NotEqual(0, coins.Delta);
                    players[coins.PlayerId].Coins += coins.Delta;

                    // W10 / D-96: the completion reward is always a gain
                    Assert.True(coins.Reason != CoinsReason.CompletionReward || coins.Delta > 0, "A completion took coins.");

                    // RR1 / D-93: a reroll is a purchase: it never takes coins into the negative
                    Assert.True(coins.Reason != CoinsReason.Reroll || players[coins.PlayerId].Coins >= 0, "A reroll took coins below zero.");
                    break;
                case ResourceChanged resource:
                    Assert.NotEqual(0, resource.Delta);
                    var bag = players[resource.PlayerId].Resources;
                    bag[resource.Resource] = bag.GetValueOrDefault(resource.Resource) + resource.Delta;
                    Assert.True(
                        resource.Reason != ResourceReason.Reroll || (resource.Resource == Coupon && resource.Delta == -1 && bag[resource.Resource] >= 0),
                        "A reroll spent a coupon it did not have.");
                    break;
                case PlayerMoved moved:
                    var player = players[moved.PlayerId];
                    Assert.Equal(player.CellId, moved.From);
                    Assert.Equal(moved.To, moved.Path[^1]);
                    if (moved.Reason == MoveReason.DropPenalty)
                    {
                        // RR3: a drop only moves back, never past the start
                        Assert.True(moved.Steps < 0, "A drop penalty moved forward.");
                        Assert.InRange(moved.Path.Count, 1, -moved.Steps);
                    }

                    if (moved.Reason == MoveReason.RunCorrection)
                    {
                        // D-97: a correction moves by steps, never past the start, and never a no-op
                        Assert.NotEqual(0, moved.Steps);
                        Assert.InRange(moved.Path.Count, 1, Math.Abs(moved.Steps));
                    }

                    if (moved.Reason is MoveReason.AdminAdjustment or MoveReason.StartingCell)
                    {
                        // A transfer, not steps; and never a no-op
                        Assert.Equal([moved.To], moved.Path);
                        Assert.NotEqual(moved.From, moved.To);
                    }

                    player.CellId = moved.To;
                    break;
                case PlayerInactivitySet inactive:
                    Assert.NotEqual(players[inactive.PlayerId].IsInactive, inactive.IsInactive);
                    players[inactive.PlayerId].IsInactive = inactive.IsInactive;
                    break;
            }
        }

        return new Reference(status, players);
    }
}

using FsCheck.Xunit;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Ranking;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;
using GameEvent.Engine.Undo;

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
/// The proof variants (C8, D-98) add <see cref="SubmitProof"/> (valid and invalid links, witnesses, other players' runs),
/// <see cref="ApproveProof"/> (with and without a proof, at a lower, the same or a higher difficulty) and
/// <see cref="RejectProof"/>, and check the review queue (<see cref="ProofReviewOrder.Order"/>) after every command.
/// </summary>
public partial class PlayerAdminInvariantTests
{
    private const int MapLength = 25;

    private const int ShortMapLength = 6;

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
    /// <remarks>
    /// The finish variant (C9a, D-99) plays on a map of <see cref="ShortMapLength"/> steps, so finishes, bonuses, freezes and
    /// revokes happen often, with more games in the pool; <c>finish.requireApprovalForFirst</c> is off for every third seed.
    /// </remarks>
    private static EquatableArray<Tiebreaker> TiebreakersFor(int seed) =>
        (((seed % 5) + 5) % 5) switch
        {
            0 => [Tiebreaker.CompletedRuns, Tiebreaker.EarliestFinalScore],
            1 => [Tiebreaker.EarliestFinalScore, Tiebreaker.CompletedRuns],
            2 => [Tiebreaker.CompletedRuns],
            3 => [Tiebreaker.EarliestFinalScore],
            _ => [],
        };

    private static Scenario NewSeason(int seed, bool withChoice = false, RerollMode rerolls = RerollMode.None, bool finishes = false)
    {
        var s = Scenario.New(seed: seed)
            .WithRuleset(r => WithRerolls(withChoice ? r with { Roll = r.Roll with { ChoiceCount = ChoiceCount } } : r, seed, rerolls))
            .WithRuleset(r => r with { Features = r.Features with { Challenges = seed % 2 == 0 } })
            .WithMapLength(finishes ? ShortMapLength : MapLength)
            .WithCategory("Horror", weight: 3)
            .WithGame("Silent Hill", 12, "Horror")
            .WithGame("Alan Wake", 15, "Horror")
            .WithCategory("Puzzle", weight: 2)
            .WithGame("Tetris", 2, "Puzzle")
            .WithGame("Unknown Length", null, "Puzzle")
            .WithCategory("Action", weight: 1)
            .WithGame("Doom", 4, "Action");
        // Invariant 10: the tiebreakers vary by the seed — default, swapped, one of each alone, none
        s.WithRuleset(r => r with { Ranking = new RankingRules { Tiebreakers = TiebreakersFor(seed) } });
        if (finishes)
        {
            s.WithRuleset(r => r with { Finish = r.Finish with { RequireApprovalForFirst = ((seed % 3) + 3) % 3 != 1 } })
                .WithGame("Dead Space", 9, "Horror")
                .WithGame("Portal", 3, "Puzzle")
                .WithGame("Limbo", null, "Puzzle")
                .WithGame("Quake", 6, "Action")
                .WithGame("Prey", 18, "Action");
        }

        return s.WithPlayers(s_players);
    }

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
            // C11a (D-102): the upper arguments resolve a pending manual effect — by its owner, by another player or by the
            // admin (7), applied or not applicable, with or without a comment
            6 when arg >= 4 && s.State.ManualEffects.Count > 0 => ResolveFor(s, player, b),
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

    private static ResolveManualEffect ResolveFor(Scenario s, Guid player, byte b)
    {
        var arg = b / 32;
        var effects = s.State.ManualEffects.Values.ToList();
        var effect = effects[(b + s.Log.Count) % effects.Count];

        // Sometimes an effect already resolved (the log's last one) or a made-up one: the engine refuses it
        if (s.Log.Count % 5 == 4)
        {
            var old = s.Log.OfType<ManualEffectResolved>().LastOrDefault()?.EffectId ?? SequentialIds.Make(0x70000000, b);
            return new ResolveManualEffect(old, ManualEffectOutcome.Applied, "повтор", null);
        }

        var outcome = (b + s.Log.Count) % 2 == 0 ? ManualEffectOutcome.Applied : ManualEffectOutcome.NotApplicable;
        var comment = (s.Log.Count % 3) switch { 0 => null, 1 => " ", _ => "разыграли" };
        var by = arg == 7 ? (Guid?)null : arg == 6 ? player : effect.PlayerId;
        return new ResolveManualEffect(effect.EffectId, outcome, comment, by);
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
            4 or 6 => new CorrectRunHours(runId, s_correctedHours[HoursIndex(arg, b)], arg == 6 && b % 4 == 3 ? " " : "часы по пруфу"),
            _ => new ChangeRunDifficulty(runId, (Difficulty)(b % 4), "сложность по пруфу"),
        };
    }

    /// <summary>
    /// Bits 2–7 are fixed by the choice of a hours correction, so the index takes the argument (4 or 6) and the free bits
    /// 0–1: argument 4 gives 0, 1, 3, 6 hours, argument 6 gives 7.5, 12, 30, 100 — every value is reachable.
    /// </summary>
    private static int HoursIndex(int arg, byte b) => ((arg - 4) * 2) + (b % 4);

    private static readonly string[] s_proofLinks =
        ["https://imgur.com/a/credits", "http://example.com/ending.png", "javascript:alert(1)", "ftp://example.com/x.png", "  https://youtu.be/ending	"];

    /// <summary>
    /// With proofs, a coins adjustment with an argument of 3 and up is a proof command instead (bits 5–7): 3 a proof with
    /// links (sometimes a bad one, sometimes six, sometimes padded with spaces; the note sometimes padded or blank), 4 a proof by a
    /// witness (sometimes the player themself), 5 an approval
    /// (with or without a comment, at no, a lower, the same or a higher difficulty), 6 a reject (sometimes with a blank
    /// comment), 7 an approval of any run of the season. The run is the player's latest completed one, or any run of the
    /// season, or a made-up one. The variant inside a kind comes from the log length, since bits 2–7 are fixed.
    /// </summary>
    private static ICommand ProofCommandFor(Scenario s, byte b, Func<ICommand> otherwise)
    {
        var arg = b / 32;
        if ((b / 4) % 8 != 4 || arg < 3)
        {
            return otherwise();
        }

        var runs = s.State.Runs.Values.ToList();
        var index = b % 4;
        var player = index < s_players.Length ? s.PlayerId(s_players[index]) : s_late;
        var variant = s.Log.Count;
        var runId = (variant % 5) switch
        {
            _ when arg == 7 && runs.Count > 0 => runs[variant % runs.Count].RunId,
            1 when runs.Count > 0 => runs[b % runs.Count].RunId,
            4 => SequentialIds.Make(0x60000000, b),
            _ => runs.LastOrDefault(r => r.PlayerId == player && r.Status == RunStatus.Completed)?.RunId
                ?? runs.LastOrDefault(r => r.PlayerId == player)?.RunId
                ?? SequentialIds.Make(0x60000000, b),
        };
        var witness = s.PlayerId(s_players[(index + 1) % s_players.Length]);
        return arg switch
        {
            3 => new SubmitProof(
                player,
                runId,
                (variant % 7) switch
                {
                    0 => [s_proofLinks[variant % s_proofLinks.Length]],
                    1 => [.. Enumerable.Range(0, 6).Select(i => $"https://imgur.com/a/{i}")],
                    _ => [s_proofLinks[0], s_proofLinks[1]],
                },
                (variant % 3) switch { 0 => "титры", 1 => "  титры ", _ => null },
                Files: (variant % 5) switch
                {
                    // D-116: screenshots with the links, alone below, and the refused kinds (too many, the same twice)
                    0 => [SequentialIds.Make(0x51000000, b)],
                    1 => [.. Enumerable.Range(0, 6).Select(i => SequentialIds.Make(0x51000000, i))],
                    2 => [SequentialIds.Make(0x51000000, 1), SequentialIds.Make(0x51000000, 1)],
                    _ => [],
                }),
            4 when variant % 6 == 0 => new SubmitProof(player, runId, [], Files: [SequentialIds.Make(0x51000000, b)]),
            4 => new SubmitProof(player, runId, [], (variant % 4) switch { 0 => null, 1 => "   ", _ => "видел" }, variant % 5 == 0 ? player : witness),
            5 or 7 => new ApproveProof(
                runId,
                (variant % 5) switch { 0 => null, var d => (Difficulty)(d - 1) },
                variant % 2 == 0 ? "без скрина" : null),
            _ => new RejectProof(runId, variant % 5 == 0 ? " " : "на скрине другая игра"),
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
        bool withCorrections = false,
        bool withProofs = false,
        bool finishes = false,
        bool lifecycle = false,
        bool withUndo = false,
        bool withRulesetChanges = false)
    {
        // C13: a failure prints the (shrunk) game as builder code
        return NewSeason(seed, withChoice, rerolls, finishes).Explained(s =>
        {
            foreach (var b in script)
            {
                // With drops the clock runs: 0–21 hours before each command, so the 48-hour window both holds and closes
                if (withDrops)
                {
                    s.Advance(TimeSpan.FromHours(3 * ((b / 8) % 8)));
                }

                var before = s.State;
                var logLength = s.Log.Count;
                ICommand Base() => withDrops
                    ? DropCommandFor(s, b, withChoice, rerolls != RerollMode.None)
                    : CommandFor(s, b, withChoice, rerolls != RerollMode.None);
                ICommand Other() => withProofs ? ProofCommandFor(s, b, Base) : Base();
                ICommand Corrected() => withCorrections ? CorrectionCommandFor(s, b, Other) : Other();
                ICommand Lived() => lifecycle ? LifecycleCommandFor(s, b, Corrected) : Corrected();
                var command = withUndo || withRulesetChanges ? CombinedCommandFor(s, b, withUndo, withRulesetChanges, Lived) : Lived();
                s.Act(command);
                afterEach?.Invoke(s, command, before, logLength);
            }
        });
    }

    /// <summary>
    /// With the season lifecycle (C10, D-101), the admin's season commands with arguments 4–6 are instead: 4 a deadline a
    /// few hours ahead of the engine's clock (4, 8, 12 or 16 by bits 0–1), 5 the scheduler's <see cref="ReachDeadline"/>,
    /// 6 the admin checking the head of the proof queue (approve, or reject with bits 0–1 = 3), so the queue empties and
    /// the season can finish; 7 stays the next status.
    /// </summary>
    private static ICommand LifecycleCommandFor(Scenario s, byte b, Func<ICommand> otherwise)
    {
        var arg = b / 32;
        if ((b / 4) % 8 != 7 || arg is < 4 or > 6)
        {
            return otherwise();
        }

        var queue = ProofReviewOrder.Order(s.State);
        var head = queue.Count > 0 ? queue[0] : Guid.Empty;
        return arg switch
        {
            4 => new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromHours(4 * ((b % 4) + 1))),
            5 => new ReachDeadline(),
            _ when b % 4 == 3 => new RejectProof(head, "на скрине другая игра"),
            _ => new ApproveProof(head, null, "проверено"),
        };
    }

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_the_season_lifecycle(int seed, byte[] script) =>
        Play(seed, script, CheckFinishInvariants, withDrops: true, withCorrections: true, withProofs: true, finishes: true, lifecycle: true);

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_the_season_lifecycle_rerolls_and_a_choice_of_games(int seed, byte[] script) =>
        Play(seed, script, CheckFinishInvariants, withChoice: true, rerolls: RerollMode.Coins, withDrops: true, withCorrections: true, withProofs: true, finishes: true, lifecycle: true);

    [Property(MaxTest = 50)]
    public void Same_seed_and_commands_give_the_same_log_with_the_season_lifecycle(int seed, byte[] script)
    {
        var first = Play(seed, script, withDrops: true, withCorrections: true, withProofs: true, finishes: true, lifecycle: true);
        var second = Play(seed, script, withDrops: true, withCorrections: true, withProofs: true, finishes: true, lifecycle: true);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Fact]
    public void Lifecycle_variant_reaches_the_deadline_closing_and_the_result()
    {
        // Invariant 11 must meet its cases: over fixed scripts a turn is refused past the deadline, the scheduler closes
        // a season, a season finishes with its result
        var refused = false;
        var closed = false;
        var finished = false;
        for (var seed = 0; seed < 60; seed++)
        {
            var x = (uint)seed + 11;
            var script = Enumerable.Range(0, 400).Select(_ => (byte)((x = (x * 1103515245) + 12345) >> 16)).ToArray();
            var s = Play(seed, script, (s, command, _, _) =>
            {
                refused |= !s.Last.IsAccepted && s.Last.Rejection!.Code == RejectionCodes.SeasonDeadlinePassed;
                closed |= s.Last.IsAccepted && command is ReachDeadline;
            }, withDrops: true, withCorrections: true, withProofs: true, finishes: true, lifecycle: true);
            finished |= s.Log.OfType<SeasonResultRecorded>().Any();
        }

        Assert.True(refused, "No turn command was refused past the deadline.");
        Assert.True(closed, "The scheduler never closed a season.");
        Assert.True(finished, "No season finished with a result.");
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

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_proofs(int seed, byte[] script) =>
        Play(seed, script, CheckProofInvariants, withProofs: true);

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_proofs_corrections_drops_and_rerolls(int seed, byte[] script) =>
        Play(seed, script, CheckProofInvariants, withChoice: true, rerolls: RerollMode.Coins, withDrops: true, withCorrections: true, withProofs: true);

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_finishes(int seed, byte[] script) =>
        Play(seed, script, CheckFinishInvariants, withDrops: true, withCorrections: true, withProofs: true, finishes: true);

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_finishes_rerolls_and_a_choice_of_games(int seed, byte[] script) =>
        Play(seed, script, CheckFinishInvariants, withChoice: true, rerolls: RerollMode.Coins, withDrops: true, withCorrections: true, withProofs: true, finishes: true);

    [Property(MaxTest = 50)]
    public void Same_seed_and_commands_give_the_same_log_with_finishes(int seed, byte[] script)
    {
        var first = Play(seed, script, withDrops: true, withCorrections: true, withProofs: true, finishes: true);
        var second = Play(seed, script, withDrops: true, withCorrections: true, withProofs: true, finishes: true);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Fact]
    public void Finish_variant_reaches_finishes_bonuses_freezes_and_revokes()
    {
        // The generator must exercise the finish rules, not only allow them: over a fixed set of scripts every finish
        // event comes out (a guard against a variant that never finishes)
        var seen = new HashSet<Type>();
        var bonus = false;
        var revokedBonus = false;
        for (var seed = 0; seed < 60; seed++)
        {
            // A fixed pseudo-random script per seed (a linear congruential generator, so the guard is deterministic)
            var x = (uint)seed + 1;
            var script = Enumerable.Range(0, 600).Select(_ => (byte)((x = (x * 1103515245) + 12345) >> 16)).ToArray();
            var s = Play(seed, script, withDrops: true, withCorrections: true, withProofs: true, finishes: true);
            seen.UnionWith(s.Log.Select(e => e.GetType()).Where(t => t == typeof(PlayerFinished) || t == typeof(PlayerFrozen) || t == typeof(PlayerFinishRevoked)));
            bonus |= s.Log.OfType<PointsChanged>().Any(e => e.Reason == PointsReason.FinishBonus);
            revokedBonus |= s.Log.OfType<PointsChanged>().Any(e => e.Reason == PointsReason.FinishBonusRevoked);
        }

        Assert.Contains(typeof(PlayerFinished), seen);
        Assert.Contains(typeof(PlayerFrozen), seen);
        Assert.Contains(typeof(PlayerFinishRevoked), seen);
        Assert.True(bonus, "No finish bonus in the finish variant.");
        Assert.True(revokedBonus, "No finish bonus taken back in the finish variant.");
    }

    [Fact]
    public void Finish_variant_reaches_shared_places_and_ties_broken_by_a_tiebreaker()
    {
        // Invariant 10 must meet the cases it is about: over fixed scripts, rows sharing a place (not the first) and rows
        // with equal points but different places (a tiebreaker decided) both come out, and every tiebreak setting is used
        var shared = 0;
        var broken = 0;
        var settings = new HashSet<string>();
        for (var seed = 0; seed < 60; seed++)
        {
            var x = (uint)seed + 7;
            var script = Enumerable.Range(0, 400).Select(_ => (byte)((x = (x * 1103515245) + 12345) >> 16)).ToArray();
            Play(seed, script, (s, _, _, _) =>
            {
                var rows = Leaderboard.Build(s.State).Where(r => !r.IsFirst).ToList();
                shared += rows.GroupBy(r => r.Place).Count(g => g.Count() > 1);
                broken += rows.GroupBy(r => r.Points).Count(g => g.Select(r => r.Place).Distinct().Count() > 1);
                settings.Add(string.Join(",", s.State.Rules.Ranking.Tiebreakers));
            }, withDrops: true, withCorrections: true, withProofs: true, finishes: true);
        }

        Assert.True(shared > 0, "No shared place in the finish variant.");
        Assert.True(broken > 0, "No tie broken by a tiebreaker in the finish variant.");
        Assert.Equal(5, settings.Count);
    }

    [Property(MaxTest = 50)]
    public void Same_seed_and_commands_give_the_same_log_with_proofs(int seed, byte[] script)
    {
        var first = Play(seed, script, withDrops: true, withCorrections: true, withProofs: true);
        var second = Play(seed, script, withDrops: true, withCorrections: true, withProofs: true);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Fact]
    public void Every_proof_command_is_reachable_from_the_script()
    {
        // Bits 2–7 pick a proof command for five arguments; each kind must come out of the generator
        var s = NewSeason(0);
        var kinds = Enumerable.Range(0, 256)
            .Select(b => ProofCommandFor(s, (byte)b, () => new RollGame(Guid.Empty)).GetType())
            .ToHashSet();

        Assert.Superset(new HashSet<Type> { typeof(SubmitProof), typeof(ApproveProof), typeof(RejectProof) }, kinds);
    }

    [Fact]
    public void Every_corrected_hours_value_is_reachable_from_the_script()
    {
        // The generator must be able to produce every value, including the ones above the dice and coins ceilings
        var reachable = Enumerable.Range(0, 256)
            .Where(b => (b / 4) % 8 == 6 && b / 32 is 4 or 6)
            .Select(b => s_correctedHours[HoursIndex(b / 32, (byte)b)])
            .ToHashSet();

        Assert.Equal(s_correctedHours.ToHashSet(), reachable);
    }

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

    /// <summary>
    /// SE6 / D-98: the review queue is exactly the completed runs neither approved nor rejected, finishes first, then
    /// by completion time, ties by run id; a proof only on a completed or rejected run; plus every invariant of
    /// <see cref="CheckInvariants"/>.
    /// </summary>
    private static void CheckProofInvariants(Scenario s, ICommand command, SeasonState before, int logLengthBefore)
    {
        CheckInvariants(s, command, before, logLengthBefore);

        var expected = s.State.Runs.Values
            .Where(r => r.Status == RunStatus.Completed && r.Proof?.Status is null or ProofStatus.Pending)
            .OrderByDescending(r => IsUpToFinish(s.State, r)) // Q-3: every run up to a standing finish goes on top
            .ThenBy(r => r.CompletedAt)
            .ThenBy(r => r.RunId)
            .Select(r => r.RunId);
        Assert.Equal(expected, ProofReviewOrder.Order(s.State));

        var finish = s.State.Map.Cells.Single(c => c.Type == CellType.Finish).Id;
        foreach (var run in s.State.Runs.Values)
        {
            // A proof only on a run that was completed; a rejected run always has a rejected proof and back
            Assert.True(run.Proof is null || run.Status is RunStatus.Completed or RunStatus.Rejected, $"A proof on a {run.Status} run.");
            Assert.Equal(run.Status == RunStatus.Rejected, run.Proof?.Status == ProofStatus.Rejected);
            Assert.Equal(run.Status is RunStatus.Completed or RunStatus.Rejected, run.CompletedAt is not null);

            if (run.Status != RunStatus.Completed)
            {
                continue;
            }

            // D-98 (2): ReachedFinish follows the run's latest move — it ended on the finish
            Assert.Equal(
                s.EffectiveLog.OfType<PlayerMoved>().LastOrDefault(m => m.RunId == run.RunId)?.To == finish,
                run.ReachedFinish);

            // D-98: Moved is the cells the run really entered (sign × path), not the steps rolled
            Assert.Equal(MovedCells(s.EffectiveLog, run.RunId), run.Moved);
            Assert.True(run.Moved >= 0, $"The run moved {run.Moved} cells.");

            // A run's cells exceed its dice only when a later reduction could not take them back: absorbed by a finisher's
            // surplus (Q-3), ignored for a frozen first (D-99) — they stay after a later revoke — or blocked at the start
            // after the token went back without them: the admin transferred it, or another cause (a drop's penalty, another
            // run's reject or correction) moved it back — D-98 moves only through entered cells (C13 long run, D-111)
            var detached = s.EffectiveLog.Any(e =>
                (e is PlayerFinished f && f.PlayerId == run.PlayerId)
                || (e is PlayerMoved { Reason: MoveReason.AdminAdjustment } m && m.PlayerId == run.PlayerId)
                || (e is PlayerMoved { Steps: < 0 } back && back.PlayerId == run.PlayerId && back.RunId != run.RunId));
            Assert.True(
                detached || run.Moved <= run.Dice.Concat(run.ChallengeDice).Sum(d => d.Value),
                $"The run moved {run.Moved} cells for fewer dice.");
        }
    }

    /// <summary>
    /// P2–P6, T3, T4, RR7, RR8, SE7, invariants 8 and 9 (C9a, D-16, D-99, Q-3, Q-4), after every command of the finish
    /// variant: the finish state is the fold of the finish events; orders go 1, 2, 3… and are never reused; the first is
    /// the lowest standing order; each standing finisher holds the bonus of his place among the standing finishers (a
    /// reference table, not the engine's); only the first is ever frozen, and exactly when all his runs up to the finish
    /// are approved (or at once without required approval); a finisher's token and a frozen player's points, coins and
    /// resources do not change until a revoke (the admin's own adjustments aside); only a run's completion finishes.
    /// </summary>
    private static void CheckFinishInvariants(Scenario s, ICommand command, SeasonState before, int logLengthBefore)
    {
        CheckProofInvariants(s, command, before, logLengthBefore);

        IReadOnlyList<IGameEvent> events = s.Last.IsAccepted ? s.Last.Events : [];
        var finishes = FoldFinishes(s.EffectiveLog);
        var standing = s.State.Players.Values.Where(p => p.Finish is not null).OrderBy(p => p.Finish!.Order).ToList();

        // The state is the fold of the finish events; surplus never negative
        foreach (var player in s.State.Players.Values)
        {
            Assert.Equal(finishes.Standing.GetValueOrDefault(player.PlayerId), player.Finish);
            Assert.True(player.Finish is null || player.Finish.Surplus >= 0, $"{player.Name} has a negative surplus.");

            // Q-4: a bonus is held only with a standing finish
            Assert.True(player.Finish is not null || finishes.Bonus.GetValueOrDefault(player.PlayerId) == 0, $"{player.Name} holds a bonus without a finish.");
        }

        // Orders: each finish takes the next number, never reused — not even the number of an undone finish (D-104)
        var orders = s.Log.OfType<PlayerFinished>().Select(f => f.Order).ToList();
        Assert.Equal(Enumerable.Range(1, orders.Count), orders);
        Assert.All(finishes.Orders, order => Assert.Contains(order, orders));

        // The first is the lowest standing order; only he may be frozen
        var first = standing.FirstOrDefault();
        Assert.Equal(first?.PlayerId, FinishLine.First(s.State));
        Assert.All(standing.Where(p => p.Finish!.Frozen), p => Assert.Equal(first!.PlayerId, p.PlayerId));

        // 9 / Q-4 / D-113: each standing finisher holds the bonus of his place now, by the table he finished under (the
        // reference fold keeps it)
        for (var place = 1; place <= standing.Count; place++)
        {
            var table = finishes.Standing[standing[place - 1].PlayerId].BonusRules!;
            Assert.Equal(ReferenceBonus(table, place), standing[place - 1].Finish!.Bonus);
        }

        // Q-3: frozen exactly when all runs up to the finish are approved (or at once without required approval)
        if (first is not null)
        {
            var allApproved = UpToFinishRuns(s.State, first).All(r => r.Proof?.Status == ProofStatus.Approved);
            Assert.True(!allApproved || first.Finish!.Frozen, $"{first.Name} is first with all runs approved but not frozen.");
            // D-115: the freeze follows the rule in force when the first finished; a later change of the rule changes nothing
            if (RequiredApprovalAtFinish(s.EffectiveLog, first.PlayerId))
            {
                Assert.True(!first.Finish!.Frozen || allApproved, $"{first.Name} is frozen before all his runs up to the finish are approved.");
            }
            else
            {
                Assert.True(first.Finish!.Frozen, $"{first.Name} finished first without required approval but is not frozen.");
            }
        }

        // A standing finish links to a completed run of the player. The one exception is open (D-99 decides corrections
        // only): a first frozen at once without required approval keeps his final place when that run is rejected
        foreach (var p in standing)
        {
            var run = s.State.Runs[p.Finish!.RunId];
            Assert.Equal(p.PlayerId, run.PlayerId);
            Assert.True(
                run.Status == RunStatus.Completed
                    || (run.Status == RunStatus.Rejected && p.Finish.Frozen && !RequiredApprovalAtFinish(s.EffectiveLog, p.PlayerId)),
                $"{p.Name}'s finish stands on a {run.Status} run.");
        }

        // The rest checks how a command's own events move finishes; an undo has none of those — it gives back what the
        // undone command changed, which the folds above and CheckUndo compare with the log without it (invariant 13)
        if (!s.Last.IsAccepted || command is UndoCommand)
        {
            return;
        }

        foreach (var was in before.Players.Values)
        {
            var now = s.State.Players[was.PlayerId];
            var adjusted = command is AdjustPlayer adjust && adjust.PlayerId == was.PlayerId;

            // P6 / T4 / 8 / 9: a finisher's position is fixed until the finish is revoked (the admin's transfer aside)
            if (was.Finish is not null && now.Finish?.Order == was.Finish.Order && !adjusted)
            {
                Assert.Equal(was.CellId, now.CellId);
                Assert.DoesNotContain(events, e => e is PlayerMoved m && m.PlayerId == was.PlayerId);
            }

            // P4 / T3 / 8: a frozen first gets and loses nothing until a revoke; rerolls are free (D-99)
            if (was.Finish?.Frozen == true && now.Finish?.Order == was.Finish.Order && !adjusted)
            {
                Assert.Equal((was.Points, was.Coins, was.Resources), (now.Points, now.Coins, now.Resources));
                Assert.DoesNotContain(events, e => e is ManualEffectCreated c && c.PlayerId == was.PlayerId);
            }

            // A standing finish keeps its order, run and time, and is never unfrozen
            if (was.Finish is not null && now.Finish is not null)
            {
                Assert.Equal((was.Finish.Order, was.Finish.RunId, was.Finish.FinishedAt), (now.Finish.Order, now.Finish.RunId, now.Finish.FinishedAt));
                Assert.True(!was.Finish.Frozen || now.Finish.Frozen, $"{was.Name} was unfrozen without a revoke.");
            }
        }

        // Q-4: bonuses move only when the finishers change: a finish or a revoke in the same command
        var finishersChanged = events.Any(e => e is PlayerFinished or PlayerFinishRevoked);
        Assert.True(
            finishersChanged || !events.Any(e => e is PointsChanged { Reason: PointsReason.FinishBonus or PointsReason.FinishBonusRevoked }),
            "A bonus changed without a finish or a revoke.");

        // RR8 / D-99: only a run's completion finishes, right after its move onto the finish; the surplus is the steps burned
        var finished = events.OfType<PlayerFinished>().ToList();
        Assert.True(finished.Count <= 1, "Two finishes in one command.");
        var list = events.ToList();
        if (finished.SingleOrDefault() is { } f)
        {
            var complete = Assert.IsType<CompleteRun>(command);
            Assert.Equal(complete.PlayerId, f.PlayerId);
            Assert.Null(before.Players[f.PlayerId].Finish);
            Assert.Equal(before.Players[f.PlayerId].ActiveRunId, f.RunId);
            Assert.Equal(s.Clock.UtcNow, f.FinishedAt);
            var index = list.IndexOf(f);
            var moved = list.Take(index).OfType<PlayerMoved>().Last();
            Assert.Equal(
                (f.PlayerId, s.State.Map.Cells.Single(c => c.Type == CellType.Finish).Id, MoveReason.CompletionRoll),
                (moved.PlayerId, moved.To, moved.Reason));
            Assert.Equal(moved.Steps - moved.Path.Count, f.Surplus);
        }

        // D-102: a resolution command resolves exactly its effect; a player only their own, the admin always with a comment
        if (command is ResolveManualEffect resolve && s.Last.IsAccepted)
        {
            var resolved = Assert.IsType<ManualEffectResolved>(Assert.Single(events));
            Assert.Equal((resolve.EffectId, resolve.Outcome), (resolved.EffectId, resolved.Outcome));
            Assert.True(resolve.PlayerId is null || resolve.PlayerId == resolved.PlayerId, "A player resolved someone else's effect.");
            Assert.True(resolve.PlayerId is not null || resolved.Comment.Length > 0, "The admin resolved without a comment.");
        }

        // Q-3: a finish is revoked only by a reject or a reduction of a run up to the finish
        foreach (var revoked in events.OfType<PlayerFinishRevoked>())
        {
            Assert.True(
                command is RejectProof or CorrectRunHours or ChangeRunDifficulty or ApproveProof { Difficulty: not null },
                $"{command} revoked a finish.");
            Assert.NotNull(before.Players[revoked.PlayerId].Finish);
            Assert.Null(s.State.Players[revoked.PlayerId].Finish);
        }

        // Q-3: the surplus changes only by a correction or a reject, never with a revoke of the same player
        foreach (var changed in events.OfType<FinishSurplusChanged>())
        {
            Assert.True(command is RejectProof or CorrectRunHours or ChangeRunDifficulty or ApproveProof, $"{command} changed a surplus.");
            Assert.NotEqual(0, changed.Delta);
        }

        // P4: a freeze only at a completion without required approval, an approval, or a command that moved the first place
        foreach (var frozen in events.OfType<PlayerFrozen>())
        {
            Assert.Equal(FinishLine.First(s.State), frozen.PlayerId);
            Assert.False(before.Players[frozen.PlayerId].Finish?.Frozen ?? false, "Frozen twice.");
            var movedFirst = events.Any(e => e is PlayerFinishRevoked);
            switch (command)
            {
                case CompleteRun:
                    Assert.False(before.Rules.Finish.RequireApprovalForFirst, "Frozen at the finish with approval required.");
                    break;
                case ApproveProof approve when !movedFirst:
                    Assert.Contains(approve.RunId, UpToFinishRuns(s.State, s.State.Players[frozen.PlayerId]).Select(r => r.RunId));
                    break;
                case ApproveProof or RejectProof or CorrectRunHours or ChangeRunDifficulty:
                    Assert.True(movedFirst, $"{command} froze a player without moving the first place.");
                    break;
                default:
                    throw new Xunit.Sdk.XunitException($"{command} froze a player.");
            }
        }
    }

    /// <summary>Q-4 reference: place 1 (the first) nothing, place 2 the first element of the list, …, past it the value after the list.</summary>
    /// <summary>Whether the first's approval was required by the rules in force at the player's last finish (D-115).</summary>
    private static bool RequiredApprovalAtFinish(IEnumerable<IGameEvent> log, Guid playerId)
    {
        var required = true;
        var atFinish = true;
        foreach (var e in log)
        {
            switch (e)
            {
                case SeasonCreated created:
                    required = created.Ruleset.Finish.RequireApprovalForFirst;
                    break;
                case RulesetChanged changed:
                    required = changed.Ruleset.Finish.RequireApprovalForFirst;
                    break;
                case PlayerFinished finished when finished.PlayerId == playerId:
                    atFinish = required;
                    break;
            }
        }

        return atFinish;
    }

    private static int ReferenceBonus(FinishBonusRules rules, int place) =>
        place == 1 ? 0 : place - 2 < rules.ByOrder.Count ? rules.ByOrder[place - 2] : rules.AfterList;

    /// <summary>Q-3: the player's completed runs up to and including the finishing one (by completion time).</summary>
    // Runs completed in the same instant are told apart by their ids, given in the order the runs were rolled (C13 long
    // run: a run completed after the finish in the same second is not up to the finish)
    private static IEnumerable<RunState> UpToFinishRuns(SeasonState state, SeasonPlayer player) =>
        player.Finish is { } finish
            ? state.Runs.Values.Where(r => r.PlayerId == player.PlayerId && r.Status == RunStatus.Completed
                && (r.CompletedAt < state.Runs[finish.RunId].CompletedAt
                    || (r.CompletedAt == state.Runs[finish.RunId].CompletedAt && r.RunId.CompareTo(finish.RunId) <= 0)))
            : [];

    /// <summary>Q-3: the run counts for the player's position — he has a finish and completed it at or before the finish.</summary>
    private static bool IsUpToFinish(SeasonState state, RunState run) =>
        state.Players.TryGetValue(run.PlayerId, out var player) && UpToFinishRuns(state, player).Any(r => r.RunId == run.RunId);

    private static bool IsFinishEvent(IGameEvent e) =>
        e is PlayerFinished or PlayerFrozen or PlayerFinishRevoked or FinishSurplusChanged
            or PointsChanged { Reason: PointsReason.FinishBonus or PointsReason.FinishBonusRevoked };

    /// <summary>Q-4: players other than <paramref name="owner"/> change only by their bonus recalculation and a freeze.</summary>
    private static void CheckOthersOnlyRecalculated(Scenario s, SeasonState before, Guid owner)
    {
        foreach (var now in s.State.Players.Values.Where(p => p.PlayerId != owner))
        {
            var then = before.Players[now.PlayerId];
            var bonus = s.Last.Events.OfType<PointsChanged>()
                .Where(e => e.PlayerId == now.PlayerId && e.Reason is PointsReason.FinishBonus or PointsReason.FinishBonusRevoked)
                .Sum(e => e.Delta);
            Assert.Equal(then.Points + bonus, now.Points);

            // D-100: a bonus change numbers the player's points anew; without one the number stays
            var bonusChanged = s.Last.Events.OfType<PointsChanged>()
                .Any(e => e.PlayerId == now.PlayerId && e.Delta != 0 && e.Reason is PointsReason.FinishBonus or PointsReason.FinishBonusRevoked);
            Assert.Equal(then with { Points = now.Points, Finish = now.Finish, PointsTick = bonusChanged ? now.PointsTick : then.PointsTick }, now);
            Assert.True(!bonusChanged || now.PointsTick > then.PointsTick, "A bonus change did not number the points anew.");
            if (then.Finish is null)
            {
                Assert.Null(now.Finish);
            }
            else
            {
                Assert.NotNull(now.Finish);
                Assert.Equal(then.Finish with { Bonus = now.Finish.Bonus, Frozen = now.Finish.Frozen }, now.Finish);
            }
        }
    }

    /// <summary>
    /// Q-3: a finisher's run up to the finish changed by <paramref name="steps"/>: an increase adds to the surplus; a
    /// reduction within the surplus takes from it; beyond it the finish is revoked and the token goes back by the rest
    /// along the walked path. Nothing else of the owner's finish moves.
    /// </summary>
    private static void CheckFinishPosition(
        IReadOnlyList<IGameEvent> events, SeasonState before, SeasonPlayer was, Guid runId, int steps, MoveReason reason)
    {
        var surplus = events.OfType<FinishSurplusChanged>().Where(e => e.PlayerId == was.PlayerId).ToList();
        var revokes = events.OfType<PlayerFinishRevoked>().Where(e => e.PlayerId == was.PlayerId).ToList();
        var moves = events.OfType<PlayerMoved>().Where(e => e.PlayerId == was.PlayerId).ToList();
        var have = was.Finish!.Surplus;
        if (steps >= 0 || -steps <= have)
        {
            Assert.Equal(steps == 0 ? [] : [new FinishSurplusChanged(was.PlayerId, steps)], surplus);
            Assert.Empty(revokes);
            Assert.Empty(moves);
            return;
        }

        Assert.Single(revokes);
        var path = Movement.Backward(before.Map, was.Path, -steps - have);
        var moved = Assert.Single(moves);
        Assert.Equal((was.CellId, path[^1], reason, (Guid?)runId), (moved.From, moved.To, moved.Reason, moved.RunId));
        Assert.Equal(path, moved.Path);
    }

    private sealed record FinishFold(Dictionary<Guid, FinishState> Standing, List<int> Orders, Dictionary<Guid, int> Bonus);

    /// <summary>Folds the finish events of the log independently of the engine, checking each on the way.</summary>
    private static FinishFold FoldFinishes(IEnumerable<IGameEvent> log)
    {
        var standing = new Dictionary<Guid, FinishState>();
        var orders = new List<int>();
        var bonus = new Dictionary<Guid, int>();

        // D-113: a finisher keeps the bonus table in force at his finish, until the admin's recalculation
        FinishBonusRules? table = null;
        bool? approval = null;
        foreach (var e in log)
        {
            switch (e)
            {
                case SeasonCreated created:
                    table = new FinishBonusRules(created.Ruleset.Finish.BonusByOrder, created.Ruleset.Finish.BonusAfterList);
                    approval = created.Ruleset.Finish.RequireApprovalForFirst;
                    break;
                case RulesetChanged changed:
                    table = new FinishBonusRules(changed.Ruleset.Finish.BonusByOrder, changed.Ruleset.Finish.BonusAfterList);
                    approval = changed.Ruleset.Finish.RequireApprovalForFirst;
                    break;
                case FinishBonusRulesRefreshed refreshed:
                    Assert.Equal(table, refreshed.Rules);
                    foreach (var id in standing.Keys.ToList())
                    {
                        standing[id] = standing[id] with { BonusRules = table };
                    }

                    break;
                case PlayerFinished finished:
                    Assert.False(standing.ContainsKey(finished.PlayerId), "Finished twice without a revoke.");
                    Assert.True(finished.Surplus >= 0, "A negative surplus.");
                    orders.Add(finished.Order);
                    standing[finished.PlayerId] = new FinishState(
                        finished.Order, finished.RunId, finished.FinishedAt, false, bonus.GetValueOrDefault(finished.PlayerId), finished.Surplus, table, approval);
                    break;
                case FinishSurplusChanged surplus:
                    Assert.True(standing.TryGetValue(surplus.PlayerId, out var withSurplus), "A surplus change without a finish.");
                    standing[surplus.PlayerId] = withSurplus with { Surplus = withSurplus.Surplus + surplus.Delta };
                    Assert.True(standing[surplus.PlayerId].Surplus >= 0, "The surplus went negative.");
                    break;
                case PlayerFrozen frozen:
                    Assert.True(standing.TryGetValue(frozen.PlayerId, out var toFreeze), "Frozen without a finish.");
                    Assert.False(toFreeze.Frozen, "Frozen twice.");
                    Assert.Equal(standing.Values.Min(x => x.Order), toFreeze.Order);
                    standing[frozen.PlayerId] = toFreeze with { Frozen = true };
                    break;
                case PlayerFinishRevoked revoked:
                    Assert.True(standing.Remove(revoked.PlayerId), "Revoked without a finish.");
                    break;
                case PointsChanged { Reason: PointsReason.FinishBonus or PointsReason.FinishBonusRevoked } change:
                    Assert.True(change.Reason == PointsReason.FinishBonus ? change.Delta > 0 : change.Delta < 0, $"A bonus change {change}.");
                    bonus[change.PlayerId] = bonus.GetValueOrDefault(change.PlayerId) + change.Delta;
                    Assert.True(bonus[change.PlayerId] >= 0, "A bonus taken back that was not held.");
                    if (standing.TryGetValue(change.PlayerId, out var holder))
                    {
                        standing[change.PlayerId] = holder with { Bonus = bonus[change.PlayerId] };
                    }

                    break;
            }
        }

        return new FinishFold(standing, orders, bonus);
    }

    /// <summary>Cells a run really moved, folded from its logged moves: each move counts its path, signed by its direction.</summary>
    private static int MovedCells(IEnumerable<IGameEvent> log, Guid runId) =>
        log.OfType<PlayerMoved>().Where(m => m.RunId == runId).Sum(m => Math.Sign(m.Steps) * m.Path.Count);

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

        // 11 / K-7 / D-101: no player turn command is accepted once the engine's clock has reached the deadline — so no
        // roll, reroll, start, completion dice or drop penalty of a player after it
        if (s.Last.IsAccepted && before.Deadline is { } deadline && s.Clock.UtcNow >= deadline)
        {
            Assert.False(
                command is RollGame or DeclareAlreadyPlayed or Reroll or MakeChoice or StartRun or CompleteRun or DropRun or TechReroll,
                $"{command} was accepted at {s.Clock.UtcNow:O}, past the deadline {deadline:O}.");
            Assert.DoesNotContain(s.Last.Events, e => e is GameRolled or GameChoiceRolled or GameRerolled or RunStarted or CompletionRolled or RunDropped);
        }

        // SE4 / D-101: the result is written once, at the finish, and never changes
        var results = s.Log.OfType<SeasonResultRecorded>().ToList();
        Assert.True(results.Count <= 1, "The result was written twice.");
        Assert.Equal(results.SingleOrDefault()?.Rows, s.State.Result);
        Assert.True(before.Result is null || before.Result == s.State.Result, "The result changed.");
        Assert.Equal(s.State.Status is SeasonStatus.Finished or SeasonStatus.Archived, s.State.Result is not null);

        // 1. Replaying the log gives the stored state
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));

        var expected = Fold(s.EffectiveLog);
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
            var transfers = s.EffectiveLog.OfType<PlayerMoved>().Count(e => e.PlayerId == player.PlayerId && e.Steps == 0);
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
        var resolved = s.EffectiveLog.OfType<ManualEffectResolved>().Select(e => e.EffectId).ToHashSet();
        Assert.Equal(
            s.EffectiveLog.OfType<ManualEffectCreated>()
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

        // D-100: points changes are numbered by the fold of the log — every non-zero change the next number, each player
        // keeps his last one. D-104: an undone command's numbers are never reused, and its players get their earlier
        // numbers back — so the count runs over the whole log and a player keeps his last number of a command that counts
        var ticks = new Dictionary<Guid, long>();
        long changes = 0;
        var undone = s.UndoneCommands();
        foreach (var logged in s.History)
        {
            var counts = Scenario.IsEffective(logged, undone);
            foreach (var e in logged.Events.OfType<PointsChanged>().Where(e => e.Delta != 0))
            {
                ++changes;
                if (counts)
                {
                    ticks[e.PlayerId] = changes;
                }
            }
        }

        Assert.Equal(changes, s.State.PointsChanges);
        Assert.All(s.State.Players.Values, p => Assert.Equal(ticks.GetValueOrDefault(p.PlayerId), p.PointsTick));

        // 10 / P5, P7, P8, P11: the entries the engine ranks are the log's (points, cell, standing finish, freeze, counted
        // runs, tick), and the leaderboard equals a reference ranked here from them by the season's tiebreakers
        var entries = ReferenceEntries(s, expected, ticks);
        Assert.Equal(entries.OrderBy(e => e.PlayerId), Leaderboard.Entries(s.State).OrderBy(e => e.PlayerId));
        Assert.Equal(ReferenceLeaderboard(entries, s.State.Rules.Ranking, s.State.Map), Leaderboard.Build(s.State));
    }

    /// <summary>
    /// Invariant 10 reference entries (D-100), from the log only: points and cell by <see cref="Fold"/>, standing finishes
    /// and freezes by <see cref="FoldFinishes"/>, counted runs = completions minus rejects, ticks by numbering the non-zero
    /// points changes.
    /// </summary>
    private static List<RankingEntry> ReferenceEntries(Scenario s, Reference folded, Dictionary<Guid, long> ticks)
    {
        var completed = s.EffectiveLog.OfType<RunCompleted>().Select(e => (e.RunId, e.PlayerId)).ToHashSet();
        completed.ExceptWith(s.EffectiveLog.OfType<ProofRejected>().Select(e => (e.RunId, e.PlayerId)));
        var runs = completed.GroupBy(r => r.PlayerId).ToDictionary(g => g.Key, g => g.Count());
        var standing = FoldFinishes(s.EffectiveLog).Standing;

        return [.. folded.Players.Select(p => new RankingEntry(
            p.Key,
            p.Value.Points,
            p.Value.CellId,
            standing.TryGetValue(p.Key, out var finish) ? finish.Order : null,
            finish?.Frozen ?? false,
            runs.GetValueOrDefault(p.Key),
            ticks.GetValueOrDefault(p.Key)))];
    }

    /// <summary>
    /// Invariant 10 reference ranking (D-100), independent of the engine: the standing finish with the lowest order is
    /// place 1; every other player's place is 1 + the number of other non-first players strictly better by (points
    /// descending, then the configured tiebreak keys), plus 1 when there is a first; rows by place, then by id. Cells to
    /// the finish by a backward breadth-first search from the finish cells.
    /// </summary>
    private static List<LeaderboardRow> ReferenceLeaderboard(List<RankingEntry> entries, RankingRules rules, MapGraph map)
    {
        var distance = map.Cells.Where(c => c.Type == CellType.Finish).ToDictionary(c => c.Id, _ => 0);
        var queue = new Queue<string>(distance.Keys);
        while (queue.TryDequeue(out var cell))
        {
            foreach (var edge in map.Edges.Where(e => e.To == cell && !distance.ContainsKey(e.From)))
            {
                distance[edge.From] = distance[cell] + 1;
                queue.Enqueue(edge.From);
            }
        }

        // The key compared lexicographically, larger is better: points, then each tiebreaker in the configured order
        long[] Key(RankingEntry e) =>
            [e.Points, .. rules.Tiebreakers.Select(t => t == Tiebreaker.CompletedRuns ? e.CompletedRuns : -e.PointsTick)];

        static bool Better(long[] a, long[] b)
        {
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return a[i] > b[i];
                }
            }

            return false;
        }

        var first = entries.Where(e => e.FinishOrder is not null).MinBy(e => e.FinishOrder);
        var others = entries.Where(e => e != first).ToList();
        var rows = new List<LeaderboardRow>();
        if (first is not null)
        {
            rows.Add(new LeaderboardRow(first.PlayerId, 1, first.Points, distance.TryGetValue(first.CellId, out var d) ? d : null, true, !first.Frozen));
        }

        var shift = first is null ? 0 : 1;
        rows.AddRange(others
            .Select(e => new LeaderboardRow(
                e.PlayerId,
                1 + shift + others.Count(o => Better(Key(o), Key(e))),
                e.Points,
                distance.TryGetValue(e.CellId, out var cells) ? cells : null,
                false,
                false))
            .OrderBy(r => r.Place)
            .ThenBy(r => r.PlayerId));
        return rows;
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
            case ChangeSeasonStatus { To: SeasonStatus.Finished }:
                // SE4 / D-101: finished only with an empty proof queue; the result is the leaderboard at that moment
                Assert.Equal(SeasonStatus.Closing, before.Status);
                Assert.Empty(ProofReviewOrder.Order(before));
                Assert.Equal(
                    [new SeasonStatusChanged(SeasonStatus.Closing, SeasonStatus.Finished), new SeasonResultRecorded(Leaderboard.Build(before))],
                    events);
                break;
            case ChangeSeasonStatus change:
                Assert.Equal([new SeasonStatusChanged(before.Status, change.To)], events);
                Assert.Equal(before.Status + 1, change.To);
                break;
            case ReachDeadline:
                // D-101: only an Active season, only once the deadline has come by the engine's clock
                Assert.Equal(SeasonStatus.Active, before.Status);
                Assert.True(before.Deadline is { } d && s.Clock.UtcNow >= d, "Closed before the deadline.");
                Assert.Equal([new SeasonStatusChanged(SeasonStatus.Active, SeasonStatus.Closing)], events);
                break;
            case SetPlayerInactive inactive:
                Assert.Equal([new PlayerInactivitySet(inactive.PlayerId, inactive.IsInactive)], events);
                break;
            case SetSeasonDeadline deadline:
                // D-101: the deadline changes only in Draft and Active
                Assert.True(before.Status is SeasonStatus.Draft or SeasonStatus.Active, $"Deadline changed while {before.Status}.");
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
            case SubmitProof submit:
                CheckAcceptedProof(s, submit, before);
                break;
            case ApproveProof approve:
                CheckAcceptedApproval(s, approve, before);
                break;
            case RejectProof reject:
                CheckAcceptedReject(s, reject, before);
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

        // D-99: a frozen first rerolls for free: no coins, no coupon, no bad event
        var frozen = was.Finish?.Frozen == true;
        if (frozen)
        {
            Assert.Equal(2, events.Count);
            Assert.Equal((was.Coins, was.Resources), (now.Coins, now.Resources));
        }

        // Payment strictly in order; coins and coupons never go negative for a reroll
        var payment = frozen ? rerolled.Payment : ExpectedPayment(was, before.Rules);
        Assert.NotNull(payment);
        Assert.Equal(payment, rerolled.Payment);
        var paymentEvents = events.Skip(1).Take(events.Count - 2).ToList();
        switch (frozen ? RerollPayment.FreeThisRoll : rerolled.Payment)
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

        Assert.Equal(rerolled.Payment == RerollPayment.Coins && !frozen ? was.Coins - RerollCoins : was.Coins, now.Coins);
        Assert.Equal(
            rerolled.Payment == RerollPayment.FreeRerollResource && !frozen ? was.Resources[Coupon] - 1 : was.Resources[Coupon],
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
        Assert.Equal(SeasonStatus.Active, before.Status);
        var was = before.Players[complete.PlayerId];
        var run = before.Runs[was.ActiveRunId!.Value];

        // D-99: the finish, its bonus and a freeze are checked by CheckFinishInvariants; the rest follows D-96. A frozen
        // first gets no points, coins or event; a finisher does not move.
        var frozen = was.Finish?.Frozen == true;
        var events = s.Last.Events
            .Where(e => !IsFinishEvent(e))
            .ToList();
        if (was.Finish is not null)
        {
            Assert.Empty(events.OfType<PlayerMoved>());
        }

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
        var bonus = s.Last.Events.OfType<PointsChanged>()
            .Where(e => e.PlayerId == complete.PlayerId && e.Reason is PointsReason.FinishBonus or PointsReason.FinishBonusRevoked)
            .Sum(e => e.Delta);
        Assert.Equal(frozen ? 0 : sum, events.OfType<PointsChanged>().Sum(e => e.Delta));
        Assert.Equal(frozen ? [] : [PointsReason.CompletionRoll], events.OfType<PointsChanged>().Select(e => e.Reason));
        Assert.Equal(was.Points + (frozen ? 0 : sum) + bonus, s.State.Players[complete.PlayerId].Points);

        // W10 / Q-2 / D-96 (2): coins by the snapshot's rule and the counted hours, capped by the dice ceiling
        Assert.NotNull(run.Snapshot.Coins);
        var counted = Math.Min(completed.Hours, run.Snapshot.DiceCount.Max * run.Snapshot.DiceCount.HoursPerDie);
        var coins = frozen ? 0 : Math.Max(run.Snapshot.Coins.Min, (int)Math.Floor(counted * run.Snapshot.Coins.PerHour));
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
        if (rule.GrantEvent is { } kind && !frozen)
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
    private static void CheckCorrectionDifference(
        Scenario s, RunState run, int diff, SeasonState before, IReadOnlyList<IGameEvent>? only = null)
    {
        var events = only ?? s.Last.Events;
        var was = before.Players[run.PlayerId];

        // RR8 / D-99: a frozen first gets no points from a correction; bonus recalculations are checked apart (Q-4)
        var points = was.Finish?.Frozen == true ? 0 : diff;
        Assert.Equal(
            points == 0 ? [] : [new PointsChanged(run.PlayerId, points, PointsReason.RunCorrection, run.RunId)],
            events.OfType<PointsChanged>().Where(e => e.Reason == PointsReason.RunCorrection));
        var ownBonus = events.OfType<PointsChanged>()
            .Where(e => e.PlayerId == run.PlayerId && e.Reason is PointsReason.FinishBonus or PointsReason.FinishBonusRevoked)
            .Sum(e => e.Delta);
        Assert.Equal(was.Points + points + ownBonus, s.State.Players[run.PlayerId].Points);

        // Q-3: a finisher's position follows his runs up to the finish through the surplus; other runs never move him
        if (was.Finish is not null)
        {
            if (was.Finish.Frozen)
            {
                // D-99: the frozen first's place is final: a correction of any of his runs changes neither the surplus nor
                // the position, and does not revoke
                Assert.DoesNotContain(events, e => e is PlayerMoved or PlayerFinishRevoked or FinishSurplusChanged);
            }
            else if (IsUpToFinish(before, run))
            {
                // An earlier (not finishing) run can only lose the cells it really moved beyond its new dice (D-99)
                var corrected = s.State.Runs[run.RunId];
                var correctedSum = corrected.Dice.Concat(corrected.ChallengeDice).Sum(d => d.Value);
                var lost = diff < 0 && was.Finish.RunId != run.RunId ? -Math.Max(0, run.Moved - correctedSum) : diff;
                CheckFinishPosition(events, before, was, run.RunId, lost, MoveReason.RunCorrection);
            }
            else
            {
                Assert.DoesNotContain(events, e => e is PlayerMoved or FinishSurplusChanged or PlayerFinishRevoked);
            }

            CheckOwnTurnAndOthers(s, before, run.PlayerId);
            return;
        }

        // D-98 (1): forward by the difference; back only by the cells really moved beyond the new dice sum
        var after = s.State.Runs[run.RunId];
        var newSum = after.Dice.Concat(after.ChallengeDice).Sum(d => d.Value);
        var movedBefore = MovedCells(s.EffectiveLog.Take(s.EffectiveLog.Count - s.Last.Events.Count), run.RunId);
        var steps = diff switch
        {
            > 0 => diff,
            < 0 => -Math.Max(0, movedBefore - newSum),
            _ => 0,
        };
        var path = steps switch
        {
            > 0 => Movement.Forward(before.Map, was.CellId, steps),
            < 0 => Movement.Backward(before.Map, was.Path, -steps),
            _ => [],
        };

        // RR8 / D-99: a correction never brings a player to the finish: the token stops one cell before it
        var finish = before.Map.Cells.Single(c => c.Type == CellType.Finish).Id;
        var cut = path.Count > 0 && path[^1] == finish;
        if (cut)
        {
            path = [.. path.Take(path.Count - 1)];
        }

        var moves = events.OfType<PlayerMoved>().ToList();
        if (path.Count == 0)
        {
            Assert.Empty(moves);
        }
        else if (cut)
        {
            // The steps asked stay in the event (D-47); the path ends short of the finish
            var moved = Assert.Single(moves);
            Assert.Equal(
                (run.PlayerId, was.CellId, path[^1], MoveReason.RunCorrection, (Guid?)run.RunId),
                (moved.PlayerId, moved.From, moved.To, moved.Reason, moved.RunId));
            Assert.Equal(path, moved.Path);
        }
        else
        {
            Assert.Equal(
                [new PlayerMoved(run.PlayerId, was.CellId, path[^1], steps, [.. path], MoveReason.RunCorrection, run.RunId)],
                moves);
        }

        Assert.DoesNotContain(events, e => e is PlayerFinished or FinishSurplusChanged or PlayerFinishRevoked);
        CheckOwnTurnAndOthers(s, before, run.PlayerId);
    }

    /// <summary>The owner's turn is untouched; nobody else changes but by the bonus recalculation and a freeze (Q-4).</summary>
    private static void CheckOwnTurnAndOthers(Scenario s, SeasonState before, Guid owner)
    {
        var was = before.Players[owner];
        var now = s.State.Players[owner];
        Assert.Equal(
            (was.Phase, was.Offer, was.Choice, was.ActiveRunId, was.RerollsThisRoll, was.Resources, was.Exclusions),
            (now.Phase, now.Offer, now.Choice, now.ActiveRunId, now.RerollsThisRoll, now.Resources, now.Exclusions));
        CheckOthersOnlyRecalculated(s, before, owner);
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
            (run.RunId, run.PlayerId, run.Hours!.Value, correct.Hours, (EquatableArray<Die>)[.. run.Dice.Skip(newCount)], correct.Comment, s.Clock.UtcNow),
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

        // Order: the correction, points, move, coins; nothing else but the finish events (Q-3, Q-4)
        var order = new[] { typeof(RunHoursCorrected), typeof(PointsChanged), typeof(PlayerMoved), typeof(CoinsChanged) };
        var positions = events.Where(e => !IsFinishEvent(e)).Select(e => Array.IndexOf(order, e.GetType())).ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Distinct().Order(), positions);

        CheckCorrectionDifference(s, run, dice.Sum(d => d.Value) - run.Dice.Sum(d => d.Value), before);

        // Coins by the snapshot's formula for the new hours
        var coins = before.Players[run.PlayerId].Finish?.Frozen == true
            ? 0
            : ExpectedCoins(correct.Hours, run.Snapshot) - ExpectedCoins(run.Hours.Value, run.Snapshot);
        Assert.Equal(
            coins == 0 ? [] : [new CoinsChanged(run.PlayerId, coins, CoinsReason.RunCorrection, run.RunId)],
            events.OfType<CoinsChanged>());
        Assert.Equal(before.Players[run.PlayerId].Coins + coins, s.State.Players[run.PlayerId].Coins);
    }

    /// <summary>W8 / Q-5 / D-97: every die ⌈old × new sides / old sides⌉, both kept; the difficulty's event follows.</summary>
    private static void CheckAcceptedDifficultyChange(
        Scenario s, ChangeRunDifficulty change, SeasonState before, IReadOnlyList<IGameEvent>? only = null)
    {
        var events = only ?? s.Last.Events;
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
        Assert.All(events.Where(e => !IsFinishEvent(e)), e => Assert.Contains(e.GetType(), allowed));
        CheckCorrectionDifference(s, run, dice.Concat(challenge).Sum(d => d.After.Value - d.Before.Value), before, events);

        // Any pending event of the run's difficulty is resolved «not applicable» with the comment; the new one's is created
        var pending = before.ManualEffects.Values.Where(e => e.RunId == run.RunId && e.Source == ManualEffectSource.Difficulty);
        Assert.Equal(
            pending.Select(e => new ManualEffectResolved(e.EffectId, run.PlayerId, run.RunId, ManualEffectOutcome.NotApplicable, change.Comment)),
            events.OfType<ManualEffectResolved>());
        var created = events.OfType<ManualEffectCreated>().ToList();

        // D-99: a frozen first is given nothing, a difficulty event neither
        if (newRule.GrantEvent is { } kind && before.Players[run.PlayerId].Finish?.Frozen != true)
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

    private static bool IsHttpLink(string link) =>
        link.Length <= Limits.MaxProofLinkLength
        && Uri.TryCreate(link, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);

    /// <summary>W4 / D-98: only one's own completed unchecked run, a valid proof; only the proof changes.</summary>
    private static void CheckAcceptedProof(Scenario s, SubmitProof submit, SeasonState before)
    {
        Assert.True(before.Status is SeasonStatus.Active or SeasonStatus.Closing, $"A proof while {before.Status}.");
        var run = before.Runs[submit.RunId];
        Assert.Equal(submit.PlayerId, run.PlayerId);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.True(run.Proof?.Status is null or ProofStatus.Pending, "A checked proof was replaced.");
        Assert.InRange(submit.Links.Count, 0, Limits.MaxProofLinks);

        // D-116: up to five different screenshots; a proof is a link, a screenshot or a witness
        Assert.InRange(submit.Files.Count, 0, Limits.MaxProofFiles);
        Assert.Equal(submit.Files.Count, submit.Files.Distinct().Count());

        // D-98 (4): links and the note are stored trimmed, a blank note is no note
        EquatableArray<string> links = [.. submit.Links.Select(l => l.Trim())];
        var note = string.IsNullOrWhiteSpace(submit.Note) ? null : submit.Note.Trim();
        Assert.All(links, link => Assert.True(IsHttpLink(link), $"Link «{link}» was accepted."));
        Assert.True(links.Count > 0 || submit.Files.Count > 0 || submit.WitnessId is not null, "An empty proof was accepted.");
        Assert.True(
            submit.WitnessId is null || (submit.WitnessId != submit.PlayerId && before.Players.ContainsKey(submit.WitnessId.Value)),
            "An invalid witness was accepted.");
        Assert.True((note?.Length ?? 0) <= Limits.MaxCommentLength, "A note over the limit was accepted.");

        Assert.Equal(
            [new ProofSubmitted(run.RunId, submit.PlayerId, links, note, submit.WitnessId, s.Clock.UtcNow, submit.Files)],
            s.Last.Events);
        Assert.Equal(
            run with { Proof = new ProofState(ProofStatus.Pending, links, note, submit.WitnessId, s.Clock.UtcNow, null, submit.Files) },
            s.State.Runs[run.RunId]);
        Assert.Equal(before.Players, s.State.Players);
    }

    /// <summary>
    /// W4 / W8 / Q-5 / D-98: only a completed unchecked run; without a proof a comment is needed; a lower difficulty is a
    /// difficulty change in the same command, a higher one never; the approval is the last event but a freeze (Q-3).
    /// </summary>
    private static void CheckAcceptedApproval(Scenario s, ApproveProof approve, SeasonState before)
    {
        var all = s.Last.Events;
        var events = all.Where(e => !IsFinishEvent(e)).ToList();
        var owner = before.Runs[approve.RunId].PlayerId;

        // Q-3: a freeze comes right after the approval that completes the set of runs up to the finish
        var freezes = all.OfType<PlayerFrozen>().ToList();
        if (freezes.Count > 0)
        {
            Assert.IsType<PlayerFrozen>(all[^1]);
        }

        Assert.True(before.Status is SeasonStatus.Active or SeasonStatus.Closing, $"Approved while {before.Status}.");
        var run = before.Runs[approve.RunId];
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.True(run.Proof?.Status is null or ProofStatus.Pending, "A checked run was approved again.");
        var withoutProof = run.Proof is null;
        Assert.True(!withoutProof || !string.IsNullOrWhiteSpace(approve.Comment), "Approved without a proof and without a comment.");
        Assert.True(approve.Difficulty is null || approve.Difficulty <= run.Difficulty, "The proof raised the difficulty.");

        Assert.Equal(new ProofApproved(run.RunId, run.PlayerId, withoutProof, approve.Comment, s.Clock.UtcNow), events[^1]);
        Assert.Single(events.OfType<ProofApproved>());
        if (approve.Difficulty is { } difficulty && difficulty < run.Difficulty)
        {
            // D-98 (3): a lower difficulty needs the admin's comment, and the change carries it (no default text)
            Assert.False(string.IsNullOrWhiteSpace(approve.Comment), "Approved at a lower difficulty without a comment.");
            var changed = Assert.IsType<RunDifficultyChanged>(all[0]);
            Assert.Equal(approve.Comment, changed.Comment);
            CheckAcceptedDifficultyChange(
                s, new ChangeRunDifficulty(run.RunId, difficulty, changed.Comment), before, [.. all.Where(e => e is not ProofApproved)]);
        }
        else
        {
            // Nothing but the approval and the owner's freeze
            Assert.Single(events);
            Assert.Equal(freezes.Count, all.Count - 1);
            Assert.All(freezes, f => Assert.Equal(owner, f.PlayerId));
            Assert.Equal(
                before.Players.Values,
                s.State.Players.Values.Select(p => freezes.Any(f => f.PlayerId == p.PlayerId) ? p with { Finish = p.Finish! with { Frozen = false } } : p));
            Assert.Equal(before.ManualEffects, s.State.ManualEffects);
        }

        Assert.Equal(
            run.Proof is { } sent
                ? sent with { Status = ProofStatus.Approved, Comment = approve.Comment }
                : new ProofState(ProofStatus.Approved, [], null, null, null, approve.Comment),
            s.State.Runs[run.RunId].Proof);
        Assert.Equal(RunStatus.Completed, s.State.Runs[run.RunId].Status);
    }

    /// <summary>
    /// W5 / D-15 / D-98 / Q-3: only a completed unchecked run, with a comment; minus everything the run gave — points and
    /// coins by the sum of its logged changes; the position: a player who has not finished goes back by the cells the run
    /// moved, a finisher by the Q-3 surplus rule for a run up to the finish and not at all for a later one; its pending
    /// difficulty event «не применимо»; the run is rejected; the owner's turn untouched, the others only recalculated.
    /// </summary>
    private static void CheckAcceptedReject(Scenario s, RejectProof reject, SeasonState before)
    {
        var all = s.Last.Events;
        var events = all.Where(e => !IsFinishEvent(e)).ToList();
        Assert.True(before.Status is SeasonStatus.Active or SeasonStatus.Closing, $"Rejected while {before.Status}.");
        Assert.False(string.IsNullOrWhiteSpace(reject.Comment), "A reject without a comment.");
        var run = before.Runs[reject.RunId];
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.True(run.Proof?.Status is null or ProofStatus.Pending, "A checked run was rejected.");
        var player = run.PlayerId;
        var was = before.Players[player];
        var upToFinish = was.Finish is not null && IsUpToFinish(before, run);
        var frozen = was.Finish?.Frozen == true;

        // D-99 / Q-3: a frozen first loses nothing by a reject of his runs after the finish. His runs up to the finish are
        // approved before the freeze, unless approval is not required — whether such a reject takes the run's points and
        // coins back is open, so both are accepted.
        var keepsAll = frozen && !upToFinish;
        var open = frozen && upToFinish;

        Assert.Equal(new ProofRejected(run.RunId, player, reject.Comment, s.Clock.UtcNow), all[0]);
        var order = new[] { typeof(ProofRejected), typeof(PointsChanged), typeof(PlayerMoved), typeof(CoinsChanged), typeof(ManualEffectResolved) };
        // A finisher's move back comes with the revoke (Q-3), after the reject itself; its place among the rest is open
        var positions = events.Where(e => was.Finish is null || e is not PlayerMoved).Select(e => Array.IndexOf(order, e.GetType())).ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);

        var earlier = s.EffectiveLog.Take(s.EffectiveLog.Count - all.Count).ToList();
        var points = keepsAll || (open && !events.OfType<PointsChanged>().Any())
            ? 0
            : earlier.OfType<PointsChanged>().Where(e => e.RunId == run.RunId && e.Reason is not (PointsReason.FinishBonus or PointsReason.FinishBonusRevoked)).Sum(e => e.Delta);
        Assert.Equal(
            points == 0 ? [] : [new PointsChanged(player, -points, PointsReason.ProofRejected, run.RunId)],
            events.OfType<PointsChanged>());
        var coins = keepsAll || (open && !events.OfType<CoinsChanged>().Any()) ? 0 : earlier.OfType<CoinsChanged>().Where(e => e.RunId == run.RunId).Sum(e => e.Delta);
        Assert.Equal(
            coins == 0 ? [] : [new CoinsChanged(player, -coins, CoinsReason.ProofRejected, run.RunId)],
            events.OfType<CoinsChanged>());

        // Only the owner's finish may be revoked
        Assert.All(all.OfType<PlayerFinishRevoked>(), e => Assert.Equal(player, e.PlayerId));
        Assert.All(all.OfType<FinishSurplusChanged>(), e => Assert.Equal(player, e.PlayerId));
        if (was.Finish is null)
        {
            // Back by the cells the run really moved (sign × path of its logged moves, not the steps rolled: steps burned
            // at the finish gave no cells, D-47, D-98), from the current cell along the walked path
            var cells = MovedCells(earlier, run.RunId);
            Assert.True(cells >= 0, $"The run moved {cells} cells.");
            var path = cells > 0 ? Movement.Backward(before.Map, was.Path, cells) : [];
            Assert.Equal(
                path.Count == 0 ? [] : [new PlayerMoved(player, was.CellId, path[^1], -cells, [.. path], MoveReason.ProofRejected, run.RunId)],
                events.OfType<PlayerMoved>());
            Assert.DoesNotContain(all, e => e is PlayerFinishRevoked or FinishSurplusChanged);
        }
        else if (open && !all.OfType<PlayerFinishRevoked>().Any())
        {
            // Without required approval the frozen first's run up to the finish can be rejected; whether his place stays
            // final then is open (D-99 decides it for corrections only): kept — nothing moves
            Assert.DoesNotContain(all, e => e is PlayerMoved or FinishSurplusChanged);
        }
        else if (upToFinish)
        {
            // Q-3: the run's steps come off the position: within the surplus the finish stands, beyond it it goes
            // The finishing run takes back its dice (its cells plus the steps it burned); an earlier run only the cells it
            // really moved (D-99)
            var taken = was.Finish!.RunId == run.RunId ? run.Dice.Concat(run.ChallengeDice).Sum(d => d.Value) : run.Moved;
            CheckFinishPosition(all, before, was, run.RunId, -taken, MoveReason.ProofRejected);
        }
        else
        {
            Assert.DoesNotContain(all, e => e is PlayerMoved or PlayerFinishRevoked or FinishSurplusChanged);
        }

        var pending = before.ManualEffects.Values.Where(e => e.RunId == run.RunId && e.Source == ManualEffectSource.Difficulty).ToList();
        Assert.Equal(
            pending.Select(e => (e.EffectId, player, (Guid?)run.RunId, ManualEffectOutcome.NotApplicable)),
            events.OfType<ManualEffectResolved>().Select(e => (e.EffectId, e.PlayerId, e.RunId, e.Outcome)));

        var after = s.State.Runs[run.RunId];
        Assert.Equal(RunStatus.Rejected, after.Status);
        Assert.Equal(
            run.Proof is { } sent
                ? sent with { Status = ProofStatus.Rejected, Comment = reject.Comment }
                : new ProofState(ProofStatus.Rejected, [], null, null, null, reject.Comment),
            after.Proof);
        Assert.Equal((run.Dice, run.ChallengeDice, run.Hours, run.Difficulty), (after.Dice, after.ChallengeDice, after.Hours, after.Difficulty));

        var now = s.State.Players[player];
        var ownBonus = all.OfType<PointsChanged>()
            .Where(e => e.PlayerId == player && e.Reason is PointsReason.FinishBonus or PointsReason.FinishBonusRevoked)
            .Sum(e => e.Delta);
        Assert.Equal(was.Points - points + ownBonus, now.Points);
        Assert.Equal(was.Coins - coins, now.Coins);
        CheckOwnTurnAndOthers(s, before, player);

        // Nothing else changes in the other runs — but a revoke turns the owner's runs completed after the finish into
        // ordinary ones (D-99: they did not move the token; C13 long run, D-111)
        var revoked = all.OfType<PlayerFinishRevoked>().Any();
        Assert.All(s.State.Runs.Values.Where(r => r.RunId != run.RunId), r =>
        {
            var was = before.Runs[r.RunId];
            Assert.Equal(revoked && r.PlayerId == player ? was with { AfterFinish = false } : was, r);
        });
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
        Assert.False(was.Finish?.Frozen == true, "A frozen first rerolls for free (D-99), never short of coins.");
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
        var was = before.Players[playerId];

        // RR7 / D-09 / D-99: a frozen first pays nothing (the dice may be left unrolled); a finisher pays in points only
        var frozen = was.Finish?.Frozen == true;
        Assert.True(dice.Count == rules.PenaltyDice.Count || (frozen && dice.Count == 0), $"{dice.Count} penalty dice.");
        Assert.All(dice, d =>
        {
            Assert.Equal(rules.PenaltyDice.Sides, d.Sides);
            Assert.InRange(d.Value, 1, d.Sides);
        });
        var sum = dice.Sum(d => d.Value);

        var points = penalty.OfType<PointsChanged>().ToList();
        Assert.Equal(
            rules.AffectsPoints && sum > 0 && !frozen ? [new PointsChanged(playerId, -sum, PointsReason.DropPenalty, runId)] : [],
            points);

        var moves = penalty.OfType<PlayerMoved>().ToList();
        if (!rules.AffectsPosition || was.CellId == before.Map.Start.Id || sum == 0 || was.Finish is not null)
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
        if (rules.MandatoryEvent == MandatoryEvent.Bad && !frozen)
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
            ProofSubmitted x => x.PlayerId,
            ProofApproved x => x.PlayerId,
            ProofRejected x => x.PlayerId,
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
        var pendingEffects = new Dictionary<Guid, (Guid Player, Guid? Run)>();
        var proofs = new Dictionary<Guid, ProofStatus>();
        var completedGames = new HashSet<Guid>();
        var cellsByRun = new Dictionary<Guid, int>();

        // D-16: runs the first finisher completes do not count for the race — their games stay available
        var finishOrders = new Dictionary<Guid, int>();
        var outOfRace = new HashSet<Guid>();

        // A finish revoked by a reject: the move back that follows in the same command goes by the Q-3 surplus rule — it
        // also takes back an earlier run's reduction the surplus had absorbed (C13 long run), not only this run's cells
        var revokedByThisReject = new HashSet<Guid>();

        // D-16 / D-99: a game the first completed in free mode is completed for him only (until a reject), so his own roll
        // meets it as «уже прошёл» (C13 long run)
        var ownFree = new Dictionary<Guid, HashSet<Guid>>();
        HashSet<Guid> OwnFree(Guid player) => ownFree.TryGetValue(player, out var set) ? set : ownFree[player] = [];
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

                    // G7 / D-15: «уже прошёл» only for a game completed and not rejected; such a game is never offered
                    Assert.All(
                        rolled.Misses.Where(m => m.Reason == RollMissReason.CompletedInSeason),
                        m => Assert.True(completedGames.Contains(m.GameId) || OwnFree(rolled.PlayerId).Contains(m.GameId), $"«Уже прошёл» for {m.GameId}, not completed."));
                    Assert.DoesNotContain(rolled.GameId, completedGames);
                    Assert.DoesNotContain(rolled.GameId, OwnFree(rolled.PlayerId));
                    break;
                case GameChoiceRolled choiceRolled:
                    Assert.Equal(SeasonStatus.Active, status);
                    Assert.DoesNotContain(choiceRolled.Offers, o => players[choiceRolled.PlayerId].Exclusions.ContainsKey(o.GameId));
                    Assert.DoesNotContain(choiceRolled.Misses, m => players[choiceRolled.PlayerId].Exclusions.ContainsKey(m.GameId));
                    Assert.All(
                        choiceRolled.Misses.Where(m => m.Reason == RollMissReason.CompletedInSeason),
                        m => Assert.True(completedGames.Contains(m.GameId) || OwnFree(choiceRolled.PlayerId).Contains(m.GameId), $"«Уже прошёл» for {m.GameId}, not completed."));
                    Assert.DoesNotContain(choiceRolled.Offers, o => completedGames.Contains(o.GameId) || OwnFree(choiceRolled.PlayerId).Contains(o.GameId));
                    break;
                case RunStarted started:
                    Assert.Equal(SeasonStatus.Active, status);
                    Assert.DoesNotContain(started.GameId, players[started.PlayerId].Exclusions.Keys);
                    runs[started.RunId] = (started.PlayerId, started.GameId);
                    break;
                case RunCompleted completedRun:
                    Assert.Equal(SeasonStatus.Active, status);
                    var byFirst = finishOrders.Count > 0 && finishOrders.MinBy(x => x.Value).Key == completedRun.PlayerId;
                    if (byFirst)
                    {
                        outOfRace.Add(completedRun.RunId);
                        OwnFree(completedRun.PlayerId).Add(runs[completedRun.RunId].Game);
                    }
                    else
                    {
                        Assert.True(completedGames.Add(runs[completedRun.RunId].Game), "A game completed twice in the season.");
                    }

                    break;
                case PlayerFinished finishedPlayer:
                    finishOrders[finishedPlayer.PlayerId] = finishedPlayer.Order;
                    break;
                case PlayerFinishRevoked revokedPlayer:
                    finishOrders.Remove(revokedPlayer.PlayerId);
                    revokedByThisReject.Add(revokedPlayer.PlayerId);
                    break;
                case ProofSubmitted submitted:
                    // D-98: a proof of one's own run, until the season is finished, replacing only an unchecked one
                    Assert.True(status is SeasonStatus.Active or SeasonStatus.Closing, $"A proof while {status}.");
                    Assert.Equal(runs[submitted.RunId].Player, submitted.PlayerId);
                    Assert.True(!proofs.TryGetValue(submitted.RunId, out var shown) || shown == ProofStatus.Pending, "A checked proof was replaced.");
                    proofs[submitted.RunId] = ProofStatus.Pending;
                    break;
                case ProofApproved approved:
                    // Pending → approved (with a proof), none → approved (without one); never twice
                    Assert.True(status is SeasonStatus.Active or SeasonStatus.Closing, $"Approved while {status}.");
                    Assert.Equal(runs[approved.RunId].Player, approved.PlayerId);
                    var hadProof = proofs.TryGetValue(approved.RunId, out var approvedFrom);
                    Assert.True(!hadProof || approvedFrom == ProofStatus.Pending, $"Approved a {approvedFrom} proof.");
                    Assert.Equal(!hadProof, approved.WithoutProof);
                    Assert.True(hadProof || !string.IsNullOrWhiteSpace(approved.Comment), "Approved without a proof and without a comment.");
                    proofs[approved.RunId] = ProofStatus.Approved;
                    break;
                case ProofRejected rejectedProof:
                    revokedByThisReject.Clear();
                    // Pending or none → rejected; the game is not completed in the season any more (D-15)
                    Assert.True(status is SeasonStatus.Active or SeasonStatus.Closing, $"Rejected while {status}.");
                    Assert.Equal(runs[rejectedProof.RunId].Player, rejectedProof.PlayerId);
                    Assert.True(
                        !proofs.TryGetValue(rejectedProof.RunId, out var rejectedFrom) || rejectedFrom == ProofStatus.Pending,
                        $"Rejected a {rejectedFrom} proof.");
                    Assert.False(string.IsNullOrWhiteSpace(rejectedProof.Comment), "A reject without a comment.");
                    proofs[rejectedProof.RunId] = ProofStatus.Rejected;
                    Assert.True(
                        (outOfRace.Contains(rejectedProof.RunId) && OwnFree(rejectedProof.PlayerId).Remove(runs[rejectedProof.RunId].Game)) || completedGames.Remove(runs[rejectedProof.RunId].Game),
                        "A rejected game was not completed.");
                    break;
                case GameRerolled or ChoiceMade or CompletionRolled or RunDropped or RunTechRerolled:
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
                    Assert.True(pendingEffects.TryAdd(created.EffectId, (created.PlayerId, created.RunId)), "An effect created twice.");
                    break;
                case ManualEffectResolved resolvedEffect:
                    // D-97, D-102: only a pending effect is resolved, once, while the season runs or closes; «не применимо»
                    // always with a comment (only the owner's «применено» may go without one — checked by command below)
                    Assert.True(pendingEffects.Remove(resolvedEffect.EffectId, out var effectOwner), "A resolved effect was not pending.");
                    Assert.Equal(effectOwner, (resolvedEffect.PlayerId, resolvedEffect.RunId));
                    Assert.True(status is SeasonStatus.Active or SeasonStatus.Closing, $"Resolved while {status}.");
                    Assert.Equal(resolvedEffect.Comment.Trim(), resolvedEffect.Comment);
                    Assert.True(resolvedEffect.Comment.Length <= Limits.MaxCommentLength, "A resolution comment over the limit.");
                    Assert.True(
                        resolvedEffect.Outcome == ManualEffectOutcome.Applied || resolvedEffect.Comment.Length > 0,
                        "«Не применимо» without a comment.");
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
                    Assert.True(points.Reason != PointsReason.ProofRejected || points.Delta < 0, "A reject gave points.");
                    players[points.PlayerId].Points += points.Delta;
                    break;
                case CoinsChanged coins:
                    // RR2: no coins from drops (coins have no drop reason at all)
                    Assert.NotEqual(0, coins.Delta);
                    players[coins.PlayerId].Coins += coins.Delta;

                    Assert.True(coins.Reason != CoinsReason.ProofRejected || coins.Delta < 0, "A reject gave coins.");

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

                    if (moved.Reason == MoveReason.ProofRejected)
                    {
                        // D-98: a reject only moves back, never past the start
                        Assert.True(moved.Steps < 0, "A reject moved forward.");
                        Assert.InRange(moved.Path.Count, 1, -moved.Steps);

                        // D-98: back by the cells the run really moved, not by its steps rolled — for a player who has
                        // not finished; a finisher's revoke goes back by the Q-3 surplus rule (checked per command)
                        Assert.True(
                            finishOrders.ContainsKey(moved.PlayerId) || revokedByThisReject.Contains(moved.PlayerId)
                                || cellsByRun.GetValueOrDefault(moved.RunId!.Value) == -moved.Steps,
                            $"A reject moved {moved.Steps} for a run of {cellsByRun.GetValueOrDefault(moved.RunId!.Value)} cells.");
                    }

                    if (moved.RunId is { } movedRun)
                    {
                        cellsByRun[movedRun] = cellsByRun.GetValueOrDefault(movedRun) + (Math.Sign(moved.Steps) * moved.Path.Count);
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

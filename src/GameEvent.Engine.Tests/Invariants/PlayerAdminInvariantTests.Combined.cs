using FsCheck.Xunit;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
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
/// C13: the combined game — players, the admin, the clock, deadlines, proofs with approvals and rejects, undos of whole
/// commands (D-104) and changes of the rules (D-82) in one random season, with every stage-1 invariant (1–14, 19) checked
/// after every command. The folds of the other variants read <see cref="Scenario.EffectiveLog"/>: an undone command is as if
/// it had never been (invariant 13), only its numbers — finish orders, points ticks, ruleset versions — are never reused.
/// In the script a roll with an argument of 5 and up is instead: 5 an undo, 6 a change of the rules, 7 a manual effect
/// resolved or the inactive flag (the reroll and correction variants take the kinds these come from elsewhere).
/// </summary>
public partial class PlayerAdminInvariantTests
{
    [Property(MaxTest = 200)]
    public void Invariants_hold_in_the_combined_game(int seed, byte[] script) =>
        Play(seed, script, CheckCombinedInvariants, withDrops: true, withCorrections: true, withProofs: true, finishes: true, lifecycle: true, withUndo: true, withRulesetChanges: true);

    [Property(MaxTest = 200)]
    public void Invariants_hold_in_the_combined_game_with_rerolls_and_a_choice_of_games(int seed, byte[] script) =>
        Play(seed, script, CheckCombinedInvariants, withChoice: true, rerolls: RerollMode.BadEvent, withDrops: true, withCorrections: true, withProofs: true, finishes: true, lifecycle: true, withUndo: true, withRulesetChanges: true);

    [Property(MaxTest = 50)]
    public void Same_seed_and_commands_give_the_same_log_in_the_combined_game(int seed, byte[] script)
    {
        // Invariant 14 with undos and rule changes in the mix
        var first = Play(seed, script, withChoice: true, rerolls: RerollMode.Coins, withDrops: true, withCorrections: true, withProofs: true, finishes: true, lifecycle: true, withUndo: true, withRulesetChanges: true);
        var second = Play(seed, script, withChoice: true, rerolls: RerollMode.Coins, withDrops: true, withCorrections: true, withProofs: true, finishes: true, lifecycle: true, withUndo: true, withRulesetChanges: true);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Fact]
    public void Combined_variant_reaches_undos_rule_changes_and_disabled_mechanics()
    {
        // The generator must exercise what it adds, not only allow it: over fixed scripts undos are accepted (also of a
        // command that moved a finish and of a rule change) and refused for dependents, rules change (challenges both
        // ways), a mechanic this build does not have is refused, and a challenge is claimed while challenges are off
        var undone = 0;
        var undoneFinish = false;
        var undoneRules = false;
        var dependents = 0;
        var changed = 0;
        var challengesOn = false;
        var challengesOff = false;
        var unsupported = 0;
        var claimRefused = false;
        for (var seed = 0; seed < 200; seed++)
        {
            var x = (uint)seed + 13;
            var script = Enumerable.Range(0, 400).Select(_ => (byte)((x = (x * 1103515245) + 12345) >> 16)).ToArray();
            Play(seed, script, (s, command, before, _) =>
            {
                if (command is UndoCommand undo && s.Last.IsAccepted)
                {
                    undone++;
                    var target = s.History.Single(c => c.CommandId == undo.TargetCommandId);
                    undoneFinish |= target.Events.Any(IsFinishEvent);
                    undoneRules |= target.Events.Any(e => e is RulesetChanged);
                }

                dependents += command is UndoCommand && s.Last.Rejection?.Code == RejectionCodes.UndoDependents ? 1 : 0;
                if (command is ChangeRuleset && s.Last.IsAccepted)
                {
                    changed++;
                    challengesOn |= !before.Rules.Features.Challenges && s.State.Rules.Features.Challenges;
                    challengesOff |= before.Rules.Features.Challenges && !s.State.Rules.Features.Challenges;
                }

                unsupported += command is ChangeRuleset && s.Last.Rejection?.Code == RejectionCodes.RulesetInvalid ? 1 : 0;
                claimRefused |= command is CompleteRun { ChallengeDone: true } && !before.Rules.Features.Challenges
                    && before.Players.TryGetValue(((CompleteRun)command).PlayerId, out var p) && p.Phase == TurnPhase.Playing
                    && !s.Last.IsAccepted;
            }, withDrops: true, withCorrections: true, withProofs: true, finishes: true, lifecycle: true, withUndo: true, withRulesetChanges: true);
        }

        Assert.True(undone > 20, $"Only {undone} undos accepted.");
        Assert.True(undoneFinish, "No command that moved a finish (a finish, a freeze, a bonus, a revoke) was undone.");
        Assert.True(undoneRules, "No rule change was undone.");
        Assert.True(dependents > 20, $"Only {dependents} undos refused for dependents.");
        Assert.True(changed > 20, $"Only {changed} rule changes accepted.");
        Assert.True(challengesOn && challengesOff, "Challenges were not switched both ways.");
        Assert.True(unsupported > 5, $"Only {unsupported} unsupported rulesets refused.");
        Assert.True(claimRefused, "No challenge claim met disabled challenges.");
    }

    private static readonly (string Name, Func<Ruleset, Ruleset> Change)[] s_ruleChanges =
    [
        // Numbers that go into a snapshot at the roll (S1, S2): runs already rolled keep theirs
        ("hours per die", r => r with { Reward = r.Reward with { DiceCount = r.Reward.DiceCount with { HoursPerDie = r.Reward.DiceCount.HoursPerDie == 3 ? 2 : 3 } } }),
        ("normal die", r => r with { Reward = r.Reward with { DieByDifficulty = r.Reward.DieByDifficulty with { Normal = new DieRule { Sides = r.Reward.DieByDifficulty.Normal.Sides == 4 ? 6 : 4 } } } }),
        ("coins per hour", r => r with { Reward = r.Reward with { Coins = r.Reward.Coins with { PerHour = r.Reward.Coins.PerHour == 1 ? 2 : 1 } } }),
        ("challenge dice", r => r with { Reward = r.Reward with { ChallengeBonus = new ChallengeBonus { ExtraDice = r.Reward.ChallengeBonus.ExtraDice == 1 ? 2 : 1 } } }),
        ("tech reroll window", r => r with { Roll = r.Roll with { TechRerollWindowHours = r.Roll.TechRerollWindowHours == 48 ? 6 : 48 } }),

        // Rules read when a command runs
        ("free rerolls", r => r with { Roll = r.Roll with { FreeRerollsPerRoll = (r.Roll.FreeRerollsPerRoll + 1) % 3 } }),
        ("drop penalty", r => r with { Drop = r.Drop with { PenaltyDice = r.Drop.PenaltyDice with { Count = r.Drop.PenaltyDice.Count == 2 ? 1 : 2 }, AffectsPosition = !r.Drop.AffectsPosition } }),
        ("drop event", r => r with { Drop = r.Drop with { MandatoryEvent = r.Drop.MandatoryEvent == MandatoryEvent.Bad ? MandatoryEvent.None : MandatoryEvent.Bad } }),
        ("tiebreakers", r => r with { Ranking = new RankingRules { Tiebreakers = [.. r.Ranking.Tiebreakers.Reverse()] } }),

        // 19: a mechanic switched on and off; one this build does not have is refused, as are no change and a bad value
        ("challenges", r => r with { Features = r.Features with { Challenges = !r.Features.Challenges } }),
        ("challenges", r => r with { Features = r.Features with { Challenges = !r.Features.Challenges } }),
        ("shop", r => r with { Features = r.Features with { Shop = true } }),
        ("items and events", r => r with { Features = r.Features with { Items = true, Events = true } }),
        ("bets", r => r with { Features = r.Features with { Bets = true } }),
        ("graph map", r => r with { Features = r.Features with { MapMode = MapMode.Graph } }),
        ("unchanged", r => r),
        ("zero hours per die", r => r with { Reward = r.Reward with { DiceCount = r.Reward.DiceCount with { HoursPerDie = 0 } } }),
    ];

    /// <summary>
    /// The combined layer on top of the other variants: a roll with an argument of 5 and up is an undo (5), a change of the
    /// rules (6), or (7) a pending manual effect resolved by its owner or the admin, else the inactive flag.
    /// </summary>
    private static ICommand CombinedCommandFor(Scenario s, byte b, bool withUndo, bool withRulesetChanges, Func<ICommand> otherwise)
    {
        var arg = b / 32;
        if ((b / 4) % 8 != 0 || arg < 5)
        {
            return otherwise();
        }

        var index = b % 4;
        var player = index < s_players.Length ? s.PlayerId(s_players[index]) : s_late;
        var variant = s.Log.Count;
        switch (arg)
        {
            case 5 when withUndo:
                return UndoFor(s, b);
            case 6 when withRulesetChanges:
                var (_, change) = s_ruleChanges[(index + variant) % s_ruleChanges.Length];
                return new ChangeRuleset(change(s.Ruleset), variant % 9 == 0 ? s.State.RulesetVersion - 1 : null);
            case 7 when s.State.ManualEffects.Count > 0 && variant % 2 == 0:
                var effects = s.State.ManualEffects.Values.ToList();
                var effect = effects[variant % effects.Count];
                return new ResolveManualEffect(
                    effect.EffectId, variant % 3 == 0 ? ManualEffectOutcome.NotApplicable : ManualEffectOutcome.Applied, "разыграли", variant % 4 == 0 ? null : effect.PlayerId);
            case 7:
                return new SetPlayerInactive(player, variant % 2 == 1);
            default:
                return otherwise();
        }
    }

    /// <summary>
    /// An undo of the last command (most often) or one of the two before it, of any command of the season, of a made-up id, or with a blank
    /// comment — the refusals included.
    /// </summary>
    private static UndoCommand UndoFor(Scenario s, byte b)
    {
        var history = s.History;
        var variant = s.Log.Count;
        return (((b % 4) + variant) % 8) switch
        {
            7 => new UndoCommand(SequentialIds.Make(0x7F000000, b), "ошибка админа"),
            6 => new UndoCommand(history[variant % history.Count].CommandId, variant % 3 == 0 ? " " : "ошибка админа"),
            var back => new UndoCommand(history[history.Count - 1 - Math.Min(Math.Max(0, back - 3), history.Count - 1)].CommandId, "ошибка админа"),
        };
    }

    /// <summary>Every invariant of the finish variant plus the undo (13), the rule versions (S1, S2, C3) and invariant 19.</summary>
    private static void CheckCombinedInvariants(Scenario s, ICommand command, SeasonState before, int logLengthBefore)
    {
        CheckFinishInvariants(s, command, before, logLengthBefore);
        CheckUndo(s, command, before);
        CheckRulesetVersions(s, before);
        CheckMechanics(s, command, before);
    }

    // The season as it matters to the game: the counters of finishes and points changes and the ruleset version only
    // grow, and the tick numbers in players follow them (D-104); both are checked by their own folds
    private static SeasonState Comparable(SeasonState state) =>
        state with
        {
            FinishesSoFar = 0,
            PointsChanges = 0,
            RulesetVersion = 0,
            Players = state.Players.SetItems(state.Players.Select(p => KeyValuePair.Create(p.Key, p.Value with { PointsTick = 0 }))),
        };

    /// <summary>
    /// 13 / D-104: an accepted undo names a command that counted and leaves the season exactly as the log without that
    /// command replays (up to the numbers that only grow); a refusal for dependents names only later commands that count.
    /// </summary>
    private static void CheckUndo(Scenario s, ICommand command, SeasonState before)
    {
        if (command is not UndoCommand undo)
        {
            return;
        }

        var history = s.History.ToList();
        if (!s.Last.IsAccepted)
        {
            if (s.Last.Rejection!.Code == RejectionCodes.UndoDependents)
            {
                var target = history.FindIndex(c => c.CommandId == undo.TargetCommandId);
                var undoneNow = s.UndoneCommands();
                Assert.NotEmpty(s.Last.Rejection.Related);
                Assert.All(s.Last.Rejection.Related, id =>
                {
                    var at = history.FindIndex(c => c.CommandId == id);
                    Assert.True(at > target && Scenario.IsEffective(history[at], undoneNow), $"{id} is not a later command that counts.");
                });
            }

            return;
        }

        var undone = Assert.IsType<CommandUndone>(Assert.Single(s.Last.Events));
        Assert.Equal((undo.TargetCommandId, undo.Comment.Trim()), (undone.CommandId, undone.Comment));
        var earlier = s.UndoneCommands();
        earlier.Remove(undo.TargetCommandId);
        var logged = history.Single(c => c.CommandId == undo.TargetCommandId);
        Assert.True(Scenario.IsEffective(logged, earlier), "An undo of a command that did not count.");
        Assert.DoesNotContain(logged.Events, e => e is SeasonCreated);
        Assert.True(before.Status is SeasonStatus.Draft or SeasonStatus.Active or SeasonStatus.Closing, $"Undone while {before.Status}.");

        Assert.Equal(Comparable(SeasonEngine.Replay(s.EffectiveLog)), Comparable(s.State));
    }

    /// <summary>
    /// S1, S2, C3, D-104: versions are the season's rules in order — 1 at creation, +1 per change and per undo that brings
    /// other rules back; the rules in force are the last version; every offer, option and run plays by the rules of the
    /// version it was rolled under, and a run's snapshot never changes.
    /// </summary>
    private static void CheckRulesetVersions(Scenario s, SeasonState before)
    {
        var versions = new List<Ruleset>();
        foreach (var e in s.Log)
        {
            switch (e)
            {
                case SeasonCreated created:
                    versions.Add(created.Ruleset);
                    break;
                case RulesetChanged changed:
                    Assert.Equal(versions.Count + 1, changed.Version);
                    Assert.NotEqual(versions[^1], changed.Ruleset);
                    versions.Add(changed.Ruleset);
                    break;
                case CommandUndone { Season.Ruleset: { } restored } when restored != versions[^1]:
                    versions.Add(restored);
                    break;
            }
        }

        Assert.Equal(versions.Count, s.State.RulesetVersion);
        Assert.Equal(versions[^1], s.State.Rules);

        var snapshots = s.State.Players.Values
            .SelectMany(p => (p.Offer is { } offer ? [offer.Snapshot] : Array.Empty<RunSnapshot>())
                .Concat(p.Choice?.Options.Select(o => o.Game!.Snapshot) ?? []))
            .Concat(s.State.Runs.Values.Select(r => r.Snapshot));
        foreach (var snapshot in snapshots)
        {
            Assert.InRange(snapshot.RulesetVersion, 1, versions.Count);
            var rules = versions[snapshot.RulesetVersion - 1];
            Assert.Equal(
                (rules.Reward.DiceCount, rules.Reward.DieByDifficulty, rules.Roll.TechRerollWindowHours, rules.Reward.ChallengeBonus.ExtraDice, rules.Reward.Coins),
                (snapshot.DiceCount, snapshot.DieByDifficulty, snapshot.TechRerollWindowHours, snapshot.ChallengeExtraDice, snapshot.Coins));
        }

        foreach (var run in before.Runs.Values.Where(r => s.State.Runs.ContainsKey(r.RunId)))
        {
            Assert.Equal(run.Snapshot, s.State.Runs[run.RunId].Snapshot);
        }
    }

    /// <summary>
    /// 19 / D-22: a mechanic that is off leaves no event, and one this build does not have can never be switched on — the
    /// change is refused and the rules in force never hold it. Every event type is mapped to its mechanic here, so a new
    /// one must be placed before it can appear.
    /// </summary>
    private static void CheckMechanics(Scenario s, ICommand command, SeasonState before)
    {
        Assert.Empty(RulesetSupport.Unsupported(s.State.Rules));
        if (command is ChangeRuleset change && RulesetSupport.Unsupported(change.Ruleset).Count > 0)
        {
            // Refused as invalid — unless it was already refused for editing an older version
            Assert.False(s.Last.IsAccepted, "A mechanic this build does not have was switched on.");
            Assert.Equal(
                change.ExpectedVersion is { } edited && edited != before.RulesetVersion ? RejectionCodes.RulesetVersionConflict : RejectionCodes.RulesetInvalid,
                s.Last.Rejection!.Code);
        }

        // D-96 (1): a claim of the challenge while challenges are off is refused
        if (command is CompleteRun { ChallengeDone: true } && !before.Rules.Features.Challenges)
        {
            Assert.False(s.Last.IsAccepted, "A challenge was claimed while features.challenges is off.");
        }

        foreach (var e in s.Last.IsAccepted ? s.Last.Events : [])
        {
            var mechanic = MechanicOf(e);
            Assert.True(mechanic is null || IsOn(before.Rules.Features, mechanic), $"{e.GetType().Name} of «{mechanic}», which is off.");
        }
    }

    private static bool IsOn(Features features, string mechanic) =>
        mechanic switch
        {
            "challenges" => features.Challenges,
            "effects" => features.Items || features.Events,
            _ => throw new ArgumentOutOfRangeException(nameof(mechanic), mechanic, "Unknown mechanic."),
        };

    /// <summary>The feature flag an event belongs to; null for the core of stage 1 that is always on.</summary>
    private static string? MechanicOf(IGameEvent e) =>
        e switch
        {
            RunCompleted { ChallengeDone: true } or CompletionRolled { ChallengeDice.Count: > 0 } => "challenges",

            // The effect dispatcher reacts to content (items, events); stage 1 has no content effects in play (D-24, D-103)
            EffectChainCut => "effects",
            SeasonCreated or SeasonStatusChanged or SeasonDeadlineSet or SeasonResultRecorded or RulesetChanged
                or SeasonPlayerAdded or PlayerAdjusted or PlayerInactivitySet or OfferDiscarded or ChoiceDiscarded
                or GameRolled or GameChoiceRolled or GameRerolled or GameExcluded or ChoiceMade
                or RunStarted or RunCompleted or CompletionRolled or RunReviewed or RunDropped or RunTechRerolled
                or TechRerollConvertedToDrop or RunHoursCorrected or RunDifficultyChanged
                or ProofSubmitted or ProofApproved or ProofRejected
                or PointsChanged or CoinsChanged or ResourceChanged or PlayerMoved
                or PlayerFinished or PlayerFrozen or PlayerFinishRevoked or FinishSurplusChanged
                or ManualEffectCreated or ManualEffectResolved or CommandUndone

                // Accounts (D8, D-106) are the site's, not a season mechanic
                or Accounts.AccountCreated or Accounts.AccountPasswordReset or Accounts.AccountPasswordChanged
                or Accounts.AccountChanged or Accounts.AccountDeleted or Accounts.AccountRestored => null,
            _ => throw new Xunit.Sdk.XunitException($"{e.GetType().Name} is not mapped to a mechanic: add it to the core or to its feature flag."),
        };

    [Fact]
    public void Every_event_type_is_mapped_to_a_mechanic()
    {
        // 19: a new event type fails here until it is placed in the core or under its feature flag
        foreach (var type in EventCatalog.Types)
        {
            var sample = (IGameEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            _ = MechanicOf(sample);
        }
    }
}

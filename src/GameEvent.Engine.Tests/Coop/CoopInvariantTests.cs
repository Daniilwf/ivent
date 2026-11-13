using FsCheck.Xunit;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// Random games with coop (task 5.10): rolls, invitations and answers, leaving, completions, drops, tech rerolls, proofs,
/// hours corrections, undos, timers and the inactive flag among four players on a short map with coop games. After every
/// command: the log replays to the state (1), points and coins are the sum of their changes (2), one run per game and the
/// partner without a run of his own (4), and the coop shares add up to the throw and its coins (D-521) — at the split and
/// after every correction, per player.
/// </summary>
public class CoopInvariantTests
{
    private static readonly string[] s_players = ["Вася", "Петя", "Маша", "Коля"];

    [Property(MaxTest = 200)]
    public void Invariants_hold_in_games_with_coop(int seed, byte[] script) => Play(seed, script, Check);

    [Fact]
    public void The_coop_games_reach_splits_leaves_expiries_rejects_corrections_and_undos()
    {
        // The generator must exercise what it adds, not only allow it; a finish by a coop run is rare here: CoopFinishTests
        var (splits, left, expired, rejected, corrected, undone) = (0, 0, 0, 0, 0, 0);
        for (var seed = 0; seed < 60; seed++)
        {
            var x = (uint)seed + 7;
            var script = Enumerable.Range(0, 300).Select(_ => (byte)((x = (x * 1103515245) + 12345) >> 16)).ToArray();
            Play(seed, script, (s, command, before) =>
            {
                Check(s, command, before);
                if (!s.Last.IsAccepted)
                {
                    return;
                }

                splits += s.LastEvents<CoopRewardSplit>().Count();
                left += s.LastEvents<CoopLeft>().Count();
                expired += s.LastEvents<InteractionExpired>().Count();
                var coopRuns = before.Runs.Values.Where(r => r.Coop?.Shares is not null).Select(r => r.RunId).ToHashSet();
                rejected += s.LastEvents<ProofRejected>().Count(r => coopRuns.Contains(r.RunId));
                corrected += s.LastEvents<RunHoursCorrected>().Count(r => coopRuns.Contains(r.RunId));
                undone += command is UndoCommand && before.Runs.Values.Any(r => r.Coop is not null) ? 1 : 0;
            });
        }

        var counts = $"splits {splits}, left {left}, expired {expired}, rejected {rejected}, corrected {corrected}, undone {undone}";
        Assert.True(splits > 10, counts);
        Assert.True(left > 5, counts);
        Assert.True(expired > 5, $"Only {expired} invitations expired.");
        Assert.True(rejected > 0, "No coop run was rejected.");
        Assert.True(corrected > 0, "No coop run was corrected.");
        Assert.True(undone > 5, $"Only {undone} undos with a coop in the season.");
    }

    private static void Play(int seed, byte[] script, Action<Scenario, ICommand, SeasonState> afterEach)
    {
        // Most games of the pool are coop here, so invitations meet runs they can join
        CoopTestScenario.New(mapLength: 30, seed: seed)
            .MarkCoop(CoopTestScenario.SoloGame).MarkCoop(CoopTestScenario.OtherSoloGame).MarkCoop(CoopTestScenario.ThirdSoloGame)
            .Explained(game =>
        {
            var setup = game.History.Count;
            foreach (var b in script)
            {
                game.Advance(TimeSpan.FromHours(1 + (b % 3)));
                var before = game.State;
                var command = CommandFor(game, b, setup);
                game.Act(command);
                afterEach(game, command, before);
            }
        });
    }

    private static ICommand CommandFor(Scenario s, byte b, int setup)
    {
        var player = s.PlayerId(s_players[b % 4]);
        var other = s.PlayerId(s_players[(b % 4 + 1 + (b / 64 % 3)) % 4]);
        var state = s.State;
        var me = state.Players[player];
        var pendingTo = state.Interactions.Values.FirstOrDefault(i => i.Status == InteractionStatus.Pending && i.ToPlayerId == player);
        var pendingFrom = state.Interactions.Values.FirstOrDefault(i => i.Status == InteractionStatus.Pending && i.FromPlayerId == player);
        var unchecked_ = ProofReviewOrder.Order(state);
        var coopRun = state.Runs.Values.Where(r => r.Status == RunStatus.Completed && r.Coop?.Shares is not null).Select(r => (RunState?)r).LastOrDefault();
        return ((b / 4) % 16) switch
        {
            0 or 1 or 2 => me.Phase switch
            {
                TurnPhase.Idle => new RollGame(player),
                TurnPhase.Rolling => new StartRun(player),
                _ => new CompleteRun(player, Difficulty.Normal),
            },
            3 or 4 => new InviteToCoop(player, other),
            5 => pendingTo is null ? new InviteToCoop(player, other) : new AnswerInvitation(player, pendingTo.InteractionId, Accept: true),
            6 => pendingTo is null ? new InviteToCoop(player, other) : new AnswerInvitation(player, pendingTo.InteractionId, Accept: b % 4 != 0),
            7 => pendingFrom is null ? new LeaveCoop(player) : new WithdrawInvitation(player, pendingFrom.InteractionId),
            8 => new LeaveCoop(player),
            9 => new DropRun(player),
            10 => new TechReroll(player, TechRerollReason.WeakPc, Comment: null),
            11 => unchecked_.Count == 0 ? new FireTimers() : new ApproveProof(unchecked_[0]),
            12 => unchecked_.Count == 0 ? new FireTimers() : new RejectProof(unchecked_[^1], "Фейк"),
            13 => coopRun is null ? new FireTimers() : new CorrectRunHours(coopRun.RunId, coopRun.Hours == 9 ? 3 : 9, "Правка"),
            // The last command most often, else an earlier one, so undos meet their dependents
            14 => s.History.Count <= setup
                ? new FireTimers()
                : new UndoCommand(s.History[b % 3 == 0 ? setup + (b % (s.History.Count - setup)) : s.History.Count - 1].CommandId, "ошибка админа"),
            // Coop switched off and on again mid-game (D-420, Н-25)
            _ => (b % 8) switch
            {
                0 => new SetPlayerInactive(player, !me.IsInactive),
                1 => new Engine.Rulesets.ChangeRuleset(s.Ruleset with { Features = s.Ruleset.Features with { Coop = !s.Ruleset.Features.Coop } }),
                _ => new FireTimers(),
            },
        };
    }

    private static void Check(Scenario s, ICommand command, SeasonState before)
    {
        _ = command;
        _ = before;
        var state = s.State;

        // 1: the log replays to the state
        Assert.Equal(SeasonEngine.Replay(s.Log), state);

        // 2: points and coins are the sum of their changes in the commands in force
        var log = s.EffectiveLog;
        foreach (var player in state.Players.Values)
        {
            Assert.Equal(player.Points, log.OfType<PointsChanged>().Where(e => e.PlayerId == player.PlayerId).Sum(e => e.Delta));
            Assert.Equal(player.Coins, log.OfType<CoinsChanged>().Where(e => e.PlayerId == player.PlayerId).Sum(e => e.Delta));
        }

        // 4: one run per game at a time; a partner plays only the run he joined and has none of his own (D-520)
        var playing = state.Runs.Values.Where(r => r.Status == RunStatus.Playing).ToList();
        Assert.Equal(playing.Count, playing.Select(r => r.GameId).Distinct().Count());
        foreach (var player in state.Players.Values)
        {
            var mine = playing.Where(r => r.PlayerId == player.PlayerId || r.PartnerId == player.PlayerId).ToList();
            Assert.True(mine.Count <= 1, $"{player.Name} plays {mine.Count} runs.");
            Assert.Equal(mine.SingleOrDefault()?.RunId, player.ActiveRunId);
            Assert.Equal(mine.Count == 1, player.Phase == TurnPhase.Playing);
        }

        foreach (var run in playing.Where(r => r.Coop is not null))
        {
            Assert.NotEqual(run.PlayerId, run.PartnerId);
            Assert.True(s.PoolGame(run.GameId).IsCoop, "A partner joined a run of a game that is not coop.");
        }

        // The shares add up to the throw and its coins (D-521, EV46): at the split…
        if (s.Last.IsAccepted)
        {
            foreach (var split in s.LastEvents<CoopRewardSplit>())
            {
                Assert.Equal(split.Total, split.RollerPoints + split.PartnerPoints);
                Assert.Equal(split.Coins, split.RollerCoins + split.PartnerCoins);
                Assert.Equal((int)Math.Ceiling(split.Total * split.PointsShare), split.RollerPoints);
                Assert.Equal((int)Math.Ceiling(split.Coins * split.CoinsShare), split.RollerCoins);
                Assert.Equal(CompletionRoll.Total(s.State.Runs[split.RunId]), split.Total);
            }
        }

        // …and after every correction: what each was given for a completed coop run is his share of its total now
        foreach (var run in state.Runs.Values.Where(r => r.Status == RunStatus.Completed && r.Coop?.Shares is not null))
        {
            var coins = run.Snapshot.Coins is { } reward ? RunLifecycle.CompletionCoins(reward, run.Snapshot.DiceCount, run.Hours!.Value) : 0;
            foreach (var id in RunShares.Players(run).Where(id => state.Players[id].Finish?.Frozen != true))
            {
                Assert.Equal(
                    RunShares.Points(run, id),
                    log.OfType<PointsChanged>()
                        .Where(e => e.PlayerId == id && e.RunId == run.RunId && e.Reason is PointsReason.CompletionRoll or PointsReason.RunCorrection)
                        .Sum(e => e.Delta));
                Assert.Equal(
                    RunShares.Coins(run, id, coins),
                    log.OfType<CoinsChanged>()
                        .Where(e => e.PlayerId == id && e.RunId == run.RunId && e.Reason is CoinsReason.CompletionReward or CoinsReason.RunCorrection)
                        .Sum(e => e.Delta));
            }

            Assert.Equal(CompletionRoll.Total(run), RunShares.Players(run).Sum(id => RunShares.Points(run, id)));
        }
    }
}

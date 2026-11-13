using FsCheck.Xunit;
using GameEvent.Engine.Duels;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Duels;

/// <summary>
/// Duels in random games (task 5.12): challenges, answers, withdrawals, rolls, starts, completions, drops, tech rerolls,
/// rejects, approvals, the timer, a player marked inactive and back, the clock and undos. After every accepted command:
/// <list type="bullet">
/// <item>two players have at most one open duel (a challenge waiting or a race);</item>
/// <item>a player's open duels (challenges he sent and waiting, and races) are within <c>duels.maxOpenPerPlayer</c>, his
/// invitations sent and waiting within <c>interactions.maxOpenInvitesPerPlayer</c>;</item>
/// <item>every duel was started by an accepted invitation of kind duel under its id;</item>
/// <item>the first finisher and a player marked inactive have no open duel;</item>
/// <item>a won duel names one of its two players; a win by completion stands on a completed run of the winner, rolled
/// after the accept, not one of the runs at acceptance, completed before the race's end;</item>
/// <item>no player has more paying wins than <c>duels.maxWinsPerSeason</c>;</item>
/// <item>the duel coins of a player in the effective log equal the coins of the duels he holds as won, plus what a
/// revocation left the frozen first finisher (EV41: nothing else moves coins for a duel, the loser's never change);</item>
/// <item>replaying the log gives the same state.</item>
/// </list>
/// </summary>
public class DuelInvariantTests
{
    private static readonly string[] s_players = ["Вася", "Петя", "Маша", "Коля"];

    [Property(MaxTest = 150)]
    public void Duel_invariants_hold_in_random_games(int seed, byte[] script) =>
        Play(seed, script ?? [], Check);

    [Property(MaxTest = 30)]
    public void Same_seed_and_commands_give_the_same_log(int seed, byte[] script)
    {
        var first = Play(seed, script ?? [], _ => { });
        var second = Play(seed, script ?? [], _ => { });

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Fact]
    public void Random_games_reach_wins_forfeits_draws_and_revocations()
    {
        var (won, forfeited, drawn, revoked, expired) = (0, 0, 0, 0, 0);
        for (var seed = 0; seed < 40; seed++)
        {
            var x = (uint)seed + 11;
            var script = Enumerable.Range(0, 250).Select(_ => (byte)((x = (x * 1103515245) + 12345) >> 16)).ToArray();
            Play(seed, script, s =>
            {
                won += s.LastEvents<DuelSettled>().Count(d => d.End == DuelEnd.Completed);
                forfeited += s.LastEvents<DuelSettled>().Count(d => d.End == DuelEnd.Forfeit);
                drawn += s.LastEvents<DuelSettled>().Count(d => d.Status == DuelStatus.Drawn);
                revoked += s.LastEvents<DuelWinRevoked>().Count();
                expired += s.LastEvents<InteractionExpired>().Count();
            });
        }

        Assert.True(
            won > 0 && forfeited > 0 && drawn > 0 && revoked > 0 && expired > 0,
            $"won {won}, forfeited {forfeited}, drawn {drawn}, revoked {revoked}, expired {expired}");
    }

    private static Scenario Play(int seed, byte[] script, Action<Scenario> check)
    {
        var s = DuelScenario.New(d => d with { MaxOpenPerPlayer = 2, MaxWinsPerSeason = 3 }, mapLength: 30, seed: seed);
        check(s);

        // Only the game is undone, not the setup that added the players
        var setup = s.History.Count;
        var random = new System.Random(seed);
        foreach (var step in script)
        {
            var player = s_players[random.Next(s_players.Length)];
            var other = s_players[random.Next(s_players.Length)];
            var id = s.PlayerId(player);
            var waiting = s.State.Interactions.Values.Where(i => i.Status == InteractionStatus.Pending).ToList();
            var challenge = waiting.Count == 0 ? null : waiting[random.Next(waiting.Count)];
            var runs = s.State.Runs.Values.Where(r => r.Status is RunStatus.Completed or RunStatus.Rejected).ToList();
            var run = runs.Count == 0 ? null : runs[random.Next(runs.Count)];
            switch (step % 16)
            {
                case 0:
                case 1:
                    s.Act(new ChallengeToDuel(id, s.PlayerId(other)));
                    break;
                case 2 when challenge is not null:
                case 3 when challenge is not null:
                    s.Act(new AnswerInvitation(challenge.ToPlayerId, challenge.InteractionId, step % 5 != 0));
                    break;
                case 4 when challenge is not null:
                    s.Act(new WithdrawInvitation(challenge.FromPlayerId, challenge.InteractionId));
                    break;
                case 5:
                    s.Act(new RollGame(id));
                    break;
                case 6:
                    s.Act(new StartRun(id));
                    break;
                case 7:
                case 8:
                    s.Act(new CompleteRun(id, (Difficulty)random.Next(3), null, null, false, null));
                    break;
                case 9:
                    s.Act(new DropRun(id));
                    break;
                case 10:
                    s.Act(new TechReroll(id, TechRerollReason.DoesNotLaunch, null));
                    break;
                case 11 when run is not null:
                    s.Act(new RejectProof(run.RunId, "Случайный реджект"));
                    break;
                case 12 when run is not null:
                    s.Act(new ApproveProof(run.RunId, null, null));
                    break;
                case 13:
                    s.Act(new FireTimers());
                    break;
                case 14:
                    if (step % 7 == 0)
                    {
                        s.Act(new SetPlayerInactive(id, !s.Player(player).IsInactive));
                        break;
                    }

                    s.Advance(TimeSpan.FromHours(1 + random.Next(48)));
                    continue;
                case 15 when s.History.Count > setup:
                    // Any command since the setup, not only the last: an undo past later commands must wait for the dependents (D-735)
                    s.Act(new UndoCommand(s.History[setup + random.Next(s.History.Count - setup)].CommandId, "Случайный откат"));
                    break;
                default:
                    continue;
            }

            if (s.Last.IsAccepted)
            {
                s.Explained(check);
            }
        }

        return s;
    }

    private static void Check(Scenario s)
    {
        var rules = s.State.Rules.Duels!;
        // Open is what still waits or runs by the clock: an invitation past its time is no longer answered (interaction.notPending)
        // and a race past its end no longer wins, even before the timer writes it down
        var now = s.Clock.UtcNow;
        var waiting = s.State.Interactions.Values.Where(i => i.Kind == InteractionKind.Duel && i.Status == InteractionStatus.Pending && now < i.ExpiresAt).ToList();
        var races = s.State.Duels.Values.Where(d => d.Status == DuelStatus.Active && now < d.ExpiresAt).ToList();
        List<(Guid From, Guid To)> open = [.. waiting.Select(i => (i.FromPlayerId, i.ToPlayerId)), .. races.Select(d => (d.ChallengerId, d.OpponentId))];

        Assert.Equal(open.Count, open.Select(Pair).Distinct().Count());
        foreach (var player in s.State.Players.Values)
        {
            var id = player.PlayerId;
            var own = waiting.Count(i => i.FromPlayerId == id) + races.Count(d => d.Involves(id));
            Assert.True(own <= rules.MaxOpenPerPlayer, $"{s.PlayerName(id)} has {own} open duels");
            var sent = s.State.Interactions.Values.Count(i => i.Status == InteractionStatus.Pending && now < i.ExpiresAt && i.FromPlayerId == id);
            Assert.True(sent <= s.State.Rules.Interactions.MaxOpenInvitesPerPlayer, $"{s.PlayerName(id)} has {sent} invitations sent");
            if (player.IsInactive || FinishLine.First(s.State) == id)
            {
                Assert.DoesNotContain(open, o => o.From == id || o.To == id);
            }

            var paying = s.State.Duels.Values.Count(d => d.Status == DuelStatus.Won && d.WinnerId == player.PlayerId && d.Coins > 0);
            Assert.True(paying <= rules.MaxWinsPerSeason, $"{s.PlayerName(player.PlayerId)} has {paying} paying wins");

            var paid = s.EffectiveLog.OfType<CoinsChanged>()
                .Where(c => c.PlayerId == player.PlayerId && c.Reason is CoinsReason.Duel or CoinsReason.DuelRevoked)
                .Sum(c => c.Delta);
            var held = s.State.Duels.Values.Where(d => d.Status == DuelStatus.Won && d.WinnerId == player.PlayerId).Sum(d => d.Coins);
            var kept = Kept(s, player.PlayerId);
            Assert.True(kept == 0 || player.Finish?.Frozen == true, $"{s.PlayerName(player.PlayerId)} kept {kept} duel coins unfrozen");
            Assert.Equal(held + kept, paid);
        }

        foreach (var duel in s.State.Duels.Values)
        {
            Assert.NotEqual(duel.ChallengerId, duel.OpponentId);
            var invitation = s.State.Interactions[duel.DuelId];
            Assert.Equal(
                (InteractionKind.Duel, InteractionStatus.Accepted, duel.ChallengerId, duel.OpponentId),
                (invitation.Kind, invitation.Status, invitation.FromPlayerId, invitation.ToPlayerId));
            if (duel.End == DuelEnd.WinRejected)
            {
                // A revoked win keeps its winner and run and pays nothing (D-414)
                Assert.Equal((DuelStatus.Drawn, 0), (duel.Status, duel.Coins));
                Assert.Equal(RunStatus.Rejected, s.State.Runs[duel.RunId!.Value].Status);
                continue;
            }

            if (duel.Status != DuelStatus.Won)
            {
                Assert.Null(duel.WinnerId);
                continue;
            }

            Assert.True(duel.Involves(duel.WinnerId!.Value));
            Assert.Equal(duel.End == DuelEnd.Forfeit, duel.RunId is null);
            if (duel.RunId is { } runId)
            {
                var run = s.State.Runs[runId];
                Assert.Equal(duel.WinnerId, run.PlayerId);
                Assert.Equal(RunStatus.Completed, run.Status);
                Assert.DoesNotContain(runId, duel.RunsAtAcceptance);
                Assert.True(run.RolledAt >= duel.AcceptedAt, "The winning run was rolled before the accept.");
                Assert.True(run.CompletedAt!.Value < duel.ExpiresAt, "The winning run was completed after the race's end.");
            }
        }

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));

        // An undo leaves what the log without the undone commands gives (invariant 12, D-735)
        var without = SeasonEngine.Replay(s.EffectiveLog);
        Assert.Equal(without.Duels, s.State.Duels);
        Assert.Equal(without.Interactions, s.State.Interactions);
    }

    private static (Guid, Guid) Pair((Guid From, Guid To) duel) =>
        duel.From.CompareTo(duel.To) < 0 ? (duel.From, duel.To) : (duel.To, duel.From);

    // The coins of each revoked win it did not take back: the win's coins less the revocation's
    private static int Kept(Scenario s, Guid playerId)
    {
        var paid = new Dictionary<Guid, int>();
        var kept = 0;
        foreach (var e in s.EffectiveLog)
        {
            switch (e)
            {
                case DuelSettled settled when settled.WinnerId == playerId:
                    paid[settled.DuelId] = settled.Coins;
                    break;
                case DuelWinRevoked revoked when revoked.WinnerId == playerId:
                    kept += paid[revoked.DuelId] - revoked.Coins;
                    break;
                default:
                    break;
            }
        }

        return kept;
    }
}

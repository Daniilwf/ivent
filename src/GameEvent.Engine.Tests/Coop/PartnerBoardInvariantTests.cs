using FsCheck.Xunit;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// Random games with the partner board (task 5.11, D-524, D-545): rolls, requests, responses, answers, direct invitations,
/// leaving, completions, drops, tech rerolls, undos, timers and the board switched off and on among four players. After
/// every command: the log replays to the state (1); a coop joins the roller of the run and the other side of its invitation,
/// so a response makes the responder the partner and keeps the author the roller; an open request (by the clock) names a
/// running run of its author without a partner, one per run, whether the board was switched off meanwhile or not (D-743);
/// a pending response has an open request of its run (D-742).
/// </summary>
public class PartnerBoardInvariantTests
{
    private static readonly string[] s_players = ["Вася", "Петя", "Маша", "Коля"];

    [Property(MaxTest = 200)]
    public void Board_invariants_hold_in_random_games(int seed, byte[] script) => Play(seed, script, (_, _, _) => { });

    [Fact]
    public void Random_games_reach_posts_responses_board_coops_and_every_closing()
    {
        // The generator must exercise what it adds, not only allow it; the deadline closing is in PartnerBoardEndTests
        var (posted, responded, joined, undone) = (0, 0, 0, 0);
        var reasons = new HashSet<PartnerRequestCloseReason>();
        for (var seed = 0; seed < 60; seed++)
        {
            var x = (uint)seed + 11;
            var script = Enumerable.Range(0, 300).Select(_ => (byte)((x = (x * 1103515245) + 12345) >> 16)).ToArray();
            Play(seed, script, (s, command, before) =>
            {
                if (!s.Last.IsAccepted)
                {
                    return;
                }

                posted += s.LastEvents<PartnerRequestPosted>().Count();
                responded += command is RespondToPartnerRequest ? 1 : 0;
                joined += s.LastEvents<CoopJoined>().Count(j => before.Interactions[j.InteractionId].ToPlayerId == j.RollerId);
                undone += command is UndoCommand && before.PartnerRequests.Count > 0 ? 1 : 0;
                reasons.UnionWith(s.LastEvents<PartnerRequestClosed>().Select(c => c.Reason));
            });
        }

        var counts = $"posted {posted}, responded {responded}, joined {joined}, undone {undone}, reasons {string.Join(", ", reasons)}";
        Assert.True(posted > 20, counts);
        Assert.True(responded > 10, counts);
        Assert.True(joined > 5, counts);
        Assert.True(undone > 5, counts);
        Assert.Equal(
            [PartnerRequestCloseReason.Coop, PartnerRequestCloseReason.RunEnded, PartnerRequestCloseReason.Timeout, PartnerRequestCloseReason.Author],
            reasons.Order().ToArray());
    }

    private static void Play(int seed, byte[] script, Action<Scenario, ICommand, SeasonState> afterEach)
    {
        // Most games of the pool are coop here, so requests meet runs they can be posted on
        CoopTestScenario.New(r => r.WithPartnerBoard(requestTtlHours: 24), mapLength: 30, seed: seed)
            .MarkCoop(CoopTestScenario.SoloGame).MarkCoop(CoopTestScenario.OtherSoloGame).MarkCoop(CoopTestScenario.ThirdSoloGame)
            .Explained(game =>
        {
            var setup = game.History.Count;
            var boardWasOff = false;
            foreach (var b in script)
            {
                game.Advance(TimeSpan.FromHours(1 + (b % 3)));
                var before = game.State;
                var command = CommandFor(game, b, setup);
                game.Act(command);
                boardWasOff |= !game.State.Rules.Features.PartnerBoard;
                Check(game, before, boardWasOff);
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
        var open = state.PartnerRequests.Values.Where(r => r.Status == PartnerRequestStatus.Open).ToList();
        var mine = open.FirstOrDefault(r => r.PlayerId == player);
        var others = open.FirstOrDefault(r => r.PlayerId != player);
        return ((b / 4) % 16) switch
        {
            0 or 1 or 2 => me.Phase switch
            {
                TurnPhase.Idle => new RollGame(player),
                TurnPhase.Rolling => new StartRun(player),
                _ => new CompleteRun(player, Difficulty.Normal),
            },
            3 or 4 => new PostPartnerRequest(player),
            5 or 6 => others is null ? new PostPartnerRequest(player) : new RespondToPartnerRequest(player, others.RequestId),
            7 or 8 => pendingTo is null
                ? new RespondToPartnerRequest(player, open.FirstOrDefault()?.RequestId ?? Guid.Empty)
                : new AnswerInvitation(player, pendingTo.InteractionId, Accept: b % 4 != 0),
            9 => mine is null ? new InviteToCoop(player, other) : new ClosePartnerRequest(player, mine.RequestId),
            10 => pendingFrom is null ? new LeaveCoop(player) : new WithdrawInvitation(player, pendingFrom.InteractionId),
            11 => b % 2 == 0 ? new DropRun(player) : new TechReroll(player, TechRerollReason.WeakPc, Comment: null),
            12 or 13 => new FireTimers(),
            // The last command most often, else an earlier one, so undos meet their dependents
            14 => s.History.Count <= setup
                ? new FireTimers()
                : new UndoCommand(s.History[b % 3 == 0 ? setup + (b % (s.History.Count - setup)) : s.History.Count - 1].CommandId, "ошибка админа"),
            // The board switched off and on again mid-game (D-420)
            _ => b % 16 == 1
                ? new Engine.Rulesets.ChangeRuleset(s.Ruleset with { Features = s.Ruleset.Features with { PartnerBoard = !s.Ruleset.Features.PartnerBoard } })
                : new FireTimers(),
        };
    }

    private static void Check(Scenario s, SeasonState before, bool boardWasOff)
    {
        var state = s.State;

        // 1: the log replays to the state
        Assert.Equal(SeasonEngine.Replay(s.Log), state);

        // A coop joins the roller of the run and the other side of the invitation: a response's responder (D-524)
        if (s.Last.IsAccepted)
        {
            foreach (var joined in s.LastEvents<CoopJoined>())
            {
                var invitation = before.Interactions[joined.InteractionId];
                Assert.Equal(state.Runs[joined.RunId].PlayerId, joined.RollerId);
                Assert.Contains(joined.RollerId, new[] { invitation.FromPlayerId, invitation.ToPlayerId });
                Assert.Contains(joined.PlayerId, new[] { invitation.FromPlayerId, invitation.ToPlayerId });
                Assert.NotEqual(joined.RollerId, joined.PlayerId);
            }
        }

        // D-743 (decided by the owner 2026-11-14): the board switched off no longer lets a request outlive its run or its
        // coop, so the requests are checked whether the board was ever off or not (was: skipped once it had been off)
        _ = boardWasOff;
        var now = s.Clock.UtcNow;
        var open = state.PartnerRequests.Values.Where(r => r.Status == PartnerRequestStatus.Open && r.ExpiresAt > now).ToList();
        foreach (var request in open)
        {
            var run = state.Runs[request.RunId];
            Assert.Equal(RunStatus.Playing, run.Status);
            Assert.Equal(request.PlayerId, run.PlayerId);
            Assert.Null(run.Coop);
        }

        Assert.Equal(open.Count, open.Select(r => r.RunId).Distinct().Count());

        // D-742 (decided by the owner 2026-11-14): a pending response — a coop invitation to the roller of its run — lives
        // only while a request of that roller on that run is open; closing the request in any way cancels it
        foreach (var response in state.Interactions.Values.Where(i =>
            i.Kind == InteractionKind.Coop && i.Status == InteractionStatus.Pending && i.RunId is { } runId && state.Runs[runId].PlayerId == i.ToPlayerId))
        {
            Assert.True(
                state.PartnerRequests.Values.Any(r => r.RunId == response.RunId && r.PlayerId == response.ToPlayerId && r.Status == PartnerRequestStatus.Open),
                $"The response {response.InteractionId} is pending while no request of its run is open.");
        }
    }
}

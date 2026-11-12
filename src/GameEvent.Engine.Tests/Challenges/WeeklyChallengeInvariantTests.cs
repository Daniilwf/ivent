using FsCheck.Xunit;
using GameEvent.Engine.Challenges;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Challenges;

/// <summary>
/// The weekly challenge in random games (the invariant of task 5.13 for the generator of 5.15): rolls, starts, completions
/// at random difficulties, rejects, approvals, the admin's marks, the clock and undos, with an automatic and a manual
/// challenge. After every accepted command:
/// <list type="bullet">
/// <item>a player holds at most one completion of a challenge;</item>
/// <item>a completion stands on a run of that player that is completed (not rejected) inside the window;</item>
/// <item>an automatic completion stands on a run whose game meets the condition;</item>
/// <item>the challenge coins of a player in the effective log equal the coins of the completions they hold;</item>
/// <item>replaying the log gives the same state.</item>
/// </list>
/// </summary>
public class WeeklyChallengeInvariantTests
{
    private static readonly string[] s_players = ["Вася", "Петя", "Маша"];

    [Property(MaxTest = 150)]
    public void Weekly_challenge_invariants_hold_in_random_games(int seed, byte[] script) =>
        Play(seed, script ?? [], Check);

    [Property(MaxTest = 30)]
    public void Same_seed_and_commands_give_the_same_log(int seed, byte[] script)
    {
        var first = Play(seed, script ?? [], (_, _, _) => { });
        var second = Play(seed, script ?? [], (_, _, _) => { });

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Fact]
    public void Random_games_reach_completions_revocations_and_marks()
    {
        var (completed, revoked, marked) = (0, 0, 0);
        for (var seed = 0; seed < 40; seed++)
        {
            var x = (uint)seed + 7;
            var script = Enumerable.Range(0, 200).Select(_ => (byte)((x = (x * 1103515245) + 12345) >> 16)).ToArray();
            // The manual challenge by its id: a random undo may take back its setting while nobody completed it (D-577)
            Play(seed, script, (s, _, manual) =>
            {
                completed += s.Last.Events.OfType<WeeklyChallengeCompleted>().Count();
                revoked += s.Last.Events.OfType<WeeklyChallengeRevoked>().Count();
                marked += s.Last.Events.OfType<WeeklyChallengeCompleted>().Count(c => c.ChallengeId == manual);
            });
        }

        Assert.True(completed > 0 && revoked > 0 && marked > 0, $"completed {completed}, revoked {revoked}, marked {marked}");
    }

    private static Scenario Play(int seed, byte[] script, Action<Scenario, Guid, Guid> check)
    {
        var s = WeeklyChallengeScenario.New(seed: seed);
        var auto = s.SetChallenge(WeeklyChallengeScenario.OldSchool(), s.Clock.UtcNow.AddHours(6));
        var manual = s.SetChallenge(WeeklyChallengeScenario.Manual());
        check(s, auto, manual);
        var random = new System.Random(seed);
        foreach (var step in script)
        {
            var player = s_players[random.Next(s_players.Length)];
            var id = s.PlayerId(player);
            var runs = s.State.Runs.Values.Where(r => r.Status is RunStatus.Completed or RunStatus.Rejected).ToList();
            var run = runs.Count == 0 ? (RunState?)null : runs[random.Next(runs.Count)];
            switch (step % 9)
            {
                case 0:
                case 1:
                    s.Act(new RollGame(id));
                    break;
                case 2:
                    s.Act(new StartRun(id));
                    break;
                case 3:
                    s.Act(new CompleteRun(id, (Difficulty)random.Next(3), null, null, false, null));
                    break;
                case 4 when run is not null:
                    s.Act(new RejectProof(run.RunId, "Случайный реджект"));
                    break;
                case 5 when run is not null:
                    s.Act(new ApproveProof(run.RunId, null, null));
                    break;
                case 6 when run is not null:
                    s.Act(new MarkWeeklyChallenge(manual, run.PlayerId, run.RunId));
                    break;
                case 7:
                    s.Advance(TimeSpan.FromHours(1 + random.Next(36)));
                    continue;
                case 8 when s.History.Count > 0:
                    s.Act(new UndoCommand(s.History[^1].CommandId, "Случайный откат"));
                    break;
                default:
                    continue;
            }

            if (s.Last.IsAccepted)
            {
                s.Explained(x => check(x, auto, manual));
            }
        }

        return s;
    }

    private static void Check(Scenario s, Guid auto, Guid manual)
    {
        foreach (var challenge in s.State.WeeklyChallenges.Values)
        {
            Assert.Equal(challenge.Completions.Count, challenge.Completions.Select(c => c.PlayerId).Distinct().Count());
            foreach (var completion in challenge.Completions)
            {
                var run = s.State.Runs[completion.RunId];
                Assert.Equal(run.PlayerId, completion.PlayerId);
                Assert.Equal(RunStatus.Completed, run.Status);
                Assert.InRange(run.CompletedAt!.Value, challenge.StartsAt, challenge.EndsAt);
                if (challenge.ChallengeId == auto)
                {
                    Assert.True(s.PoolGame(run.GameId).ReleaseYear is < 2005, $"«Олдскул» completed by {s.GameTitle(run.GameId)}");
                }
            }
        }

        foreach (var player in s.State.Players.Values)
        {
            var paid = s.EffectiveLog.OfType<CoinsChanged>()
                .Where(c => c.PlayerId == player.PlayerId && c.Reason is CoinsReason.WeeklyChallenge or CoinsReason.WeeklyChallengeRevoked)
                .Sum(c => c.Delta);
            var held = s.State.WeeklyChallenges.Values.SelectMany(w => w.Completions).Where(c => c.PlayerId == player.PlayerId).Sum(c => c.Coins);
            Assert.Equal(held, paid);
        }

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
        _ = manual;
    }
}


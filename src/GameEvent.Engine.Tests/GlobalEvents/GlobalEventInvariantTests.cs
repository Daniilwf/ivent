using FsCheck.Xunit;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Effects;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;
using static GameEvent.Engine.Tests.GlobalEvents.GlobalSetup;

namespace GameEvent.Engine.Tests.GlobalEvents;

/// <summary>
/// Random games with global events (task 5.9; D-517, D-518, D-519, D-537, D-755; EV19, EV31, EV49): the admin launches events
/// with goals of hours and of runs, ends them early, the clock moves and the timer ends them, players roll, start and
/// complete (a map of 12, so some finish), the admin rejects and approves proofs, corrects hours and undoes commands; the
/// queue's settlements run after each command, as D-702 does. After every command: a refusal changes nothing; 1 — the log
/// replays to the state; the progress of a goal is the sum of its contributions, one per run, each of a run still
/// completed, of its hours (a goal of hours) or 1 (a goal of runs); a goal is reached at most once, and its flag says
/// whether it was; at most <c>globalEvents.maxActive</c> events run at a time; no settlement of a global event moves a
/// player; a command stays within the chain limit. Written by <c>test-author</c> from the SPEC, before the implementation
/// is finished.
/// </summary>
public class GlobalEventInvariantTests
{
    private static readonly string[] s_objects = ["coin-rain", "bonus-day", "gift-rain", "banner", "self-coins", "horror-week"];

    private static Scenario NewSeason(int seed) => Season(Rules(maxActive: 1 + (Math.Abs(seed) % 2)), mapLength: 12, seed: seed);

    private static GlobalEventGoal? GoalFor(int arg) => (arg % 4) switch
    {
        0 => null,
        1 => HoursGoal(1 + (arg % 20), OnAll(Coins(1))),
        2 => RunsGoal(1 + (arg % 3), OnAll(Coins(1), Points(1))),
        _ => HoursGoal(5 + (arg % 7)),
    };

    private static ICommand CommandFor(Scenario s, byte b, int setup)
    {
        var arg = b / 9;
        var events = s.State.GlobalEvents.Values.ToList();
        var completed = s.State.Runs.Values.Where(r => r.Status == RunStatus.Completed).OrderBy(r => r.RunId).ToList();
        switch (b % 9)
        {
            case 0:
                return new LaunchGlobalEvent(s_objects[arg % s_objects.Length], 6 + (arg % 30), GoalFor(arg));
            case 1 when events.Count > 0:
                return new EndGlobalEvent(events[arg % events.Count].GlobalEventId);
            case 2:
                s.Advance(TimeSpan.FromHours(1 + (arg % 12)));
                return new FireTimers();
            case 3 when completed.Count > 0:
                return new RejectProof(completed[arg % completed.Count].RunId, "Случайный реджект");
            case 4 when completed.Count > 0:
                return new ApproveProof(completed[arg % completed.Count].RunId, null, null);
            case 5 when completed.Count > 0:
                return new CorrectRunHours(completed[arg % completed.Count].RunId, 1 + (arg % 15), "Правка часов");
            case 6 when s.History.Count > setup:
                var played = s.History.Count - setup;
                return new UndoCommand(s.History[s.History.Count - 1 - Math.Min(arg % 3, played - 1)].CommandId, "ошибка админа");
            default:
                var player = s.Player(Names[arg % Names.Length]);
                return player.Phase switch
                {
                    TurnPhase.Idle => new RollGame(player.PlayerId),
                    TurnPhase.Rolling => new StartRun(player.PlayerId),
                    _ => new CompleteRun(player.PlayerId, Difficulty.Normal),
                };
        }
    }

    private static Scenario Play(int seed, byte[] script, bool check)
    {
        var s = NewSeason(seed);
        var setup = s.History.Count;
        return s.Explained(x =>
        {
            foreach (var b in script ?? [])
            {
                Step(x, CommandFor(x, b, setup), check);

                // The queue settles what a command queued before the next command (D-702)
                for (var i = 0; i < 10 && x.Last.IsAccepted && ForAllSettlements.Next(x.State) is { } id; i++)
                {
                    Step(x, new SettleForAll(id), check);
                }
            }
        });
    }

    private static void Step(Scenario s, ICommand command, bool check)
    {
        var before = s.State;
        var logLength = s.Log.Count;
        s.Act(command);
        if (check)
        {
            CheckInvariants(s, command, before, logLength);
        }
    }

    [Property(MaxTest = 120)]
    public void Invariants_hold_in_games_with_global_events(int seed, byte[] script) => Play(seed, script, check: true);

    [Property(MaxTest = 30)]
    public void Same_seed_and_commands_give_the_same_log_with_global_events(int seed, byte[] script)
    {
        var first = Play(seed, script, check: false);
        var second = Play(seed, script, check: false);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Fact]
    public void The_random_games_reach_launches_progress_goals_rejects_corrections_ends_and_undos()
    {
        // The generator is worth its invariants only if it gets there
        var seen = new HashSet<string>();
        var scripts = Enumerable.Range(0, 60)
            .Select(seed => (Seed: seed, Script: Enumerable.Range(0, 90).Select(i => (byte)((seed * 53 + i * 97) % 256)).ToArray()));
        foreach (var (seed, script) in scripts)
        {
            var s = Play(seed, script, check: true);
            foreach (var e in s.Log)
            {
                seen.Add(e switch
                {
                    GlobalEventLaunched => "launched",
                    GlobalProgressChanged { Reason: GlobalProgressReason.Completed } => "progress",
                    GlobalProgressChanged { Reason: GlobalProgressReason.Rejected } => "rejected",
                    GlobalProgressChanged { Reason: GlobalProgressReason.HoursCorrected } => "corrected",
                    GlobalGoalReached => "reached",
                    GlobalEventEnded { Reason: GlobalEventEndReason.Timer } => "timer",
                    GlobalEventEnded { Reason: GlobalEventEndReason.Admin } => "admin",
                    ForAllSettled => "settled",
                    CommandUndone => "undone",
                    _ => "other",
                });
            }
        }

        Assert.Empty(new[] { "launched", "progress", "rejected", "corrected", "reached", "timer", "admin", "settled", "undone" }.Except(seen));
    }

    private static void CheckInvariants(Scenario s, ICommand command, SeasonState before, int logLength)
    {
        if (!s.Last.IsAccepted)
        {
            Assert.Equal(before, s.State);
            Assert.Equal(logLength, s.Log.Count);
            return;
        }

        // 1: the state rebuilt from the log (through its stored form) is the state
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));

        var reached = s.EffectiveLog.OfType<GlobalGoalReached>().GroupBy(r => r.GlobalEventId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var global in s.State.GlobalEvents.Values)
        {
            // One contribution per run, each of a run still completed, of its hours or 1
            Assert.Equal(global.Contributions.Count, global.Contributions.Select(c => c.RunId).Distinct().Count());
            foreach (var contribution in global.Contributions)
            {
                var run = s.State.Runs[contribution.RunId];
                Assert.Equal(RunStatus.Completed, run.Status);
                Assert.Equal(global.Goal!.Metric == GoalMetric.Runs ? 1m : run.Hours!.Value, contribution.Amount);
            }

            // The progress is what the commands in force wrote: the sum of their changes (by review — not of itself)
            Assert.Equal(
                s.EffectiveLog.OfType<GlobalProgressChanged>().Where(p => p.GlobalEventId == global.GlobalEventId).Sum(p => p.Delta), global.Progress);
            if (global.Goal is null)
            {
                Assert.Empty(global.Contributions);
            }

            // Reached at most once, and the flag says so
            var times = reached.GetValueOrDefault(global.GlobalEventId);
            Assert.InRange(times, 0, 1);
            Assert.Equal(times == 1, global.Reached);
            Assert.Equal(global.Status == GlobalEventStatus.Ended, global.EndReason is not null);
        }

        Assert.All(reached, r => Assert.InRange(r.Value, 0, 1));

        // At most maxActive running at a time
        Assert.InRange(s.State.GlobalEvents.Values.Count(g => g.RunsAt(s.Clock.UtcNow)), 0, s.Ruleset.GlobalEvents!.MaxActive);

        // D-519: no settlement of a global event moves anyone
        if (command is SettleForAll)
        {
            Assert.DoesNotContain(s.Last.Events, e => e is PlayerMoved);
        }

        Assert.True(s.Last.Events.Count <= Limits.MaxEventsPerCommand + 1, $"{s.Last.Events.Count} events in one command.");
    }
}

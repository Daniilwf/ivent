using FsCheck.Xunit;
using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;
using static GameEvent.Engine.Tests.Polls.PollSetup;

namespace GameEvent.Engine.Tests.Polls;

/// <summary>
/// Random games with polls (task 5.8, invariant 18 «Не больше одного голоса от пользователя»; EV24, EV40, EV42, EV49): the
/// admin starts polls with and without an effect, players and spectators vote (valid and invalid options, the same vote
/// again, changes), the admin closes early, the timer closes, players play and finish (a map of 4), the admin moves the
/// leader, commands are undone. The queue's settlements for everyone run after each command, as D-702 does. After every
/// command: a refusal changes nothing; 1 — the log replays to the state; 18 — at most one vote in force per user and poll;
/// a closed poll's counts are frozen and are the counts of its <see cref="PollClosed"/>; the result is the option with the
/// most votes, a tie the option of the recorded roll, none without votes; a vote is cast or changed only in an open poll,
/// only as a real change, and the first finisher votes only in polls without an effect; 13 — an accepted undo leaves the
/// state the log without it replays to; 14 — the same seed and commands give the same log. Written by <c>test-author</c>
/// from the SPEC, before the implementation.
/// </summary>
public class PollInvariantTests
{
    private static readonly string[] s_players = ["Вася", "Петя", "Маша"];

    private static readonly Guid[] s_spectators =
    [
        Guid.Parse("4f000000-0000-0000-0000-000000000001"),
        Guid.Parse("4f000000-0000-0000-0000-000000000002"),
    ];

    private static readonly PollDefinition[] s_definitions =
    [
        ContentPoll(),
        ListPoll(PollVoters.Everyone),
        ListPoll(PollVoters.Players, anonymous: true, options: ["Да", "Нет"]),
        ListPoll() with { Options = new ChoiceOptionsSpec { From = ChoiceSource.Players } },
        ListEffectPoll(new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.RandomActive }, Actions = [Coins(1)] }),
        EffectPoll(new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.All }, Actions = [Coins(1)] }) with { Voters = PollVoters.Everyone },
    ];

    private static Scenario NewSeason(int seed)
    {
        var rules = Rules(Spectators(seed % 2 == 0), allowChangeVote: seed % 3 != 0, showLiveCountsDefault: seed % 5 == 0);
        var s = Scenario.New(rules, seed).WithMapLength(4)
            .WithCategory("Horror").WithCategory("RPG")
            .WithGame("Silent Hill", 6, "Horror").WithGame("Alan Wake", 6, "Horror")
            .WithGame("Skyrim", 6, "RPG").WithGame("Persona", 6, "RPG")
            .WithPlayers(s_players);
        return s.WithContent(EconomyScenario.ExamplePack("forced-genre"));
    }

    private static IReadOnlyList<Guid> Users(Scenario s) => [.. s_players.Select(p => s.Player(p).UserId), .. s_spectators];

    private static ICommand CommandFor(Scenario s, byte b, int setup)
    {
        var arg = b / 8;
        var polls = s.State.Polls.Values.ToList();
        var poll = polls.Count == 0 ? null : polls[arg % polls.Count];
        switch (b % 8)
        {
            case 0:
                return new StartPoll(s_definitions[arg % s_definitions.Length]);
            case 1 or 2 when poll is not null:
                var users = Users(s);
                var user = users[(arg / 3) % users.Count];

                // One index past the options is a vote off them
                var index = (arg / 2) % (poll.Options.Count + 1);
                return new CastVote(poll.PollId, user, index < poll.Options.Count ? poll.Options[index].Id : "нет такого");
            case 3 when poll is not null:
                return new ClosePoll(poll.PollId);
            case 4:
                s.Advance(TimeSpan.FromHours(7));
                return new FireTimers();
            case 5 when s.History.Count > setup:
                // The game's own commands only: the setup stays (D-566)
                var played = s.History.Count - setup;
                return new UndoCommand(s.History[s.History.Count - 1 - Math.Min(arg % 3, played - 1)].CommandId, "ошибка админа");
            case 6:
                var player = s.Player(s_players[arg % s_players.Length]);
                return player.Phase switch
                {
                    TurnPhase.Idle => new RollGame(player.PlayerId),
                    TurnPhase.Rolling => new StartRun(player.PlayerId),
                    _ => new CompleteRun(player.PlayerId, Difficulty.Normal),
                };
            default:
                return new AdjustPlayer(s.PlayerId(s_players[arg % s_players.Length]), "Очки для теста", PointsDelta: (arg % 2 == 0 ? 3 : -2));
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

    [Property(MaxTest = 150)]
    public void Invariants_hold_in_games_with_polls(int seed, byte[] script) => Play(seed, script, check: true);

    [Property(MaxTest = 40)]
    public void Same_seed_and_commands_give_the_same_log_with_polls(int seed, byte[] script)
    {
        var first = Play(seed, script, check: false);
        var second = Play(seed, script, check: false);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Fact]
    public void The_random_games_reach_votes_changes_closes_ties_empty_polls_finishes_and_undos()
    {
        // The generator is worth its invariants only if it gets there
        var seen = new HashSet<string>();
        var scripts = Enumerable.Range(0, 60)
            .Select(seed => (Seed: seed, Script: Enumerable.Range(0, 80).Select(i => (byte)((seed * 53 + i * 97) % 256)).ToArray()));
        foreach (var (seed, script) in scripts)
        {
            var s = Play(seed, script, check: true);
            foreach (var e in s.Log)
            {
                switch (e)
                {
                    case PollStarted:
                        seen.Add("started");
                        break;
                    case VoteCast:
                        seen.Add("cast");
                        break;
                    case VoteChanged:
                        seen.Add("changed");
                        break;
                    case PollClosed { Result: null }:
                        seen.Add("empty");
                        break;
                    case PollClosed { Tied.Count: > 1 }:
                        seen.Add("tie");
                        break;
                    case PollClosed { Reason: PollCloseReason.Admin }:
                        seen.Add("admin");
                        break;
                    case PollClosed:
                        seen.Add("closed");
                        break;
                    case ForAllQueued:
                        seen.Add("forAll");
                        break;
                    case PlayerFinished:
                        seen.Add("finished");
                        break;
                    case CommandUndone:
                        seen.Add("undone");
                        break;
                }
            }
        }

        Assert.Empty(new[] { "started", "cast", "changed", "empty", "tie", "admin", "closed", "forAll", "finished", "undone" }.Except(seen));
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

        var closings = s.EffectiveLog.OfType<PollClosed>().GroupBy(c => c.PollId).ToDictionary(g => g.Key, g => g.Last());
        foreach (var poll in s.State.Polls.Values)
        {
            // 18: one vote in force per user, kept in user order
            Assert.Equal(poll.Votes.Count, poll.Votes.Select(v => v.UserId).Distinct().Count());
            Assert.Equal(poll.Votes.Select(v => v.UserId).Order(), poll.Votes.Select(v => v.UserId));
            Assert.All(poll.Votes, v => Assert.Contains(v.OptionId, poll.Options.Select(o => o.Id)));

            switch (poll.Status)
            {
                case PollStatus.Closed:
                    var closed = closings[poll.PollId];
                    Assert.Equal(closed.Counts, poll.Counts());
                    Assert.Equal(closed.Result, poll.Result);
                    CheckResult(closed);

                    // Frozen: a command other than an undo changes no vote of a poll closed before it
                    if (command is not UndoCommand && before.Polls.TryGetValue(poll.PollId, out var was) && was.Status == PollStatus.Closed)
                    {
                        Assert.Equal(was.Votes, poll.Votes);
                    }

                    break;
                case PollStatus.Cancelled:
                    Assert.Null(poll.Result);
                    Assert.True(poll.HasEffect, "Only a poll with an effect is cancelled.");
                    break;
                default:
                    Assert.Null(poll.Result);
                    break;
            }
        }

        foreach (var e in s.Last.Events)
        {
            switch (e)
            {
                case VoteCast cast:
                    CheckVote(before, cast.PollId, cast.UserId, cast.OptionId, changed: false);
                    break;
                case VoteChanged change:
                    CheckVote(before, change.PollId, change.UserId, change.OptionId, changed: true);
                    break;
            }
        }

        // 13: an accepted undo leaves the season as the log without the undone command replays — but for a batch of a
        // settlement for everyone, whose undo halts the settlement for good (D-701), so it is not the replay without it
        if (command is UndoCommand undo && !s.History.Single(c => c.CommandId == undo.TargetCommandId).Events.Any(e => e is ForAllSettled))
        {
            Assert.Equal(Comparable(SeasonEngine.Replay(s.EffectiveLog)), Comparable(s.State));
        }

        Assert.True(s.Last.Events.Count <= Limits.MaxEventsPerCommand + 1, $"{s.Last.Events.Count} events in one command.");
    }

    /// <summary>The result is the option with the most votes; a tie — the recorded pick among the tied; none without votes.</summary>
    private static void CheckResult(PollClosed closed)
    {
        var most = closed.Counts.Count == 0 ? 0 : closed.Counts.Max(c => c.Votes);
        var leaders = closed.Counts.Where(c => c.Votes == most).Select(c => c.OptionId).ToList();
        if (most == 0)
        {
            Assert.Null(closed.Result);
            Assert.Empty(closed.Tied);
            Assert.Null(closed.TieRoll);
        }
        else if (leaders.Count == 1)
        {
            Assert.Equal(leaders[0], closed.Result);
            Assert.Empty(closed.Tied);
            Assert.Null(closed.TieRoll);
        }
        else
        {
            Assert.Equal(leaders.Order(StringComparer.Ordinal), closed.Tied.Order(StringComparer.Ordinal));
            Assert.InRange(closed.TieRoll!.Value, 0, closed.Tied.Count - 1);
            Assert.Equal(closed.Tied[closed.TieRoll.Value], closed.Result);
        }
    }

    /// <summary>A vote only in an open poll, as a real first vote or change, never by the first finisher in a poll with an effect.</summary>
    private static void CheckVote(SeasonState before, Guid pollId, Guid userId, string optionId, bool changed)
    {
        var poll = before.Polls[pollId];
        Assert.Equal(PollStatus.Open, poll.Status);
        var had = poll.Votes.FirstOrDefault(v => v.UserId == userId);
        if (changed)
        {
            Assert.True(had is not null && had.OptionId != optionId, $"A change of {userId} without a different vote in force.");
            Assert.True(before.Ruleset!.Polls!.AllowChangeVote, "A change while the ruleset forbids it.");
        }
        else
        {
            Assert.True(had is null, $"A second vote of {userId} in {pollId}.");
        }

        var player = before.Players.Values.FirstOrDefault(p => p.UserId == userId);
        if (player is not null && poll.HasEffect)
        {
            Assert.NotEqual(player.PlayerId, FinishLine.First(before));
        }

        if (player is null)
        {
            Assert.True(poll.Voters == PollVoters.Everyone && before.Ruleset!.Social.SpectatorsCanVote, $"A spectator voted in {pollId}.");
        }
    }

    // The counters that only grow (D-104) are not part of what an undo brings back
    private static SeasonState Comparable(SeasonState state) =>
        state with
        {
            FinishesSoFar = 0,
            PointsChanges = 0,
            RulesetVersion = 0,
            Players = state.Players.SetItems(state.Players.Select(p => KeyValuePair.Create(p.Key, p.Value with { PointsTick = 0 }))),
        };
}

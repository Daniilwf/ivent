using FsCheck.Xunit;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// Random seasons with every source of events of task 5.3 (cells, drops, paid rerolls, completions above hard, items, events
/// drawn onto others) and Бабл effects. Each byte of <c>script</c> is one command, valid or not; after every command:
/// (1) no <c>bad-event-intercepted</c> has the source <c>RerollCost</c> or <c>DropPenalty</c> (panel D-484 (2));
/// (2) every interception follows a bad event drawn for the same player in the same command;
/// (3) a frozen first finisher gets no <c>event-drawn</c>, <c>event-draw-missed</c> or manual effect (D-510, EV16);
/// (4) a command writes at most 50 events (invariant 8). At the end the log replays to the state, and the same seed and
/// script give the same log (invariant 14). Belongs to the generator of task 5.15; kept here until it lands there.
/// </summary>
public class EventSourceInvariantTests
{
    private static readonly string[] s_players = ["Вася", "Петя", "Маша"];

    private static Scenario NewSeason(int seed)
    {
        var s = EventSetup.Season(
            EventSetup.Pack(
                ["babl", "bubble-effect", "horror-week", "forced-genre", "tailwind", "coins-minus-3", "coins-plus-2", "sack-bad", "jinx"],
                EventSetup.Deck(EventKind.Bad, "horror-week", "coins-minus-3"),
                EventSetup.Deck(EventKind.Good, "tailwind", "coins-plus-2")),
            map: MapBuilder.New()
                .EventCell("c2", "bad").EventCell("c4", "good").EventCell("c6", "bad")
                .Path("start", "c1", "c2", "c3", "c4", "c5", "c6", "c7", "finish").Build(),
            rules: r => r with { Roll = r.Roll with { RerollCost = new RerollCost { Kind = RerollCostKind.BadEvent } } },
            seed: seed,
            players: s_players);
        return s;
    }

    private static ICommand? CommandFor(Scenario s, byte b)
    {
        var name = s_players[b % s_players.Length];
        var player = s.PlayerId(name);
        var other = s.PlayerId(s_players[(b / 3 + 1 + b % s_players.Length) % s_players.Length]);
        var difficulty = (Difficulty)((b / 27) % 4);
        switch ((b / 3) % 9)
        {
            case 0: return new RollGame(player);
            case 1: return new Reroll(player);
            case 2: return new StartRun(player);
            case 3: return new CompleteRun(player, difficulty);
            case 4: return new DropRun(player);
            case 5:
                // The admin approves the player's latest completed run (the finish, when it is one)
                var run = s.State.Runs.Values.Where(r => r.PlayerId == player && r.Status == RunStatus.Completed).OrderBy(r => r.RunId).LastOrDefault();
                return run is null ? null : new ApproveProof(run.RunId, null, EventSetup.Proof);
            default:
                var objectId = ((b / 3) % 9) switch { 6 => "babl", 7 => "sack-bad", _ => "jinx" };
                s.Act(new AdjustInventory(player, objectId, null, "Для партии"));
                if (!s.Last.IsAccepted)
                {
                    return null;
                }

                var instance = s.LastEvents<ObjectGiven>().Single().Item.InstanceId;
                return new UseItem(player, instance, objectId == "jinx" ? other : null);
        }
    }

    private static Scenario Play(int seed, byte[] script, bool check = true)
    {
        var s = NewSeason(seed);
        foreach (var b in script ?? [])
        {
            s.Advance(TimeSpan.FromMinutes(37));
            var command = CommandFor(s, b);
            if (command is null)
            {
                continue;
            }

            var before = s.State;
            s.Act(command);
            if (check && s.Last.IsAccepted)
            {
                CheckAfter(s, before);
            }
        }

        return s;
    }

    private static void CheckAfter(Scenario s, SeasonState before)
    {
        var events = s.Last.Events;
        Assert.True(events.Count <= 50, $"{events.Count} events in one command.");

        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is not BadEventIntercepted stopped)
            {
                continue;
            }

            Assert.DoesNotContain(stopped.Source, new[] { EventSource.RerollCost, EventSource.DropPenalty });
            Assert.Contains(events.Take(i), e => e is EventDrawn { Kind: EventKind.Bad } d && d.PlayerId == stopped.PlayerId && d.ObjectId == stopped.EventId);
        }

        foreach (var (id, player) in before.Players)
        {
            if (player.Finish?.Frozen != true)
            {
                continue;
            }

            Assert.DoesNotContain(events, e => e is EventDrawn d && d.PlayerId == id);
            Assert.DoesNotContain(events, e => e is EventDrawMissed m && m.PlayerId == id);
            Assert.DoesNotContain(events, e => e is ManualEffectCreated m && m.PlayerId == id);
        }
    }

    [Property(MaxTest = 150)]
    public void Invariants_of_event_sources_hold_after_every_command(int seed, byte[] script)
    {
        var s = Play(seed, script);

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Property(MaxTest = 40)]
    public void Same_seed_and_commands_give_the_same_log_with_event_sources(int seed, byte[] script)
    {
        var first = Play(seed, script, check: false);
        var second = Play(seed, script, check: false);

        Assert.Equal(first.Log, second.Log);
    }
}

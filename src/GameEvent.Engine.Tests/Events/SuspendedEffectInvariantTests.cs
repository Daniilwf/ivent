using FsCheck.Xunit;
using GameEvent.Engine.Content;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// Random games with events that ask (task 5.7, its done-criterion: invariants 1, 12, 13, 14 in games with suspended
/// events). Players step and push one another onto event cells, answer (with valid and invalid options), the admin discards,
/// commands are undone, the clock runs the answer timeout (D-560) on even seeds, and players roll, complete (an extreme
/// difficulty draws its event), drop (the penalty draws a bad event) and use an item that draws (D-559). After every
/// command: 1 — the log replays to the state; 12 — the chain limits; 13 — an accepted undo leaves the state the log without
/// the command replays to; the new invariant of D-562 — a pending choice of <see cref="ChoiceKind.Effect"/> exactly when the
/// player has a suspended effect not resumed or discarded, unless a choice of another kind is asked first (D-567 (10); the
/// map has a fork), and the pending one is among them; 14 — the same seed and commands give the same log.
/// </summary>
public class SuspendedEffectInvariantTests
{
    private static readonly string[] s_players = ["Вася", "Петя", "Маша"];

    /// <summary>An item that draws from the deck of the asking events (D-504, D-559).</summary>
    private static readonly ObjectDefinition s_askSack = ContentJson.Parse<ObjectDefinition>("""
        {
          "id": "ask-sack", "kind": "item", "name": "Мешок вопросов", "description": "Вытяни ивент.",
          "rarity": "common", "price": 10, "window": "anytime", "hostile": false, "stackable": true,
          "effect": { "target": { "selector": "self" }, "actions": [{ "type": "drawEvent", "deck": "ask" }] }
        }
        """);

    private static Scenario NewSeason(int seed)
    {
        var rules = TestRuleset.Create().WithEvents();
        if (seed % 2 == 0)
        {
            rules = rules with { Events = rules.Events! with { AnswerTimeoutHours = 24 } };
        }

        var map = MapBuilder.New()
            .EventCell("e1", "ask").EventCell("e2", "ask").EventCell("e3", "ask").EventCell("e4", "ask").EventCell("e5", "ask")
            .Path("start", "e1", "e2", "a", "e3", "b", "e4", "e5", "c", "d", "f", "g", "h", "finish")
            .Path("b", "x", "e5")
            .Build();
        var s = Scenario.New(rules, seed).WithMap(map)
            .WithCategory("Horror").WithCategory("RPG")
            .WithGame("Silent Hill", 3, "Horror").WithGame("Persona", 9, "Horror").WithGame("Disco Elysium", 6, "RPG")
            .WithPlayers(s_players);
        var asking = SuspendedEffectTests.AskingEvents;
        ObjectDefinition[] objects =
        [
            .. asking, SuspendedEffectTests.Shove, s_askSack,
            EconomyScenario.Examples()["orange"], EconomyScenario.Examples()["forced-genre"], EconomyScenario.Examples()["lucky-die"],
        ];
        s.WithContent(new ContentPack
        {
            Objects = [.. objects.OrderBy(o => o.Id, StringComparer.Ordinal)],
            // The difficulty's good event and the drop's bad one ask too (TestRuleset: extreme grants good, a drop draws bad)
            Wheels =
            [
                EventScenario.Deck(EventKind.Good, "ask", asking.Select(o => o.Id).ToArray()),
                EventScenario.Deck(EventKind.Bad, "bad", asking.Select(o => o.Id).ToArray()),
                EventScenario.Deck(EventKind.Good, "good", asking.Select(o => o.Id).ToArray()),
            ],
        });
        return s;
    }

    private static ICommand CommandFor(Scenario s, byte b, int setup)
    {
        var name = s_players[(b / 8) % s_players.Length];
        var player = s.Player(name);
        var arg = b / 24;
        switch (b % 8)
        {
            case 0:
                return new AdjustInventory(player.PlayerId, (arg % 3) switch { 0 => "lucky-die", 1 => "ask-sack", _ => "orange" }, null, "Выдано для теста");
            case 1:
                // An item that draws an event, else the orange that steps (D-559)
                return player.Wallet.Inventory.FirstOrDefault(o => o.ObjectId == (arg % 2 == 0 ? "ask-sack" : "orange")) is { } item
                    ? new UseItem(player.PlayerId, item.InstanceId)
                    : new AdjustInventory(player.PlayerId, "orange", null, "Выдано для теста");
            case 2 when player.Choice is { } choice:
                // One index past the options is an answer off them
                var index = arg % (choice.Options.Count + 1);
                return new MakeChoice(player.PlayerId, choice.ChoiceId, index < choice.Options.Count ? choice.Options[index].Id : "нет такого");
            case 2:
                return new MakeChoice(player.PlayerId, Guid.Parse("00000000-0000-0000-0000-00000000abcd"), "монетки");
            case 3:
                return new AdjustPlayer(player.PlayerId, "Сброс для теста", DiscardOffer: true);
            case 4 when s.History.Count > setup:
                // The game's own commands only: the setup (players, content) stays, as in the other generators (D-566)
                var history = s.History;
                var played = history.Count - setup;
                return new UndoCommand(history[history.Count - 1 - Math.Min(arg % 3, played - 1)].CommandId, "ошибка админа");
            case 5:
                s.Advance(TimeSpan.FromHours(13));
                return new FireTimers();
            case 6:
                return player.Phase switch
                {
                    TurnPhase.Idle => new RollGame(player.PlayerId),
                    TurnPhase.Rolling => new StartRun(player.PlayerId),
                    // A drop draws its penalty, an extreme difficulty its event (D-504, D-559)
                    _ => (arg % 3) switch
                    {
                        0 => new DropRun(player.PlayerId),
                        1 => new CompleteRun(player.PlayerId, Difficulty.Extreme),
                        _ => new CompleteRun(player.PlayerId, Difficulty.Normal),
                    },
                };
            default:
                var target = s.PlayerId(s_players[((b / 8) + 1) % s_players.Length]);
                return player.Wallet.Inventory.FirstOrDefault(o => o.ObjectId == "shove") is { } shove
                    ? new UseItem(player.PlayerId, shove.InstanceId, target)
                    : new AdjustInventory(player.PlayerId, "shove", null, "Выдано для теста");
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
                var before = x.State;
                var logLength = x.Log.Count;
                var openBefore = OpenSuspensions(x);
                var command = CommandFor(x, b, setup);
                x.Act(command);
                if (check)
                {
                    CheckInvariants(x, command, before, logLength, openBefore);
                }
            }
        });
    }

    [Property(MaxTest = 150)]
    public void Invariants_hold_in_games_with_suspended_events(int seed, byte[] script) => Play(seed, script, check: true);

    [Property(MaxTest = 40)]
    public void Same_seed_and_commands_give_the_same_log_with_suspended_events(int seed, byte[] script)
    {
        var first = Play(seed, script, check: false);
        var second = Play(seed, script, check: false);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Fact]
    public void The_random_games_reach_suspensions_answers_discards_timeouts_and_undos()
    {
        // The generator is worth its invariants only if it gets there: fixed seeds, scripts that step, answer and undo
        var seen = new HashSet<string>();
        // Beyond the stepping scripts: Вася rolls, starts, waits 13 hours and completes on extreme (30: case 6, arg 1) or
        // drops (6: arg 0), so the difficulty's and the drop's events get drawn too (D-559)
        var scripts = Enumerable.Range(0, 40)
            .Select(seed => (Seed: seed, Script: Enumerable.Range(0, 60).Select(i => (byte)((seed * 37 + i * 101) % 256)).ToArray()))
            .Concat(Enumerable.Range(0, 10).Select(seed => (Seed: seed, Script: new byte[] { 6, 6, 5, (byte)(seed % 2 == 0 ? 30 : 6), 2, 2, 6, 6, 5, 30 })));
        foreach (var (seed, script) in scripts)
        {
            var s = Play(seed, script, check: true);
            foreach (var command in s.History)
            {
                foreach (var e in command.Events)
                {
                    switch (e)
                    {
                        case EffectSuspended suspended:
                            seen.Add("suspended");
                            seen.Add($"suspended:{suspended.Source}");
                            break;
                        case EffectResumed when command.Events.Count > 0 && s.Commands[s.History.ToList().IndexOf(command)] is FireTimers:
                            seen.Add("timeout");
                            break;
                        case EffectResumed:
                            seen.Add("resumed");
                            break;
                        case ChoiceDiscarded:
                            seen.Add("discarded");
                            break;
                        case CommandUndone:
                            seen.Add("undone");
                            break;
                    }
                }
            }
        }

        Assert.Equal(new[] { "discarded", "resumed", "suspended", "timeout", "undone" }, seen.Where(k => !k.Contains(':', StringComparison.Ordinal)).Order());

        // The sources of 5.3 ask in these games too (D-559): a cell, an item, a difficulty, a drop
        Assert.Empty(new[] { "suspended:Cell", "suspended:Item", "suspended:Difficulty", "suspended:DropPenalty" }.Except(seen));
    }

    /// <summary>The suspended effects of each player not yet resumed or discarded, by the log as the rules see it.</summary>
    private static Dictionary<Guid, HashSet<Guid>> OpenSuspensions(Scenario s)
    {
        var open = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var e in s.EffectiveLog)
        {
            switch (e)
            {
                case EffectSuspended suspended:
                    (open.TryGetValue(suspended.PlayerId, out var set) ? set : open[suspended.PlayerId] = []).Add(suspended.ChoiceId);
                    break;
                case EffectResumed resumed when open.TryGetValue(resumed.PlayerId, out var answered):
                    answered.Remove(resumed.ChoiceId);
                    break;
                case ChoiceDiscarded discarded when open.TryGetValue(discarded.PlayerId, out var dropped):
                    dropped.Remove(discarded.ChoiceId);
                    break;
            }
        }

        return open;
    }

    private static void CheckInvariants(Scenario s, ICommand command, SeasonState before, int logLength, Dictionary<Guid, HashSet<Guid>> openBefore)
    {
        if (!s.Last.IsAccepted)
        {
            Assert.Equal(before, s.State);
            Assert.Equal(logLength, s.Log.Count);
        }

        // 1: the state rebuilt from the log (through its stored form) is the state
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));

        // D-562, D-567 (10): a pending choice of an effect only when an effect waits, and it is one of those; an effect that
        // waits is asked unless a choice of another kind (a branch, a game) is asked first
        var open = OpenSuspensions(s);
        foreach (var player in s.State.Players.Values)
        {
            var waiting = open.TryGetValue(player.PlayerId, out var set) ? set : [];
            Assert.True(waiting.Count == 0 || player.Choice is not null, $"{player.Name} has an effect waiting and nothing asked.");
            Assert.True(waiting.Count > 0 || player.Choice?.Kind != ChoiceKind.Effect, $"{player.Name} is asked by an effect that does not wait.");
            if (player.Choice is { Kind: ChoiceKind.Effect } choice)
            {
                Assert.Contains(choice.ChoiceId, waiting);
            }
        }

        // An effect resumes only once, only if it waited, and by an answer of its owner or the timer
        foreach (var resumed in s.Last.Events.OfType<EffectResumed>())
        {
            Assert.True(openBefore.TryGetValue(resumed.PlayerId, out var was) && was.Contains(resumed.ChoiceId), $"{resumed} did not wait.");
            Assert.True(command is FireTimers || (command is MakeChoice answer && answer.PlayerId == resumed.PlayerId), $"{resumed} by {command}.");
        }

        // 12: the chain limits — a suspension keeps its level within them, the cut is the 51st event at most. The level is
        // the one the event's actions run at (D-567 (4), D-559 (1); the bound 0…3 owner-approved 2026-11-13)
        CheckSuspensionLevels(s.Last.Events);
        var cut = s.Last.Events.OfType<EffectChainCut>().SingleOrDefault();
        Assert.True(cut is null || cut.Events <= Limits.MaxEventsPerCommand);
        Assert.True(s.Last.Events.Count <= Limits.MaxEventsPerCommand + 1, $"{s.Last.Events.Count} events in one command.");

        // 13: an accepted undo leaves the season as the log without the undone command replays
        if (command is UndoCommand && s.Last.IsAccepted)
        {
            Assert.Equal(Comparable(SeasonEngine.Replay(s.EffectiveLog)), Comparable(s.State));
        }
    }

    /// <summary>
    /// Invariant 12 for the levels of suspensions, by source (D-567 (4), D-559 (1)): 0…3 at all; a cell's event — a stop
    /// in the chain or of the command's own move — at its reaction level 1…3; the command's own events (difficulty, drop,
    /// reroll) and an item used by its holder or on another player (the only items of these games) at 0.
    /// </summary>
    private static void CheckSuspensionLevels(IReadOnlyList<IGameEvent> events)
    {
        foreach (var suspended in events.OfType<EffectSuspended>())
        {
            Assert.InRange(suspended.Depth, 0, Limits.MaxEffectDepth);
            if (suspended.Source == EventSource.Cell)
            {
                Assert.InRange(suspended.Depth, 1, Limits.MaxEffectDepth);
            }
            else
            {
                Assert.Equal(0, suspended.Depth);
            }
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

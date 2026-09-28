using FsCheck.Xunit;
using GameEvent.Engine.Content;
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
/// Random games on random graph maps (stage 2, D-300…D-308): forks, merges, teleports, checkpoints, bonus cells and a
/// zone, with rolls, completions, branch choices, drops, rejects, corrections, admin moves, map publications and undos.
/// After every command: the log replays to the state (through the stored format too), points and positions are the
/// fold of the effective log, the player stands on a cell of the map with the last path segment on its arrows, a push
/// back never leaves a checkpoint nor passes the start, a paused walk waits at a fork for its branch and blocks the
/// player's other turns, teleports and bonuses fire only on a stop, a run's moved cells are the sum of its moves.
/// Each byte of <c>script</c> is one command; a failure prints the shrunk game as builder code.
/// </summary>
public class GraphInvariantTests
{
    private static readonly string[] s_players = ["Вася", "Петя", "Маша"];

    [Property(MaxTest = 200)]
    public void Invariants_hold_on_random_graph_maps(int seed, byte[] script) => Play(seed, script ?? [], Check);

    [Property(MaxTest = 50)]
    public void Same_seed_and_commands_give_the_same_log_on_a_graph_map(int seed, byte[] script)
    {
        var first = Play(seed, script ?? [], (_, _, _, _) => { });
        var second = Play(seed, script ?? [], (_, _, _, _) => { });

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Fact]
    public void Random_games_meet_forks_teleports_checkpoints_and_bonuses()
    {
        // The invariants must meet their cases over fixed scripts
        var seen = new HashSet<string>();
        for (var seed = 0; seed < 60; seed++)
        {
            var x = (uint)seed + 7;
            var script = Enumerable.Range(0, 300).Select(_ => (byte)((x = (x * 1103515245) + 12345) >> 16)).ToArray();
            Play(seed, script, (s, command, before, _) =>
            {
                // D-325: wish rerolls accepted, refused for an unlisted genre and for one the zone imposed
                if (command is TechReroll { Reason: TechRerollReason.Wish })
                {
                    var outcome = s.Last.IsAccepted ? "wish" : s.Last.Rejection!.Code switch
                    {
                        RejectionCodes.WishRerollNotListed => "wish-unlisted",
                        RejectionCodes.WishRerollImposed => "wish-imposed",
                        _ => null,
                    };
                    if (outcome is not null)
                    {
                        seen.Add(outcome);
                    }
                }

                if (!s.Last.IsAccepted)
                {
                    return;
                }

                foreach (var e in s.Last.Events)
                {
                    var kind = e switch
                    {
                        BranchChoiceRequested => "branch",
                        PlayerMoved { Reason: MoveReason.Teleport } => "teleport",
                        PointsChanged { Reason: PointsReason.CellBonus } => "bonus",
                        MapPublished => "publish",
                        CommandUndone => "undo",
                        PlayerMoved { Reason: MoveReason.DropPenalty } m when m.Path.Count < -m.Steps
                            && s.State.Map.CellById(m.To).Type == CellType.Checkpoint => "checkpoint",
                        _ => null,
                    };
                    if (kind is not null)
                    {
                        seen.Add(kind);
                    }
                }

                // D-321: rejects of runs whose gain is not their walked cells (a teleport, a branch), both ways
                if (command is RejectProof reject && before.Runs[reject.RunId] is { } rejected && rejected.Gain != rejected.Moved)
                {
                    seen.Add(rejected.Gain > rejected.Moved ? "reject-shortcut" : "reject-snake");
                }

                if (command is MakeChoice && before.Players[((MakeChoice)command).PlayerId].Choice?.Kind == ChoiceKind.Branch)
                {
                    seen.Add("chosen");
                }
            });
        }

        Assert.Empty(new[] { "branch", "chosen", "teleport", "bonus", "publish", "undo", "checkpoint", "reject-shortcut", "reject-snake", "wish", "wish-unlisted", "wish-imposed" }.Except(seen));
    }

    // ---- The random map ----

    /// <summary>
    /// A main line start → m1 … → finish of 10–20 steps; 1–3 branches leave a main cell (a fork) and rejoin it further on
    /// (the main entry stays primary); some main cells become teleports (to main cells that are not teleports: no
    /// cycles), checkpoints or bonuses; the cells of one branch form a Horror zone.
    /// </summary>
    internal static MapGraph RandomMap(int seed, int variant = 0)
    {
        var random = new Random(seed);
        var length = random.Next(10, 21);
        var main = Enumerable.Range(0, length).Select(i => i == 0 ? "start" : $"m{i}").Append("finish").ToList();
        var builder = MapBuilder.New().Path([.. main]);
        var forks = new HashSet<int>();
        var branches = random.Next(1, 4);
        var zoneCells = new List<string>();
        for (var b = 0; b < branches; b++)
        {
            var from = random.Next(1, length - 3);
            if (!forks.Add(from))
            {
                continue;
            }

            var to = random.Next(from + 2, Math.Min(length, from + 6) + 1);
            var cells = Enumerable.Range(1, random.Next(1, 5)).Select(i => $"b{from}x{i}").ToList();
            builder.Path([main[from], .. cells, main[to]]);
            if (zoneCells.Count == 0)
            {
                zoneCells.AddRange(cells);
            }
        }

        // Special main cells: not the start, not a fork, not a merge target's neighbour concerns — any empty main cell
        var free = Enumerable.Range(1, length - 1).Where(i => !forks.Contains(i)).ToList();
        foreach (var i in free)
        {
            var id = main[i];
            switch ((random.Next(0, 10) + variant) % 10)
            {
                case 0:
                    // A snake or a shortcut to a main cell that stays plain
                    var target = main[random.Next(0, length)];
                    if (target != id)
                    {
                        builder.Teleport(id, target);
                    }

                    break;
                case 1:
                    builder.Checkpoint(id);
                    break;
                case 2:
                    builder.Bonus(id, random.Next(1, 4) * (random.Next(0, 4) == 0 ? -1 : 1));
                    break;
            }
        }

        var map = builder.Build();

        // Teleports only onto plain cells: no cycles, and the destination may be anything but the finish
        var teleports = map.Cells.Where(c => c.Type == CellType.Teleport).Select(c => c.Id).ToHashSet();
        map = map with
        {
            Cells = [.. map.Cells.Select(c => c.Type == CellType.Teleport && teleports.Contains(c.To!) ? new Cell(c.Id, CellType.Empty) : c)],
        };

        if (zoneCells.Count > 0)
        {
            var zone = new ZoneDefinition
            {
                Id = "swamp",
                Name = "Болото",
                RollFilter = new GameFilterSpec { Tags = ["Horror"] },
                DiceModifier = (seed + variant) % 3 == 0 ? null : new DiceModifierSpec
                {
                    Stage = (seed + variant) % 3 == 1 ? DiceStage.Add : DiceStage.Count,
                    Value = ContentJson.Parse<ContentValue>(((seed + variant) % 2 == 0 ? 1 : -1).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                },
                DropPenaltyMultiplier = (seed + variant) % 2 == 0 ? 1.5m : null,
            };
            map = map with
            {
                Cells = [.. map.Cells.Select(c => zoneCells.Contains(c.Id) ? c with { Zone = "swamp" } : c)],
                Zones = [zone],
            };
        }

        return map;
    }

    [Property(MaxTest = 300)]
    public void Random_maps_are_valid(int seed, byte variant)
    {
        var rules = TestRuleset.Create() with { Features = TestRuleset.Create().Features with { MapMode = MapMode.Graph } };

        Assert.Empty(MapValidator.Validate(RandomMap(seed, variant % 10), rules));
    }

    // ---- The game ----

    private static Scenario Play(int seed, byte[] script, Action<Scenario, ICommand, SeasonState, int> afterEach)
    {
        // D-205, D-206: drops in a row add a die; Horror games may be given up by wish unless the swamp imposed Horror
        var s = Scenario.New(seed: seed)
            .WithRuleset(r => r with { Drop = r.Drop with { ConsecutiveExtraDice = 1 }, Roll = r.Roll with { WishRerollTags = ["horror"] } })
            .WithMap(RandomMap(seed))
            .WithCategory("Horror", weight: 2).WithCategory("Puzzle")
            .WithGame("Silent Hill", 12, "Horror").WithGame("Alan Wake", 6, "Horror").WithGame("Dead Space", 3, "Horror")
            .WithGame("Tetris", 3, "Puzzle").WithGame("Portal", 6, "Puzzle").WithGame("Limbo", null, "Puzzle")
            .WithPlayers(s_players);
        var variant = 0;
        return s.Explained(x =>
        {
            foreach (var b in script)
            {
                var before = x.State;
                var logLength = x.Log.Count;
                var command = CommandFor(x, b, seed, ref variant);
                x.Act(command);
                afterEach(x, command, before, logLength);
            }
        });
    }

    /// <summary>Bits 0–1 the player (3 — the admin's commands), bits 2–4 the kind, bits 5–7 the argument.</summary>
    private static ICommand CommandFor(Scenario s, byte b, int seed, ref int variant)
    {
        var index = b % 4;
        var arg = b / 32;
        if (index == 3)
        {
            return AdminCommandFor(s, b, seed, ref variant);
        }

        var player = s.PlayerId(s_players[index]);
        var p = s.State.Players[player];
        return ((b / 4) % 8) switch
        {
            0 or 1 => new RollGame(player),
            2 => new StartRun(player),
            3 or 4 => new CompleteRun(player, (Difficulty)(arg % 4), EstimatedHours: 1 + arg, HoursSource: "HLTB"),
            5 when p.Choice is { } choice => new MakeChoice(player, choice.ChoiceId, arg < choice.Options.Count * 2 ? choice.Options[arg % choice.Options.Count].Id : "nowhere"),
            5 => new MakeChoice(player, SequentialIds.Make(0x50000000, arg), "nowhere"),
            6 => new DropRun(player),
            _ => new TechReroll(player, arg % 2 == 0 ? TechRerollReason.WeakPc : TechRerollReason.Wish, null),
        };
    }

    private static ICommand AdminCommandFor(Scenario s, byte b, int seed, ref int variant)
    {
        var arg = b / 32;
        var target = s.PlayerId(s_players[arg % 3]);
        var runs = s.State.Runs.Values.Where(r => r.Status == RunStatus.Completed).ToList();
        var run = runs.Count > 0 ? runs[(b + s.Log.Count) % runs.Count] : null;
        switch ((b / 4) % 8)
        {
            case 0 when run is not null:
                return new RejectProof(run.RunId, "нет пруфа");
            case 1 when run is not null:
                return new ApproveProof(run.RunId, Comment: "видел");
            case 2 when run is not null:
                return new CorrectRunHours(run.RunId, 3 * (1 + arg), "часы");
            case 3 when run is not null:
                return new ChangeRunDifficulty(run.RunId, (Difficulty)(arg % 4), "сложность");
            case 4:
                var cells = s.State.Map.Cells.Where(c => c.Type != CellType.Finish).ToList();
                return new AdjustPlayer(target, "перенос", CellId: cells[(b + s.Log.Count) % cells.Count].Id, DiscardOffer: arg % 2 == 0);
            case 5:
                return new AdjustPlayer(target, "сброс", DiscardOffer: true);
            case 6:
                // The same cells and arrows with other special cells and zone rules: every player keeps a cell (D-308)
                variant = (variant % 9) + 1;
                return new PublishMap(RandomMap(seed, variant), $"версия {variant}");
            default:
                // The game's commands, not the setup (creation, players joining, the start)
                var history = s.History.Where(h => !h.Events.Any(e => e is SeasonCreated or SeasonPlayerAdded or SeasonStatusChanged)).ToList();
                return history.Count == 0
                    ? new SetPlayerInactive(target, arg % 2 == 0)
                    : new UndoCommand(history[^(1 + (arg % Math.Min(3, history.Count)))].CommandId, "откат");
        }
    }

    // ---- The invariants ----

    private static void Check(Scenario s, ICommand command, SeasonState before, int logLengthBefore)
    {
        if (!s.Last.IsAccepted)
        {
            ScenarioAssert.Rejected(s, before, logLengthBefore, s.Last.Rejection!.Code);
        }

        // D-305: a player choosing a branch takes no other turn
        if (s.Last.IsAccepted && command is RollGame or StartRun or CompleteRun or DropRun or TechReroll)
        {
            var actor = command switch
            {
                RollGame c => c.PlayerId,
                StartRun c => c.PlayerId,
                CompleteRun c => c.PlayerId,
                DropRun c => c.PlayerId,
                TechReroll c => c.PlayerId,
                _ => Guid.Empty,
            };
            Assert.NotEqual(ChoiceKind.Branch, before.Players[actor].Choice?.Kind);
        }

        // 1. The log replays to the state, the stored format too
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log.Select(e => EventCodec.Decode(EventCodec.Encode(e)))));

        var log = s.EffectiveLog;
        var map = s.State.Map;
        foreach (var player in s.State.Players.Values)
        {
            // 2. Points are the sum of their changes
            Assert.Equal(log.OfType<PointsChanged>().Where(e => e.PlayerId == player.PlayerId).Sum(e => e.Delta), player.Points);

            // 7. On a cell of the map, where the last move put it; the last segment walks the map's arrows
            var lastMove = log.OfType<PlayerMoved>().LastOrDefault(e => e.PlayerId == player.PlayerId);
            Assert.Equal(lastMove?.To ?? map.Start.Id, player.CellId);
            Assert.True(map.HasCell(player.CellId), $"{player.Name} stands on {player.CellId}, which is not on the map.");
            Assert.Equal(player.CellId, player.Path.Current);
            Assert.True(MapPublishing.LiesOn(player.Path, map), $"{player.Name}'s last path segment leaves the map's arrows.");

            // D-304: a branch choice waits at a fork, for its exits, with steps left, while idle
            if (player.Choice is { Kind: ChoiceKind.Branch } branch)
            {
                Assert.Equal(CellType.Fork, map.CellById(player.CellId).Type);
                Assert.Equal(map.Exits(player.CellId).Select(e => e.To), branch.Options.Select(o => o.Id));
                Assert.True(branch.Move is { Steps: > 0, RunId: not null }, "A branch choice without steps or a run.");
                Assert.Equal(TurnPhase.Idle, player.Phase);
                Assert.Equal(RunStatus.Completed, s.State.Runs[branch.Move!.RunId!.Value].Status);
            }
        }

        // A run's moved cells are the sum of its moves
        foreach (var run in s.State.Runs.Values)
        {
            Assert.Equal(
                log.OfType<PlayerMoved>().Where(e => e.RunId == run.RunId).Sum(e => Math.Sign(e.Steps) * e.Path.Count),
                run.Moved);
        }

        if (s.Last.IsAccepted)
        {
            CheckMoves(before, s.Last.Events);
        }

        // D-321: a reject never improves the position — the player of the rejected run ends no closer to the finish
        if (s.Last.IsAccepted && command is RejectProof reject)
        {
            var owner = before.Runs[reject.RunId].PlayerId;
            var distances = Engine.Ranking.Leaderboard.CellsToFinish(before.Map);
            Assert.True(
                distances[s.State.Players[owner].CellId] >= distances[before.Players[owner].CellId],
                $"A reject brought {before.Players[owner].Name} from {before.Players[owner].CellId} closer to the finish, to {s.State.Players[owner].CellId}.");
            CheckRejectPosition(s, reject, before);
        }

        // D-325: a wish reroll goes through only for a listed genre the roll did not impose, within the window
        if (command is TechReroll { Reason: TechRerollReason.Wish } wish && before.Players[wish.PlayerId].ActiveRunId is { } active)
        {
            var run = before.Runs[active];
            var listed = s.PoolGame(run.GameId).Tags.Contains("Horror", StringComparer.OrdinalIgnoreCase);
            var imposed = run.Snapshot.ImposedTags.Count > 0;
            var open = s.Clock.UtcNow - run.RolledAt <= TimeSpan.FromHours(run.Snapshot.TechRerollWindowHours);
            if (s.Last.IsAccepted)
            {
                Assert.True(listed && !imposed && open, $"A wish reroll of {s.GameTitle(run.GameId)} went through.");
            }
            else if (s.Last.Rejection!.Code is RejectionCodes.WishRerollNotListed)
            {
                Assert.False(listed);
            }
            else if (s.Last.Rejection.Code is RejectionCodes.WishRerollImposed)
            {
                Assert.True(listed && imposed);
            }
        }

        // D-325: a genre is imposed only by a kept zone filter — the swamp's Horror, with the roll inside the swamp
        foreach (var rolled in s.Last.IsAccepted ? s.Last.Events.OfType<GameRolled>() : [])
        {
            Assert.True(
                rolled.Snapshot.ImposedTags.Count == 0 || (rolled.Snapshot.Zone is not null && rolled.Snapshot.ImposedTags.SequenceEqual(["Horror"])),
                $"Imposed {rolled.Snapshot.ImposedTags} outside the swamp.");
        }
    }

    /// <summary>
    /// D-321 by the log: what the rejected run's moves brought its player closer to the finish (its own moves, the teleport
    /// its move stopped on, its corrections; by the map of the moment) and whether the player moved since. A gain of 0 or
    /// less leaves the position; a player who has not moved since goes back to the cell the run's move began on — unless
    /// that cell is closer to the finish on today's map, then he stays.
    /// </summary>
    private static void CheckRejectPosition(Scenario s, RejectProof reject, SeasonState before)
    {
        var run = before.Runs[reject.RunId];
        var was = before.Players[run.PlayerId];
        if (was.Finish is not null)
        {
            return;
        }

        MapGraph? map = null;
        var gain = 0;
        string? from = null;
        Guid? lastRun = null;
        var points = 0;
        foreach (var e in s.EffectiveLog.Take(s.EffectiveLog.Count - s.Last.Events.Count))
        {
            switch (e)
            {
                case SeasonCreated created:
                    map = created.Map;
                    break;
                case MapPublished published:
                    map = published.Map;
                    break;
                // D-327: the run's points — its dice and corrections, and the bonus of a cell its own move stopped on
                case PointsChanged p when p.PlayerId == was.PlayerId
                    && (p.RunId == run.RunId || (p.Reason == PointsReason.CellBonus && lastRun == run.RunId)):
                    points += p.Delta;
                    break;
                case PlayerMoved m when m.PlayerId == was.PlayerId:
                    var ofRun = (m.Reason is MoveReason.CompletionRoll or MoveReason.RunCorrection && m.RunId == run.RunId)
                        || (m.Reason == MoveReason.Teleport && lastRun == run.RunId);
                    if (ofRun)
                    {
                        var d = Engine.Ranking.Leaderboard.CellsToFinish(map!);
                        gain += d[m.From] - d[m.To];
                    }

                    from ??= m.Reason == MoveReason.CompletionRoll && m.RunId == run.RunId ? m.From : null;
                    lastRun = m.Reason switch
                    {
                        MoveReason.CompletionRoll => m.RunId,
                        MoveReason.Teleport => lastRun,
                        MoveReason.RunCorrection when m.RunId == lastRun => lastRun,
                        _ => null,
                    };
                    break;
            }
        }

        Assert.Equal(gain, run.Gain);
        Assert.Equal(
            -points,
            s.Last.Events.OfType<PointsChanged>().Where(p => p.PlayerId == was.PlayerId && p.Reason == PointsReason.ProofRejected).Sum(p => p.Delta));
        var now = s.State.Players[was.PlayerId].CellId;
        var distances = Engine.Ranking.Leaderboard.CellsToFinish(before.Map);
        if (gain <= 0)
        {
            Assert.True(was.CellId == now, $"A reject of a run {gain} closer moved {was.Name} from {was.CellId} to {now}.");
        }
        else if (lastRun == run.RunId && from is not null && before.Map.HasCell(from))
        {
            Assert.True(
                now == from || (now == was.CellId && distances[from] < distances[was.CellId]),
                $"{was.Name} had not moved since the rejected run's move from {from}, but the reject left him on {now}.");
        }
        else
        {
            // Moved since (or the cell before the move is gone): back along his own path by the gain, unless that is closer
            var back = Movement.Backward(before.Map, was.Path, gain);
            var target = back.Count == 0 ? was.CellId : back[^1];
            Assert.True(
                now == target || (now == was.CellId && distances[target] < distances[was.CellId]),
                $"{was.Name} moved since the rejected run's move: back {gain} leads to {target}, the reject left him on {now}.");
        }
    }

    /// <summary>The moves of one accepted command against the map it was decided on.</summary>
    private static void CheckMoves(SeasonState before, IReadOnlyList<IGameEvent> events)
    {
        var map = events.OfType<MapPublished>().LastOrDefault()?.Map ?? before.Map;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is not PlayerMoved move)
            {
                continue;
            }

            // D-322: only a run's own move reaches the finish (the steps after a branch choice are part of it)
            if (map.CellById(move.To).Type == CellType.Finish)
            {
                Assert.True(move.Reason == MoveReason.CompletionRoll && move.Steps > 0, $"A {move.Reason} move reached the finish.");
            }

            if (move.Steps != 0)
            {
                // Every step follows an arrow, forward or back
                var cells = new[] { move.From }.Concat(move.Path).ToList();
                Assert.All(cells.Zip(cells.Skip(1)), step => Assert.True(
                    map.Edges.Any(e => (e.From, e.To) == (move.Steps > 0 ? step : (step.Second, step.First))),
                    $"Step {step.First} → {step.Second} of {move.Reason} follows no arrow."));
            }

            if (move.Steps < 0)
            {
                // RR3, D-306: a move back never passes the start; a push back never leaves or passes a checkpoint
                Assert.DoesNotContain(move.Path.SkipLast(1), c => map.CellById(c).Type == CellType.Start);
                Assert.NotEqual(CellType.Start, map.CellById(move.From).Type);
                if (move.Reason == MoveReason.DropPenalty)
                {
                    Assert.NotEqual(CellType.Checkpoint, map.CellById(move.From).Type);
                    Assert.DoesNotContain(move.Path.SkipLast(1), c => map.CellById(c).Type == CellType.Checkpoint);
                }
            }

            // D-304: a paused walk ends on a fork and asks for its branch next
            if (move.Paused)
            {
                Assert.Equal(CellType.Fork, map.CellById(move.To).Type);
                Assert.Contains(events.Skip(i + 1), e => e is BranchChoiceRequested r && r.PlayerId == move.PlayerId && r.CellId == move.To);
            }

            // D-303: a teleport follows a stop of the game on a teleport cell, to its destination, and nothing fires there
            if (move.Reason == MoveReason.Teleport)
            {
                var stop = events.Take(i).OfType<PlayerMoved>().Last(m => m.PlayerId == move.PlayerId);
                Assert.True(stop.Steps != 0 && !stop.Paused && stop.Reason is MoveReason.CompletionRoll or MoveReason.DropPenalty);
                Assert.Equal(new Cell(stop.To, CellType.Teleport) { To = move.To }.To, map.CellById(stop.To).To);
                Assert.Equal(stop.To, move.From);
                Assert.False(
                    events.Skip(i + 1).FirstOrDefault(e => e is PointsChanged { Reason: PointsReason.CellBonus } or PlayerMoved { Reason: MoveReason.Teleport }) is { } fired
                        && PlayerOf(fired) == move.PlayerId,
                    "A teleport's destination fired.");
            }
        }

        // D-303: a cell bonus follows a stop of the game on a bonus cell, by its amount
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is PointsChanged { Reason: PointsReason.CellBonus } bonus)
            {
                var stop = events.Take(i).OfType<PlayerMoved>().Last(m => m.PlayerId == bonus.PlayerId);
                Assert.True(stop.Steps != 0 && !stop.Paused && stop.Reason is MoveReason.CompletionRoll or MoveReason.DropPenalty);
                Assert.Equal(map.CellById(stop.To).Amount, bonus.Delta);
            }
        }
    }

    private static Guid PlayerOf(IGameEvent e) => e switch
    {
        PointsChanged p => p.PlayerId,
        PlayerMoved m => m.PlayerId,
        _ => Guid.Empty,
    };
}

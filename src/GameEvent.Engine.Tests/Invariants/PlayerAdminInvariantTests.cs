using FsCheck.Xunit;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Invariants;

/// <summary>
/// Random seasons with the admin in the game (C2): players roll, start and complete while the admin adjusts
/// points, coins, resources and positions, discards offers, toggles the inactive flag, adds a player mid-season,
/// moves the deadline and, rarely, the season status. After every command the invariants of docs/TESTING.md
/// this can break must hold; the expected values are folded from the log independently of the engine.
/// Each byte of <c>script</c> is one command; <c>seed</c> drives the engine's random source.
/// The choice variant (C4, D-91) plays with <c>roll.choiceCount</c> 3 and adds <see cref="MakeChoice"/>, so the admin
/// discard (D-89) meets pending choices as well as offers.
/// </summary>
public class PlayerAdminInvariantTests
{
    private const int MapLength = 25;

    private static readonly string[] s_players = ["Вася", "Петя", "Маша"];
    private static readonly Guid s_late = SequentialIds.Make(0x10000000, 0x99);
    private static readonly Guid s_lateUser = SequentialIds.Make(0x40000000, 0x99);

    private const int ChoiceCount = 3;

    private static Scenario NewSeason(int seed, bool withChoice = false) =>
        Scenario.New(seed: seed)
            .WithRuleset(r => withChoice ? r with { Roll = r.Roll with { ChoiceCount = ChoiceCount } } : r)
            .WithMapLength(MapLength)
            .WithCategory("Horror", weight: 3)
            .WithGame("Silent Hill", 12, "Horror")
            .WithGame("Alan Wake", 15, "Horror")
            .WithCategory("Puzzle", weight: 2)
            .WithGame("Tetris", 2, "Puzzle")
            .WithGame("Unknown Length", null, "Puzzle")
            .WithCategory("Action", weight: 1)
            .WithGame("Doom", 4, "Action")
            .WithPlayers(s_players);

    /// <summary>
    /// Bits 0–1 pick the player (the fourth is the late one, maybe not added yet), bits 2–4 the kind of command,
    /// bits 5–7 its argument, so all three vary independently. With a choice, a start with an odd argument is
    /// MakeChoice instead: the player's pending choice (the argument picks the option, one past the last is unknown)
    /// or a made-up id.
    /// </summary>
    private static ICommand CommandFor(Scenario s, byte b, bool withChoice = false)
    {
        var index = b % 4;
        var player = index < s_players.Length ? s.PlayerId(s_players[index]) : s_late;
        var arg = b / 32;
        var comment = arg == 7 ? "" : "правка";
        if (withChoice && (b / 4) % 8 == 1 && arg % 2 == 1)
        {
            return ChoiceFor(s, player, arg / 2);
        }

        return ((b / 4) % 8) switch
        {
            0 => new RollGame(player),
            1 => new StartRun(player),
            2 => new CompleteRun(player, (Difficulty)(arg % 4), EstimatedHours: 1 + arg),
            3 => new AdjustPlayer(player, comment, PointsDelta: arg - 3),
            4 => new AdjustPlayer(player, comment, CoinsDelta: 3 - arg, ResourceDeltas: [new ResourceDelta("tickets", (arg % 3) - 1)]),
            5 => new AdjustPlayer(player, comment, CellId: CellAt(s, arg * 4), DiscardOffer: arg % 2 == 1),
            6 => new SetPlayerInactive(player, arg % 2 == 1),
            _ => arg switch
            {
                7 => new ChangeSeasonStatus(s.State.Status + 1),
                6 => new AdjustPlayer(player, comment, DiscardOffer: true),
                5 => new SetSeasonDeadline(null),
                4 => new SetSeasonDeadline(FixedClock.SeasonStart.AddDays(10 + arg)),
                _ => new AddSeasonPlayer(s_late, s_lateUser, "Лёша", CellId: CellAt(s, arg * 5), Points: arg, Coins: 2 - arg),
            },
        };
    }

    private static MakeChoice ChoiceFor(Scenario s, Guid player, int arg)
    {
        if (!s.State.Players.TryGetValue(player, out var p) || p.Choice is not { } choice)
        {
            return new MakeChoice(player, SequentialIds.Make(0x50000000, arg), "none");
        }

        var index = arg % (choice.Options.Count + 1);
        return new MakeChoice(player, choice.ChoiceId, index < choice.Options.Count ? choice.Options[index].Id : "unknown");
    }

    private static string CellAt(Scenario s, int index) => s.State.Map.Cells[Math.Min(index, s.State.Map.Cells.Count - 1)].Id;

    private static Scenario Play(
        int seed, byte[] script, Action<Scenario, ICommand, SeasonState, int>? afterEach = null, bool withChoice = false)
    {
        var s = NewSeason(seed, withChoice);
        foreach (var b in script)
        {
            var before = s.State;
            var logLength = s.Log.Count;
            var command = CommandFor(s, b, withChoice);
            s.Act(command);
            afterEach?.Invoke(s, command, before, logLength);
        }

        return s;
    }

    [Property(MaxTest = 200)]
    public void Invariants_hold_after_every_command(int seed, byte[] script) =>
        Play(seed, script, CheckInvariants);

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_a_choice_of_games(int seed, byte[] script) =>
        Play(seed, script, CheckInvariants, withChoice: true);

    [Property(MaxTest = 50)]
    public void Same_seed_and_commands_give_the_same_log_with_a_choice_of_games(int seed, byte[] script)
    {
        var first = Play(seed, script, withChoice: true);
        var second = Play(seed, script, withChoice: true);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Property(MaxTest = 50)]
    public void Same_seed_and_commands_give_the_same_log(int seed, byte[] script)
    {
        // Invariant 14 with admin commands in the mix
        var first = Play(seed, script);
        var second = Play(seed, script);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Property(MaxTest = 100)]
    public void Every_move_fires_its_entered_cells_and_a_transfer_fires_none(int seed, byte[] script) =>
        Play(seed, script, (s, _, _, _) =>
        {
            // D-90: a MoveStep per entered cell, a Stop on the last one only if the move had steps
            foreach (var moved in s.LastEvents<PlayerMoved>())
            {
                var visits = Movement.Visits(moved);
                if (moved.Steps == 0)
                {
                    Assert.Empty(visits);
                    continue;
                }

                Assert.Equal(moved.Path, visits.Where(v => v.Kind == CellVisitKind.MoveStep).Select(v => v.CellId));
                Assert.Equal(new CellVisit(moved.To, CellVisitKind.Stop), Assert.Single(visits, v => v.Kind == CellVisitKind.Stop));
                Assert.Equal(new CellVisit(moved.To, CellVisitKind.Stop), visits[^1]);
            }
        });

    private static void CheckInvariants(Scenario s, ICommand command, SeasonState before, int logLengthBefore)
    {
        // A rejected command has no events and changes nothing
        if (!s.Last.IsAccepted)
        {
            ScenarioAssert.Rejected(s, before, logLengthBefore, s.Last.Rejection!.Code);
        }
        else
        {
            CheckAcceptedCommand(s, command, before);
        }

        // 1. Replaying the log gives the stored state
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));

        var expected = Fold(s.Log);
        Assert.Equal(expected.Status, s.State.Status);
        Assert.Equal(expected.Players.Keys.Order(), s.State.Players.Keys.Order());

        foreach (var player in s.State.Players.Values)
        {
            var reference = expected.Players[player.PlayerId];

            // 2. Points and coins equal the sum of their logged changes; other resources too, without zero entries
            Assert.Equal(reference.Points, player.Points);
            Assert.Equal(reference.Coins, player.Coins);
            Assert.Equal(ResourceBag.From(reference.Resources), player.Resources);
            Assert.DoesNotContain(player.Resources, r => r.Value == 0);

            // P1. The token moves only by PlayerMoved events, each starting where the previous one ended
            Assert.Equal(reference.CellId, player.CellId);

            // 7. The token is on an existing cell
            Assert.Contains(s.State.Map.Cells, c => c.Id == player.CellId);

            // M4. The path ends where the token stands; inside a segment every pair of cells is a map edge;
            // one segment per placement (joining the season) and per transfer (Steps = 0)
            Assert.Equal(player.CellId, player.Path.Current);
            Assert.All(player.Path.Segments, segment =>
            {
                Assert.NotEmpty(segment.Cells);
                Assert.All(segment.Cells, cell => Assert.Contains(s.State.Map.Cells, c => c.Id == cell));
                Assert.All(
                    segment.Cells.Zip(segment.Cells.Skip(1)),
                    step => Assert.Contains(s.State.Map.Edges, e => e.From == step.First && e.To == step.Second));
            });
            var transfers = s.Log.OfType<PlayerMoved>().Count(e => e.PlayerId == player.PlayerId && e.Steps == 0);
            Assert.Equal(1 + transfers, player.Path.Segments.Count);

            // SE5. The flag is what the admin last set
            Assert.Equal(reference.IsInactive, player.IsInactive);

            // 3. No more active runs than allowed; the phase matches the offer or pending choice and the active run
            var playing = s.State.Runs.Values.Where(r => r.PlayerId == player.PlayerId && r.Status == RunStatus.Playing).ToList();
            Assert.True(playing.Count <= s.Ruleset.Season.MaxActiveRunsPerPlayer);
            Assert.Equal(playing.SingleOrDefault()?.RunId, player.ActiveRunId);
            Assert.Equal(player.Phase == TurnPhase.Playing, player.ActiveRunId is not null);
            Assert.Equal(player.Phase == TurnPhase.Rolling, player.Offer is not null || player.Choice is not null);
            Assert.False(player.Offer is not null && player.Choice is not null, "Both an offer and a pending choice.");
        }

        // 4 / G9. A game is busy for at most one player: offered, among pending options (D-06) or played
        // (a discarded offer or choice frees it)
        var busy = s.State.Players.Values.Where(p => p.Offer is not null).Select(p => p.Offer!.GameId)
            .Concat(s.State.Players.Values.Where(p => p.Choice is not null).SelectMany(p => p.Choice!.Options.Select(o => o.Game!.GameId)))
            .Concat(s.State.Runs.Values.Where(r => r.Status == RunStatus.Playing).Select(r => r.GameId))
            .ToList();
        Assert.Equal(busy.Count, busy.Distinct().Count());
    }

    /// <summary>Per-command rules for accepted administration commands.</summary>
    private static void CheckAcceptedCommand(Scenario s, ICommand command, SeasonState before)
    {
        var events = s.Last.Events;
        switch (command)
        {
            case AdjustPlayer adjust:
                // D-21: exactly one comment, at least one real change, every change is about this player
                var adjusted = Assert.Single(events.OfType<PlayerAdjusted>());
                Assert.Equal(adjust.PlayerId, adjusted.PlayerId);
                Assert.False(string.IsNullOrWhiteSpace(adjusted.Comment));
                Assert.True(events.Count >= 2, "An adjustment must change something.");
                Assert.All(events, e => Assert.Equal(adjust.PlayerId, PlayerOf(e)));
                Assert.All(events.OfType<PointsChanged>(), e => Assert.Equal(PointsReason.AdminAdjustment, e.Reason));
                Assert.All(events.OfType<CoinsChanged>(), e => Assert.Equal(CoinsReason.AdminAdjustment, e.Reason));
                Assert.All(events.OfType<PlayerMoved>(), e => Assert.Equal(MoveReason.AdminAdjustment, e.Reason));
                // D-89, D-91: a discard while Rolling drops exactly what was pending: the offer or the choice
                var was = before.Players[adjust.PlayerId];
                var discards = events.Where(e => e is OfferDiscarded or ChoiceDiscarded).ToList();
                if (adjust.DiscardOffer && was.Phase == TurnPhase.Rolling)
                {
                    var discarded = Assert.Single(discards);
                    IGameEvent expectedDiscard = was.Choice is { } pending
                        ? new ChoiceDiscarded(adjust.PlayerId, pending.ChoiceId)
                        : new OfferDiscarded(adjust.PlayerId, was.Offer!.GameId);
                    Assert.Equal(expectedDiscard, discarded);
                    Assert.Null(s.State.Players[adjust.PlayerId].Choice);
                    Assert.Null(s.State.Players[adjust.PlayerId].Offer);
                }
                else
                {
                    Assert.Empty(discards);
                }

                if (adjust.DiscardOffer)
                {
                    // Accepted with the flag: the player was idle or rolling, never playing, and is idle now
                    Assert.NotEqual(TurnPhase.Playing, before.Players[adjust.PlayerId].Phase);
                    Assert.Equal(TurnPhase.Idle, s.State.Players[adjust.PlayerId].Phase);
                }

                break;
            case AddSeasonPlayer add:
                Assert.Equal(new SeasonPlayerAdded(add.PlayerId, add.UserId, add.Name, s.State.Map.Start.Id), events[0]);
                Assert.All(events, e => Assert.Equal(add.PlayerId, PlayerOf(e)));
                Assert.All(events.OfType<PointsChanged>(), e => Assert.Equal(PointsReason.StartingBalance, e.Reason));
                Assert.All(events.OfType<CoinsChanged>(), e => Assert.Equal(CoinsReason.StartingBalance, e.Reason));
                Assert.All(events.OfType<PlayerMoved>(), e => Assert.Equal(MoveReason.StartingCell, e.Reason));
                break;
            case ChangeSeasonStatus change:
                Assert.Equal([new SeasonStatusChanged(before.Status, change.To)], events);
                Assert.Equal(before.Status + 1, change.To);
                break;
            case SetPlayerInactive inactive:
                Assert.Equal([new PlayerInactivitySet(inactive.PlayerId, inactive.IsInactive)], events);
                break;
            case SetSeasonDeadline deadline:
                Assert.Equal([new SeasonDeadlineSet(deadline.Deadline)], events);
                break;
            case RollGame or StartRun or CompleteRun:
                // Game actions only in a running season
                Assert.Equal(SeasonStatus.Active, before.Status);
                break;
            case MakeChoice choose:
                // Choosing --> Playing (D-91): the chosen option starts at once with its roll-time snapshot
                Assert.Equal(SeasonStatus.Active, before.Status);
                var option = before.Players[choose.PlayerId].Choice!.Options.Single(o => o.Id == choose.OptionId).Game!;
                Assert.Equal(2, events.Count);
                Assert.Equal(new ChoiceMade(choose.PlayerId, choose.ChoiceId, choose.OptionId), events[0]);
                var started = Assert.IsType<RunStarted>(events[1]);
                Assert.Equal(
                    new RunStarted(started.RunId, choose.PlayerId, option.GameId, option.Snapshot, option.RolledAt, s.Clock.UtcNow),
                    started);
                Assert.Equal(TurnPhase.Playing, s.State.Players[choose.PlayerId].Phase);
                Assert.Null(s.State.Players[choose.PlayerId].Choice);
                break;
        }
    }

    private static Guid? PlayerOf(IGameEvent e) =>
        e switch
        {
            SeasonPlayerAdded x => x.PlayerId,
            PlayerAdjusted x => x.PlayerId,
            OfferDiscarded x => x.PlayerId,
            ChoiceDiscarded x => x.PlayerId,
            GameRolled x => x.PlayerId,
            GameChoiceRolled x => x.PlayerId,
            ChoiceMade x => x.PlayerId,
            RunStarted x => x.PlayerId,
            PointsChanged x => x.PlayerId,
            CoinsChanged x => x.PlayerId,
            ResourceChanged x => x.PlayerId,
            PlayerMoved x => x.PlayerId,
            PlayerInactivitySet x => x.PlayerId,
            _ => null,
        };

    private sealed class ReferencePlayer
    {
        public required string CellId { get; set; }

        public int Points { get; set; }

        public int Coins { get; set; }

        public Dictionary<string, int> Resources { get; } = new(StringComparer.Ordinal);

        public bool IsInactive { get; set; }
    }

    private sealed record Reference(SeasonStatus Status, Dictionary<Guid, ReferencePlayer> Players);

    /// <summary>Folds the log the way the rules say, independently of the engine's Apply, checking each event on the way.</summary>
    private static Reference Fold(IEnumerable<IGameEvent> log)
    {
        var status = SeasonStatus.Draft;
        var players = new Dictionary<Guid, ReferencePlayer>();
        foreach (var e in log)
        {
            switch (e)
            {
                case SeasonStatusChanged changed:
                    Assert.Equal(status, changed.From);
                    Assert.Equal(status + 1, changed.To);
                    status = changed.To;
                    break;
                case SeasonPlayerAdded added:
                    Assert.True(status is SeasonStatus.Draft or SeasonStatus.Active, $"Player added while {status}.");
                    Assert.DoesNotContain(added.PlayerId, players.Keys);
                    players[added.PlayerId] = new ReferencePlayer { CellId = added.CellId };
                    break;
                case GameRolled or GameChoiceRolled or ChoiceMade or RunStarted or RunCompleted or CompletionRolled:
                    // SE1/SE2: no game actions outside a running season
                    Assert.Equal(SeasonStatus.Active, status);
                    break;
                case PointsChanged points:
                    Assert.True(points.Reason == PointsReason.CompletionRoll || points.Delta != 0, "Zero changes are not logged.");
                    players[points.PlayerId].Points += points.Delta;
                    break;
                case CoinsChanged coins:
                    Assert.NotEqual(0, coins.Delta);
                    players[coins.PlayerId].Coins += coins.Delta;
                    break;
                case ResourceChanged resource:
                    Assert.NotEqual(0, resource.Delta);
                    var bag = players[resource.PlayerId].Resources;
                    bag[resource.Resource] = bag.GetValueOrDefault(resource.Resource) + resource.Delta;
                    break;
                case PlayerMoved moved:
                    var player = players[moved.PlayerId];
                    Assert.Equal(player.CellId, moved.From);
                    Assert.Equal(moved.To, moved.Path[^1]);
                    if (moved.Reason is MoveReason.AdminAdjustment or MoveReason.StartingCell)
                    {
                        // A transfer, not steps; and never a no-op
                        Assert.Equal([moved.To], moved.Path);
                        Assert.NotEqual(moved.From, moved.To);
                    }

                    player.CellId = moved.To;
                    break;
                case PlayerInactivitySet inactive:
                    Assert.NotEqual(players[inactive.PlayerId].IsInactive, inactive.IsInactive);
                    players[inactive.PlayerId].IsInactive = inactive.IsInactive;
                    break;
            }
        }

        return new Reference(status, players);
    }
}

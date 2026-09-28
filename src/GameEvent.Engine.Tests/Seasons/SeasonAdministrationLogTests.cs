using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Seasons;

/// <summary>
/// The season and player administration events (C2) go through the storage format and replay into the same
/// state (L4, L6 groundwork, invariant 1).
/// </summary>
public class SeasonAdministrationLogTests
{
    private static readonly Guid s_late = SequentialIds.Make(0x10000000, 0x50);

    /// <summary>A season that uses every administration event: lifecycle, deadline, late player, flag, adjustments.</summary>
    private static Scenario AdministeredSeason()
    {
        var s = Scenario.New().AsDraft()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithGame("Alan Wake", 15, "Horror")
            .WithPlayers("Вася", "Петя");

        Accept(s, new SetSeasonDeadline(FixedClock.SeasonStart.AddDays(30)));
        Accept(s, new ChangeSeasonStatus(SeasonStatus.Active));
        Accept(s, new AddSeasonPlayer(s_late, SequentialIds.Make(0x40000000, 0x50), "Лёша", CellId: "c5", Points: 12, Coins: 7));
        s.Roll("Вася");
        s.Roll("Петя").Start("Петя").Complete("Петя");
        Accept(s, new AdjustPlayer(
            s.PlayerId("Вася"), "Сбой колеса", CellId: "c3", PointsDelta: 4, CoinsDelta: -1,
            ResourceDeltas: [new ResourceDelta("tickets", 2)], DiscardOffer: true));
        Accept(s, new SetPlayerInactive(s.PlayerId("Петя"), true));
        Accept(s, new SetSeasonDeadline(FixedClock.SeasonStart.AddDays(40)));
        Accept(s, new ChangeSeasonStatus(SeasonStatus.Closing));
        return s;
    }

    private static void Accept(Scenario s, ICommand command)
    {
        s.Act(command);
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Every_administration_event_survives_the_json_round_trip()
    {
        var s = AdministeredSeason();

        // The log covers every new event type and every new reason
        Assert.Contains(s.Log, e => e is SeasonStatusChanged);
        Assert.Contains(s.Log, e => e is SeasonDeadlineSet);
        Assert.Contains(s.Log, e => e is PlayerInactivitySet);
        Assert.Contains(s.Log, e => e is PlayerAdjusted);
        Assert.Contains(s.Log, e => e is OfferDiscarded);
        Assert.Contains(s.Log, e => e is ResourceChanged);
        Assert.Contains(s.Log, e => e is PointsChanged { Reason: PointsReason.StartingBalance });
        Assert.Contains(s.Log, e => e is PointsChanged { Reason: PointsReason.AdminAdjustment });
        Assert.Contains(s.Log, e => e is CoinsChanged { Reason: CoinsReason.StartingBalance });
        Assert.Contains(s.Log, e => e is CoinsChanged { Reason: CoinsReason.AdminAdjustment });
        Assert.Contains(s.Log, e => e is PlayerMoved { Reason: MoveReason.StartingCell });
        Assert.Contains(s.Log, e => e is PlayerMoved { Reason: MoveReason.AdminAdjustment });

        Assert.All(s.Log, e =>
        {
            var stored = EventCodec.Encode(e);
            var back = EventCodec.Decode(stored);
            Assert.Equal(e, back);
            Assert.Equal(stored, EventCodec.Encode(back));
        });
    }

    [Fact]
    public void Replaying_the_stored_log_gives_the_same_state()
    {
        var s = AdministeredSeason();

        var replayed = SeasonEngine.Replay(s.Log.Select(EventCodec.Encode).Select(EventCodec.Decode));

        Assert.Equal(s.State, replayed);
        Assert.Equal(SeasonStatus.Closing, replayed.Status);
        Assert.Equal(FixedClock.SeasonStart.AddDays(40), replayed.Deadline);
        Assert.Equal(("c5", 12, 7), (replayed.Players[s_late].CellId, replayed.Players[s_late].Points, replayed.Players[s_late].Coins));
        Assert.True(replayed.Players[s.PlayerId("Петя")].IsInactive);
        Assert.Equal(2, replayed.Players[s.PlayerId("Вася")].Resources["tickets"]);
    }

    [Fact]
    public void Replaying_every_prefix_matches_the_state_after_each_command()
    {
        var checkpoints = new List<(int LogLength, SeasonState State)>();
        var s = Scenario.New().AsDraft()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
            .WithPlayers("Вася");
        checkpoints.Add((s.Log.Count, s.State));

        ICommand[] commands =
        [
            new SetSeasonDeadline(FixedClock.SeasonStart.AddDays(30)),
            new ChangeSeasonStatus(SeasonStatus.Active),
            new AddSeasonPlayer(s_late, SequentialIds.Make(0x40000000, 0x50), "Лёша", CellId: "c2", Points: 3, Coins: 1),
            new SetPlayerInactive(s_late, true),
            new AdjustPlayer(s_late, "Правка", CellId: "c7", PointsDelta: -5, ResourceDeltas: [new ResourceDelta("keys", 1)]),
            new SetPlayerInactive(s_late, false),
            new ChangeSeasonStatus(SeasonStatus.Closing),
            new ChangeSeasonStatus(SeasonStatus.Finished),
            new ChangeSeasonStatus(SeasonStatus.Archived),
        ];
        foreach (var command in commands)
        {
            Accept(s, command);
            checkpoints.Add((s.Log.Count, s.State));
        }

        Assert.All(checkpoints, c => Assert.Equal(c.State, SeasonEngine.Replay(s.Log.Take(c.LogLength))));
    }
}

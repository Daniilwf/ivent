using GameEvent.Engine.Finish;
using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Finish.FinishSetup;

namespace GameEvent.Engine.Tests.Finish;

/// <summary>
/// When a finish is revoked (SE7, P3, P6; SPEC «Если первое место освободилось после реджекта … Первым становится
/// следующий финишировавший, его бонус снимается»; Q-4; the freeze amendment; D-99): the revoked player's bonus goes, and
/// the bonuses of ALL standing finishers are recalculated by their new places with difference events —
/// <c>PointsChanged(+, FinishBonus)</c> or <c>PointsChanged(−, FinishBonusRevoked)</c>. If the first was revoked, the next
/// finisher by order becomes first: his bonus goes, and he is frozen at once if all his runs up to the finish are already
/// approved, otherwise when they are. What he gained between his finish and the freeze stays.
/// </summary>
public class FirstPlaceReassignTests
{
    /// <summary>Вася 1st, Петя 2nd (+10), Маша 3rd (+8); returns their finishing runs.</summary>
    private static (Scenario S, Guid Vasya, Guid Petya, Guid Masha) ThreeFinished()
    {
        var s = New();
        var vasya = FinishRun(s, "Вася");
        var petya = FinishRun(s, "Петя");
        var masha = FinishRun(s, "Маша");
        Assert.Equal((4, 14, 12), (s.Player("Вася").Points, s.Player("Петя").Points, s.Player("Маша").Points));
        return (s, vasya, petya, masha);
    }

    private static List<(Guid, int, PointsReason)> Sorted(Scenario s) => [.. BonusChanges(s).OrderByDescending(x => x.Delta)];

    [Fact]
    public void Next_finisher_becomes_first_and_the_bonuses_move_up()
    {
        var (s, vasyaRun, _, _) = ThreeFinished();
        var (vasya, petya, masha) = (s.PlayerId("Вася"), s.PlayerId("Петя"), s.PlayerId("Маша"));

        Reject(s, vasyaRun);

        // Вася's finish is revoked; Петя is first (10 → 0), Маша second (8 → 10), after the revoke
        ScenarioAssert.Accepted(s);
        Assert.Equal(new PlayerFinishRevoked(vasya, vasyaRun), Assert.Single(s.LastEvents<PlayerFinishRevoked>()));
        Assert.Equal([(masha, 2, PointsReason.FinishBonus), (petya, -10, PointsReason.FinishBonusRevoked)], Sorted(s));
        Assert.All(
            s.LastEvents<PointsChanged>().Where(e => e.Reason is PointsReason.FinishBonus or PointsReason.FinishBonusRevoked),
            e => Assert.True(s.Last.Events.ToList().IndexOf(e) > IndexOf<PlayerFinishRevoked>(s), "Bonuses are recalculated after the revoke."));
        Assert.Equal(petya, FinishLine.First(s.State));
        Assert.Equal((4, 14), (s.Player("Петя").Points, s.Player("Маша").Points));
        Assert.Equal((0, 10), (FinishOf(s, "Петя")!.Bonus, FinishOf(s, "Маша")!.Bonus));

        // Orders stay as they were: Петя 2, Маша 3; Петя's runs are not approved, so he is not frozen yet
        Assert.Equal((2, 3), (FinishOf(s, "Петя")!.Order, FinishOf(s, "Маша")!.Order));
        Assert.Empty(s.LastEvents<PlayerFrozen>());
        Assert.False(FinishOf(s, "Петя")!.Frozen);
    }

    [Fact]
    public void Third_finisher_moving_to_second_gets_the_second_place_bonus()
    {
        // Маша finished third (8); after Петя, second, is revoked she is second: 10
        var (s, _, petyaRun, _) = ThreeFinished();
        var masha = s.PlayerId("Маша");

        Reject(s, petyaRun);

        ScenarioAssert.Accepted(s);
        Assert.Contains((masha, 2, PointsReason.FinishBonus), BonusChanges(s));
        Assert.Equal((10, 14), (FinishOf(s, "Маша")!.Bonus, s.Player("Маша").Points));
    }

    [Fact]
    public void Next_first_with_all_runs_approved_is_frozen_at_once()
    {
        var (s, vasyaRun, petyaRun, _) = ThreeFinished();
        var petya = s.PlayerId("Петя");
        Approve(s, petyaRun);
        ScenarioAssert.Accepted(s);
        Assert.False(FinishOf(s, "Петя")!.Frozen);

        Reject(s, vasyaRun);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PlayerFrozen(petya), Assert.Single(s.LastEvents<PlayerFrozen>()));
        Assert.True(IndexOf<PlayerFrozen>(s) > IndexOf<PlayerFinishRevoked>(s), "The freeze follows the revoke.");
        Assert.True(FinishOf(s, "Петя")!.Frozen);

        // Frozen after his bonus was taken back
        Assert.Equal((4, 0), (s.Player("Петя").Points, FinishOf(s, "Петя")!.Bonus));
    }

    [Fact]
    public void Next_first_with_an_earlier_run_unchecked_is_not_frozen_at_once()
    {
        // Петя: A 1 + 1, B 1 + 1 → second; only B is approved — A still waits, so no freeze when he becomes first
        var s = New();
        var vasyaRun = FinishRun(s, "Вася");
        var (petyaA, _) = Complete(s, "Петя", [1, 1]);
        var (petyaB, _) = Complete(s, "Петя", [1, 1]);
        Approve(s, petyaB);

        Reject(s, vasyaRun);

        ScenarioAssert.Accepted(s);
        Assert.Equal(s.PlayerId("Петя"), FinishLine.First(s.State));
        Assert.Empty(s.LastEvents<PlayerFrozen>());

        // Then the approval of A completes the set and freezes him
        Approve(s, petyaA);
        ScenarioAssert.Accepted(s);
        Assert.Equal(new PlayerFrozen(s.PlayerId("Петя")), s.Last.Events[^1]);
    }

    [Fact]
    public void Next_first_is_frozen_when_his_runs_are_approved()
    {
        var (s, vasyaRun, petyaRun, _) = ThreeFinished();
        var petya = s.PlayerId("Петя");
        Reject(s, vasyaRun);
        ScenarioAssert.Accepted(s);

        Approve(s, petyaRun);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [new ProofApproved(petyaRun, petya, true, "Видел на стриме", s.Clock.UtcNow), new PlayerFrozen(petya)],
            s.Last.Events);
        Assert.True(FinishOf(s, "Петя")!.Frozen);
    }

    [Fact]
    public void Points_gained_between_the_finish_and_the_freeze_stay()
    {
        // Петя: 4 + 10 at the finish, +4 by a later run = 18; his bonus goes (8); nothing else is restored or taken
        var (s, vasyaRun, petyaRun, _) = ThreeFinished();
        Complete(s, "Петя", [2, 2]);
        Assert.Equal(18, s.Player("Петя").Points);

        Reject(s, vasyaRun);
        Approve(s, petyaRun);
        ScenarioAssert.Accepted(s);

        Assert.Equal(8, s.Player("Петя").Points);
        Assert.True(FinishOf(s, "Петя")!.Frozen);
    }

    [Fact]
    public void New_first_in_free_mode_does_not_move()
    {
        var (s, vasyaRun, _, _) = ThreeFinished();
        Reject(s, vasyaRun);
        ScenarioAssert.Accepted(s);

        Complete(s, "Петя", [2, 2]);

        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal(4, s.LastEvents<PointsChanged>().Single().Delta);
    }

    [Fact]
    public void Rejecting_a_later_finisher_revokes_his_finish_and_bonus()
    {
        var (s, _, petyaRun, _) = ThreeFinished();
        var (petya, masha) = (s.PlayerId("Петя"), s.PlayerId("Маша"));

        Reject(s, petyaRun);

        // The usual reject of his run, then his finish and his bonus; Маша moves up (8 → 10)
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new PointsChanged(petya, -4, PointsReason.ProofRejected, petyaRun),
            s.LastEvents<PointsChanged>().Single(e => e.Reason == PointsReason.ProofRejected));
        Assert.Equal(LinearMap.StartId, s.LastEvents<PlayerMoved>().Single().To);
        Assert.Equal(new PlayerFinishRevoked(petya, petyaRun), Assert.Single(s.LastEvents<PlayerFinishRevoked>()));
        Assert.Equal([(masha, 2, PointsReason.FinishBonus), (petya, -10, PointsReason.FinishBonusRevoked)], Sorted(s));
        Assert.Empty(s.LastEvents<PlayerFrozen>());

        // Вася stays first
        Assert.Equal(s.PlayerId("Вася"), FinishLine.First(s.State));
        Assert.Equal((0, LinearMap.StartId), (s.Player("Петя").Points, s.Player("Петя").CellId));
        Assert.Null(FinishOf(s, "Петя"));
        Assert.Equal((14, 3), (s.Player("Маша").Points, FinishOf(s, "Маша")!.Order));
    }

    [Fact]
    public void Orders_are_never_reused_and_the_bonus_follows_the_place()
    {
        // Петя (2) is revoked and finishes again: order 4, place 3 behind Вася and Маша — 8
        var (s, _, petyaRun, _) = ThreeFinished();
        var petya = s.PlayerId("Петя");
        Reject(s, petyaRun);
        ScenarioAssert.Accepted(s);

        var (again, at) = Complete(s, "Петя", [3, 1]);

        Assert.Equal(new PlayerFinished(petya, again, 4, at, 0), Assert.Single(s.LastEvents<PlayerFinished>()));
        Assert.Equal([(petya, 8, PointsReason.FinishBonus)], BonusChanges(s));
        Assert.Equal((10, 8), (FinishOf(s, "Маша")!.Bonus, FinishOf(s, "Петя")!.Bonus));
    }

    [Fact]
    public void Each_player_holds_one_bonus_when_the_first_place_moves_twice()
    {
        // Вася revoked → Петя first (10 → 0), Маша second (8 → 10); Петя revoked → Маша first (10 → 0)
        var (s, vasyaRun, petyaRun, _) = ThreeFinished();
        var (petya, masha) = (s.PlayerId("Петя"), s.PlayerId("Маша"));
        Reject(s, vasyaRun);
        ScenarioAssert.Accepted(s);

        Reject(s, petyaRun);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PlayerFinishRevoked(petya, petyaRun), Assert.Single(s.LastEvents<PlayerFinishRevoked>()));
        Assert.Equal([(masha, -10, PointsReason.FinishBonusRevoked)], BonusChanges(s));
        Assert.Equal(masha, FinishLine.First(s.State));
        Assert.Equal((0, 4), (s.Player("Петя").Points, s.Player("Маша").Points));
        Assert.Equal(0, FinishOf(s, "Маша")!.Bonus);
    }

    [Fact]
    public void First_place_moves_to_the_next_by_order_not_by_points()
    {
        // Маша has more points than Петя, but Петя finished before her
        var (s, vasyaRun, _, _) = ThreeFinished();
        Complete(s, "Маша", [4, 4]);
        Complete(s, "Маша", [4, 4]);
        Assert.True(s.Player("Маша").Points > s.Player("Петя").Points);

        Reject(s, vasyaRun);

        Assert.Equal(s.PlayerId("Петя"), FinishLine.First(s.State));
    }
}

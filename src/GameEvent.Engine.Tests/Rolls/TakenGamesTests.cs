using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Tests.Finish;
using GameEvent.Engine.Tests.Lifecycle;
using GameEvent.Engine.Tests.Proofs;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rolls;

/// <summary>
/// H6: the pool page shows each game's status in the season (SPEC «Статусы игры в сезоне»): «Уже прошёл Вася» and
/// «Сейчас играет Вася» come from the same predicate the roll uses, so the page never calls a game free that the wheel
/// would miss. Offered games and pending options are reserved — shown as being played. Dropped, tech-rerolled and
/// rejected runs leave the game free.
/// </summary>
public class TakenGamesTests
{
    private static Scenario Pool() =>
        Scenario.New()
            .WithCategory("Horror")
            .WithGame("Silent Hill", 12, "Horror")
            .WithGame("Alan Wake", 15, "Horror")
            .WithGame("Dead Space", 9, "Horror")
            .WithPlayers("Вася", "Петя", "Маша");

    private static IReadOnlyList<RollMiss> Taken(Scenario s) => PoolStats.Taken(s.State, Guid.Empty);

    [Fact]
    public void Missing_state_is_an_argument_error()
    {
        Assert.Throws<ArgumentNullException>(() => PoolStats.Taken(null!, Guid.Empty));
    }

    [Fact]
    public void Nothing_is_taken_before_the_first_roll()
    {
        Assert.Empty(Taken(Pool()));
    }

    [Fact]
    public void Offered_game_is_being_played_by_the_roller()
    {
        var s = Pool().Roll("Вася");
        var offered = s.Player("Вася").Offer!.GameId;

        Assert.Equal([new RollMiss(offered, RollMissReason.BeingPlayed, s.PlayerId("Вася"))], Taken(s));
    }

    [Fact]
    public void Started_game_is_being_played_and_completed_game_is_completed()
    {
        var s = Pool().Roll("Вася").Start("Вася");
        var first = s.Player("Вася").ActiveRunId is { } run ? s.State.Runs[run].GameId : Guid.Empty;
        Assert.Equal([new RollMiss(first, RollMissReason.BeingPlayed, s.PlayerId("Вася"))], Taken(s));

        s.Complete("Вася").Roll("Петя").Start("Петя");
        var second = s.State.Runs[s.Player("Петя").ActiveRunId!.Value].GameId;

        Assert.Equal(
            new[]
            {
                new RollMiss(first, RollMissReason.CompletedInSeason, s.PlayerId("Вася")),
                new RollMiss(second, RollMissReason.BeingPlayed, s.PlayerId("Петя")),
            }.OrderBy(m => m.GameId),
            Taken(s));
    }

    [Fact]
    public void Options_of_a_pending_choice_are_being_played()
    {
        var s = Scenario.New()
            .WithRuleset(r => r with { Roll = r.Roll with { ChoiceCount = 2 } })
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithGame("Alan Wake", 15, "Horror").WithGame("Dead Space", 9, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася");

        var options = s.Player("Вася").Choice!.Options.Select(o => o.Game!.GameId).Order().ToList();

        Assert.Equal(options.Select(g => new RollMiss(g, RollMissReason.BeingPlayed, s.PlayerId("Вася"))), Taken(s));
    }

    [Fact]
    public void Dropped_game_is_free_again()
    {
        var s = LifecycleSetup.New();
        s.Roll("Вася").Start("Вася");
        s.Act(new DropRun(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);

        Assert.Empty(Taken(s));
    }

    [Fact]
    public void Tech_rerolled_game_is_free_again()
    {
        var s = LifecycleSetup.New();
        s.Roll("Вася").Start("Вася");
        var game = s.State.Runs[s.Player("Вася").ActiveRunId!.Value].GameId;

        s.Act(new TechReroll(s.PlayerId("Вася"), TechRerollReason.DoesNotLaunch, null));
        ScenarioAssert.Accepted(s);

        // The tech reroll rolls again: whatever is taken now, it is not the game he gave up
        Assert.DoesNotContain(Taken(s), m => m.GameId == game);
    }

    [Fact]
    public void Rejected_game_is_free_again()
    {
        var (s, run) = ProofSetup.Completed([2, 2]);
        Assert.Equal(RollMissReason.CompletedInSeason, Assert.Single(Taken(s)).Reason);

        ProofSetup.Reject(s, run);
        ScenarioAssert.Accepted(s);

        Assert.Empty(Taken(s));
    }

    [Fact]
    public void Game_the_first_completed_in_free_mode_is_completed_for_him_alone()
    {
        // D-16: Вася finished first and completed another game in free mode; the wheel misses it for him only
        var s = FinishSetup.New(players: 2, games: 3);
        FinishSetup.FrozenFirst(s, "Вася");
        var (freeRun, _) = FinishSetup.Complete(s, "Вася", [2, 2]);
        var game = s.State.Runs[freeRun].GameId;

        Assert.Contains(
            new RollMiss(game, RollMissReason.CompletedInSeason, s.PlayerId("Вася")),
            PoolStats.Taken(s.State, s.PlayerId("Вася")));
        Assert.DoesNotContain(PoolStats.Taken(s.State, s.PlayerId("Петя")), m => m.GameId == game);
        Assert.DoesNotContain(Taken(s), m => m.GameId == game);
    }

    [Fact]
    public void Taken_does_not_change_the_state()
    {
        var s = Pool().Roll("Вася");
        var before = s.State;

        Taken(s);

        Assert.Equal(before, s.State);
    }
}

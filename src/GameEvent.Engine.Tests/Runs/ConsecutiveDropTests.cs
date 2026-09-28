using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Runs;

/// <summary>
/// Drops in a row (the owner's decision D-205, RGG 1; D-324): every further drop in a row adds
/// <c>drop.consecutiveExtraDice</c> dice of the penalty's own kind. Only a counted completion — completed and not
/// rejected — ends the streak; a tech reroll neither adds to it nor ends it; a rejected completion no longer ends it. The
/// streak of a run is the drops rolled before it since the last counted completion, so an undo or a reject needs no
/// bookkeeping; penalties already thrown stay as logged. A tech reroll turned into a drop counts where it was rolled. Without
/// the field (a season logged before it) the penalty never grows. Pinned test ruleset: penalty 2d4; every game 12 hours.
/// </summary>
public class ConsecutiveDropTests
{
    private static readonly string[] s_games = ["Silent Hill", "Alan Wake", "Dead Space", "Outlast", "Soma", "Amnesia", "Kuon", "Siren"];

    private static Scenario Season(int? extra = 1)
    {
        var s = Scenario.New().WithRuleset(r => r with { Drop = r.Drop with { ConsecutiveExtraDice = extra } }).WithCategory("Horror");
        foreach (var game in s_games)
        {
            s.WithGame(game, 12, "Horror");
        }

        return s.WithPlayers("Вася", "Петя");
    }

    /// <summary>Вася rolls, starts and drops; returns how many penalty dice the drop threw.</summary>
    private static int Drop(Scenario s)
    {
        s.Roll("Вася").Start("Вася");
        return DropActive(s);
    }

    private static int DropActive(Scenario s)
    {
        s.Advance(TimeSpan.FromHours(2)).Act(new DropRun(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(1));
        return Assert.Single(s.LastEvents<RunDropped>()).PenaltyDice.Count;
    }

    private static Guid Complete(Scenario s)
    {
        s.Roll("Вася").Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.NextRandom(2, 2, 2, 2).Complete("Вася");
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(1));
        return runId;
    }

    [Fact]
    public void Every_further_drop_in_a_row_adds_the_extra_dice()
    {
        var s = Season();

        Assert.Equal([2, 3, 4], new[] { Drop(s), Drop(s), Drop(s) });

        // The extra dice are the penalty's own kind
        Assert.All(s.LastEvents<RunDropped>().Single().PenaltyDice, d => Assert.Equal(4, d.Sides));
    }

    [Fact]
    public void Extra_dice_follow_the_setting()
    {
        var s = Season(extra: 2);

        Assert.Equal([2, 4, 6], new[] { Drop(s), Drop(s), Drop(s) });
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void Without_extra_dice_the_penalty_never_grows(int? extra)
    {
        var s = Season(extra);

        Assert.Equal([2, 2, 2], new[] { Drop(s), Drop(s), Drop(s) });
    }

    [Fact]
    public void Counted_completion_ends_the_streak()
    {
        var s = Season();
        Drop(s);
        Drop(s);

        Complete(s);

        Assert.Equal(2, Drop(s));
    }

    [Fact]
    public void Tech_reroll_neither_adds_to_the_streak_nor_ends_it()
    {
        var s = Season();
        Drop(s);

        // The tech reroll rolls a new game at once: Вася starts it and drops it
        s.Roll("Вася").Start("Вася").Act(new TechReroll(s.PlayerId("Вася"), TechRerollReason.DoesNotLaunch, null));
        ScenarioAssert.Accepted(s);
        s.Start("Вася");

        Assert.Equal(3, DropActive(s));
    }

    [Fact]
    public void Tech_reroll_first_is_not_a_drop()
    {
        var s = Season();
        s.Roll("Вася").Start("Вася").Act(new TechReroll(s.PlayerId("Вася"), TechRerollReason.WeakPc, null));
        ScenarioAssert.Accepted(s);
        s.Start("Вася");

        Assert.Equal(2, DropActive(s));
    }

    [Fact]
    public void Rejected_completion_no_longer_ends_the_streak()
    {
        // A rejected run is not counted: the drops before and after it are in a row
        var s = Season();
        Drop(s);
        var run = Complete(s);
        s.Act(new RejectProof(run, "Не та игра"));
        ScenarioAssert.Accepted(s);

        Assert.Equal(3, Drop(s));
    }

    [Fact]
    public void Penalties_already_thrown_stay_when_a_completion_between_is_rejected_later()
    {
        var s = Season();
        Drop(s);
        var run = Complete(s);
        Drop(s);
        var pointsBefore = s.Player("Вася").Points;

        s.Act(new RejectProof(run, "Не та игра"));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<RunDropped>());
        Assert.Equal(pointsBefore - 8, s.Player("Вася").Points);
    }

    [Fact]
    public void Tech_reroll_turned_into_a_drop_counts_where_it_was_rolled()
    {
        // Drop, then a tech reroll: turned into a drop, it follows one drop in a row — 3 dice; the next drop follows two
        var s = Season();
        Drop(s);
        s.Roll("Вася").Start("Вася");
        var rerolled = s.Player("Вася").ActiveRunId!.Value;
        s.Act(new TechReroll(s.PlayerId("Вася"), TechRerollReason.DoesNotLaunch, null));
        ScenarioAssert.Accepted(s);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Сброс", DiscardOffer: true));
        ScenarioAssert.Accepted(s);

        s.Act(new ConvertTechRerollToDrop(rerolled, "Игра запускалась"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(3, Assert.Single(s.LastEvents<TechRerollConvertedToDrop>()).PenaltyDice.Count);
        Assert.Equal(4, Drop(s));
    }

    [Fact]
    public void Undone_drop_leaves_the_streak()
    {
        var s = Season();
        Drop(s);
        Drop(s);
        var second = s.History[^1].CommandId;

        s.Act(new UndoCommand(second, "Ошибка"));
        ScenarioAssert.Accepted(s);

        // The run is being played again: dropping it once more follows one drop
        Assert.Equal(3, DropActive(s));
    }

    [Fact]
    public void Other_players_drops_do_not_count()
    {
        var s = Season();
        s.Roll("Петя").Start("Петя").Advance(TimeSpan.FromHours(2)).NextRandom(1, 1).Act(new DropRun(s.PlayerId("Петя")));
        ScenarioAssert.Accepted(s);

        Assert.Equal(2, Drop(s));
    }
}

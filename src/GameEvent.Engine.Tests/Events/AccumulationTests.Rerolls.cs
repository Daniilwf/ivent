using GameEvent.Engine.Inventory;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// D-583 at the other ways into a roll (review of 5.4): a reroll and «Уже проходил» give up games, so the events' filters of
/// the roll in progress are fitted again against what is left, and one that fits nothing now waits for the next roll
/// instead of leaving the roll empty or being lost; the paid reroll's bad event fits the same way (D-546 (3)).
/// </summary>
public partial class AccumulationTests
{
    private static Ruleset OneFreeReroll(Ruleset r) => r with { Roll = r.Roll with { FreeRerollsPerRoll = 1 } };

    private static Ruleset BadEventRerolls(Ruleset r) =>
        r with { Roll = r.Roll with { FreeRerollsPerRoll = 0, RerollCost = new RerollCost { Kind = RerollCostKind.BadEvent } } };

    /// <summary>One horror game, Silent Hill; Вася has the bad «only horror» and is offered Silent Hill.</summary>
    private static Scenario OfferedTheOnlyHorror(Func<Ruleset, Ruleset> rules)
    {
        var s = Season(rules, horror: false);
        s.WithGame("Silent Hill", 3, "Horror");
        Draw(s, "Вася", EventKind.Bad, "horror-filter");
        s.Roll("Вася");
        Assert.Equal(s.GameId("Silent Hill"), s.Player("Вася").Offer!.GameId);
        Assert.Equal(new[] { "horror-filter" }, s.Player("Вася").Wallet.CurrentRoll.Select(m => m.ObjectId));
        return s;
    }

    [Fact]
    public void A_reroll_giving_up_the_only_fitting_game_lets_the_event_filter_wait()
    {
        var s = OfferedTheOnlyHorror(OneFreeReroll);

        s.Act(new Reroll(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        var refit = Applied(s);
        Assert.Empty(refit.Modifiers);
        Assert.Equal(new (string, EventKind?)[] { ("horror-filter", EventKind.Bad) }, Kinds(refit.Waiting));
        Assert.Contains("RPG", Tags(s, "Вася"));
        Assert.Equal(new[] { "horror-filter" }, s.Player("Вася").Wallet.NextRoll.Select(m => m.ObjectId));

        // It waits for a roll where it fits again
        Plays(s, "Вася").Roll("Вася");
        Assert.Contains(Applied(s).Modifiers, m => m.ObjectId == "horror-filter");
        Assert.Equal(s.GameId("Silent Hill"), s.Player("Вася").Offer!.GameId);
    }

    [Fact]
    public void Already_played_on_the_only_fitting_game_lets_the_event_filter_wait()
    {
        var s = OfferedTheOnlyHorror(r => r);

        s.Act(new DeclareAlreadyPlayed(s.PlayerId("Вася"), s.GameId("Silent Hill")));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new (string, EventKind?)[] { ("horror-filter", EventKind.Bad) }, Kinds(Applied(s).Waiting));
        Assert.Contains("RPG", Tags(s, "Вася"));
        Assert.Equal(new[] { "horror-filter" }, s.Player("Вася").Wallet.NextRoll.Select(m => m.ObjectId));
    }

    [Fact]
    public void A_reroll_that_leaves_the_event_filter_a_game_changes_nothing_in_the_log()
    {
        // Two horror games: the reroll gives up one, the filter still fits the other — no refit is written
        var s = Season(OneFreeReroll);
        Draw(s, "Вася", EventKind.Bad, "horror-filter");
        s.Roll("Вася");

        s.Act(new Reroll(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<RollModifiersApplied>());
        Assert.Contains("Horror", Tags(s, "Вася"));
    }

    [Fact]
    public void The_bad_event_of_a_paid_reroll_that_fits_no_game_waits_and_the_roll_goes_on()
    {
        // No horror at all: the price «only horror» goes after the waiting changes; the reroll is not refused
        var s = Season(BadEventRerolls, horror: false);
        s.Roll("Вася");

        s.NextRandom(Array.IndexOf(s_deck, "horror-filter")).Act(new Reroll(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Equal("horror-filter", Assert.Single(s.LastEvents<GameEvent.Engine.Decks.EventDrawn>()).ObjectId);
        var applied = s.LastEvents<RollModifiersApplied>().Last();
        Assert.DoesNotContain(applied.Modifiers, m => m.ObjectId == "horror-filter");
        Assert.Equal(new (string, EventKind?)[] { ("horror-filter", EventKind.Bad) }, Kinds(applied.Waiting));
        Assert.Contains("RPG", Tags(s, "Вася"));
        Assert.Equal(new[] { "horror-filter" }, s.Player("Вася").Wallet.NextRoll.Select(m => m.ObjectId));
    }
}

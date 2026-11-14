using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Ranking;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// A coop completion is the partner's own run too (D-716, decided by the owner 2026-11-14: «иначе нет смысла играть
/// вместе»): for the partner his run-end triggers fire (<c>runCompleted</c>, <c>runEnded</c>), his objects «на N игр»
/// count the game down, the shop price starts over (<c>economy.shop.resetOn</c>), his drop streak ends, and the run counts
/// in his completed runs (the «прохождения» tiebreaker). A reject takes the completion back from both, and a partner who
/// left before the completion gets none of it. The shares still add up to the throw (D-521). Duels are the exception
/// (D-731): there the coop completion wins only for the roller — <c>Duels/DuelReviewTests</c>. Written before the
/// implementation.
/// </summary>
public class CoopPartnerRunTests
{
    private const string OnCompleted = "partner-on-completed";
    private const string OnEnded = "partner-on-ended";
    private const string ForTwoGames = "partner-for-two-games";

    private static ObjectDefinition Effect(string id, Trigger trigger, DurationSpec? duration, int coins) =>
        new()
        {
            Id = id,
            Kind = ObjectKind.Effect,
            Name = id,
            Description = "Эффект для теста.",
            Rarity = Rarity.Common,
            Effect = new EffectSpec
            {
                Trigger = trigger,
                Duration = duration,
                Actions = [new ChangeResourceAction { Resource = "coins", Amount = ContentValue.Of(coins) }],
            },
        };

    /// <summary>Coop with items and the shop on; the repository content plus three effects of the tests.</summary>
    private static Scenario Season(Func<Ruleset, Ruleset>? more = null)
    {
        var s = New(r => more is null ? r.WithEconomy() : more(r.WithEconomy()));
        var pack = EconomyScenario.RepositoryPack();
        return s.WithContent(pack with
        {
            Objects =
            [
                .. pack.Objects,
                Effect(OnCompleted, Trigger.RunCompleted, null, 3),
                Effect(OnEnded, Trigger.RunEnded, null, 3),
                Effect(ForTwoGames, Trigger.RunCompleted, new DurationSpec { Runs = 2 }, 1),
            ],
        });
    }

    private static int ItemCoins(Scenario s, string player) =>
        s.LastEvents<CoinsChanged>().Where(c => c.PlayerId == s.PlayerId(player) && c.Reason == CoinsReason.Item).Sum(c => c.Delta);

    // ---- Run-end triggers ----

    [Theory]
    [InlineData(Trigger.RunCompleted, OnCompleted)]
    [InlineData(Trigger.RunEnded, OnEnded)]
    public void The_partners_run_end_effects_fire_on_the_coop_completion(Trigger trigger, string effect)
    {
        var s = Season();
        var instance = s.Give("Петя", effect);
        s.CoopRun();

        s.NextRandom(3, 4).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(new EffectTriggered(s.PlayerId("Петя"), instance, effect, trigger), Assert.Single(s.LastEvents<EffectTriggered>()));
        Assert.Equal(3, ItemCoins(s, "Петя"));

        // The split itself is as before: the shares add up to the throw (D-521)
        var split = Assert.Single(s.LastEvents<CoopRewardSplit>());
        Assert.Equal(split.Total, split.RollerPoints + split.PartnerPoints);
    }

    [Fact]
    public void The_rollers_and_the_partners_effects_each_fire_once_on_the_coop_completion()
    {
        var s = Season();
        s.Give("Вася", OnCompleted);
        s.Give("Петя", OnCompleted);
        s.CoopRun();

        s.NextRandom(3, 4).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new[] { s.PlayerId("Вася"), s.PlayerId("Петя") }.Order(),
            s.LastEvents<EffectTriggered>().Select(e => e.PlayerId).Order());
        Assert.Equal((3, 3), (ItemCoins(s, "Вася"), ItemCoins(s, "Петя")));
    }

    [Fact]
    public void A_partner_who_left_before_the_completion_gets_no_run_end_trigger()
    {
        var s = Season();
        s.Give("Петя", OnCompleted);
        s.CoopRun();
        s.Act(new LeaveCoop(s.PlayerId("Петя")));
        ScenarioAssert.Accepted(s);

        s.NextRandom(3, 4).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.DoesNotContain(s.LastEvents<EffectTriggered>(), e => e.PlayerId == s.PlayerId("Петя"));
        Assert.Equal(0, ItemCoins(s, "Петя"));
    }

    // ---- Objects «на N игр» ----

    [Fact]
    public void The_coop_completion_counts_down_the_partners_object_for_n_games()
    {
        var s = Season();
        s.Give("Петя", ForTwoGames);
        Assert.Equal(2, s.Held("Петя", ForTwoGames).RunsLeft);
        s.CoopRun();

        s.CompleteWith("Вася", 3, 4);

        Assert.Equal(1, s.Held("Петя", ForTwoGames).RunsLeft);

        // His second game — another coop — spends it
        s.CoopRun(title: SecondCoopGame);
        s.CompleteWith("Вася", 1, 1);
        Assert.DoesNotContain(ForTwoGames, s.Inventory("Петя"));
        Assert.Equal(ObjectRemoval.Expired, Assert.Single(s.LastEvents<ObjectRemoved>(), r => r.PlayerId == s.PlayerId("Петя")).Reason);
    }

    // ---- The shop price ----

    [Fact]
    public void The_coop_completion_starts_the_partners_shop_price_over()
    {
        var s = Season().WithCoins("Петя", 100);
        s.RollShop("Петя");
        Assert.Equal(1, s.Player("Петя").Wallet.ShopRolls);
        s.CoopRun();

        s.NextRandom(3, 4).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Contains(new ShopPriceRestarted(s.PlayerId("Петя")), s.Last.Events);
        Assert.Equal(0, s.Player("Петя").Wallet.ShopRolls);
    }

    [Fact]
    public void The_partners_shop_price_does_not_start_over_on_what_the_rules_leave_out()
    {
        var s = Season(r => r with { Economy = r.Economy with { Shop = r.Economy.Shop with { ResetOn = [ShopPriceReset.RunDropped] } } })
            .WithCoins("Петя", 100);
        s.RollShop("Петя");
        s.CoopRun();

        s.NextRandom(3, 4).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.DoesNotContain(new ShopPriceRestarted(s.PlayerId("Петя")), s.Last.Events);
        Assert.Equal(1, s.Player("Петя").Wallet.ShopRolls);
    }

    // ---- The drop streak ----

    private static Scenario StreakSeason() => New(r => r with { Drop = r.Drop with { ConsecutiveExtraDice = 1 } });

    /// <summary>The player plays <paramref name="title"/> and drops it; returns how many penalty dice the drop threw.</summary>
    private static int PlayAndDrop(Scenario s, string player, string title)
    {
        s.PlayTitle(player, title);
        s.Act(new DropRun(s.PlayerId(player)));
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(1));
        return Assert.Single(s.LastEvents<RunDropped>()).PenaltyDice.Count;
    }

    [Fact]
    public void The_coop_completion_ends_the_partners_drop_streak()
    {
        // Pinned test ruleset: the penalty is 2d4, one more die for each further drop in a row
        var s = StreakSeason();
        Assert.Equal(2, PlayAndDrop(s, "Петя", SoloGame));
        s.CoopRun();
        s.CompleteWith("Вася", 3, 4);

        Assert.Equal(2, PlayAndDrop(s, "Петя", OtherSoloGame));
    }

    [Fact]
    public void A_rejected_coop_completion_no_longer_ends_the_partners_drop_streak()
    {
        var s = StreakSeason();
        PlayAndDrop(s, "Петя", SoloGame);
        var run = s.CoopRun();
        s.CompleteWith("Вася", 3, 4);
        s.Act(new RejectProof(run, "На скрине другая игра"));
        ScenarioAssert.Accepted(s);

        Assert.Equal(3, PlayAndDrop(s, "Петя", OtherSoloGame));
    }

    [Fact]
    public void A_partner_who_left_the_coop_keeps_his_drop_streak()
    {
        var s = StreakSeason();
        PlayAndDrop(s, "Петя", SoloGame);
        s.CoopRun();
        s.Act(new LeaveCoop(s.PlayerId("Петя")));
        ScenarioAssert.Accepted(s);
        s.CompleteWith("Вася", 3, 4);

        Assert.Equal(3, PlayAndDrop(s, "Петя", OtherSoloGame));
    }

    // ---- Completed runs (the tiebreaker) ----

    [Fact]
    public void The_coop_completion_counts_in_the_completed_runs_of_both()
    {
        var s = New();
        s.CoopRun();

        s.CompleteWith("Вася", 3, 4);

        var entries = Leaderboard.Entries(s.State).ToDictionary(e => e.PlayerId);
        Assert.Equal((1, 1), (entries[s.PlayerId("Вася")].CompletedRuns, entries[s.PlayerId("Петя")].CompletedRuns));
        Assert.Equal(0, entries[s.PlayerId("Маша")].CompletedRuns);
    }

    [Fact]
    public void With_equal_points_the_partner_with_the_coop_completion_is_above_a_player_without_runs()
    {
        // Маша gets 1 point from the admin first (no run), so the earliest final score would put her above; Петя then gets
        // 1 of the throw of 2: the completed runs come first in the tiebreakers and break the tie for him
        var s = New();
        s.WithPoints("Маша", 1);
        s.CoopRun();
        s.CompleteWith("Вася", 1, 1);
        Assert.Equal(1, s.Player("Петя").Points);

        var rows = Leaderboard.Build(s.State).ToDictionary(r => r.PlayerId);

        Assert.True(rows[s.PlayerId("Петя")].Place < rows[s.PlayerId("Маша")].Place, "The partner's coop completion did not break the tie.");
    }

    [Fact]
    public void A_rejected_coop_run_counts_for_neither()
    {
        var s = New();
        var run = s.CoopRun();
        s.CompleteWith("Вася", 3, 4);

        s.Act(new RejectProof(run, "На скрине другая игра"));

        ScenarioAssert.Accepted(s);
        var entries = Leaderboard.Entries(s.State).ToDictionary(e => e.PlayerId);
        Assert.Equal((0, 0), (entries[s.PlayerId("Вася")].CompletedRuns, entries[s.PlayerId("Петя")].CompletedRuns));
    }

    [Fact]
    public void A_partner_who_left_before_the_completion_does_not_count_it()
    {
        var s = New();
        s.CoopRun();
        s.Act(new LeaveCoop(s.PlayerId("Петя")));
        ScenarioAssert.Accepted(s);

        s.CompleteWith("Вася", 3, 4);

        var entries = Leaderboard.Entries(s.State).ToDictionary(e => e.PlayerId);
        Assert.Equal((1, 0), (entries[s.PlayerId("Вася")].CompletedRuns, entries[s.PlayerId("Петя")].CompletedRuns));
    }
}

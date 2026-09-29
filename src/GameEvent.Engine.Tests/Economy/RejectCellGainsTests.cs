using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// MP22 (D-327 (2), D-421): a reject takes back what the rejected run's move got on cells — the objects of the shop cells it
/// stopped on or passed, when the player still holds them — and what items changed in its throw after it was made. Coins
/// the run gave go even when spent: the balance goes below zero, and nothing is bought in debt. Games of 3 hours give one
/// d4; the map is start → s1 (shop) → a → s2 (shop) → b → c → finish, both shops grant the coupon.
/// </summary>
public class RejectCellGainsTests
{
    private const string Comment = "Фейковое прохождение";

    private static Scenario Season(MapGraph? map = null)
    {
        map ??= ShopMap();
        var s = EconomyScenario.New().WithMap(map).WithCategory("Horror")
            .WithGame("Silent Hill", 3, "Horror").WithGame("Fatal Frame", 3, "Horror").WithGame("Siren", 3, "Horror")
            .WithPlayers("Вася", "Петя");
        return s.WithContent(EconomyScenario.RepositoryPack());
    }

    private static MapGraph ShopMap()
    {
        var map = MapBuilder.New().Path("start", "s1", "a", "s2", "b", "c", "finish").Build();
        return map with
        {
            Cells = [.. map.Cells.Select(c => c.Id is "s1" or "s2" ? c with { Type = CellType.Shop, Grants = "shop-coupon" } : c)],
        };
    }

    private static Guid Throw(Scenario s, string title, int die)
    {
        s.RollTitle("Вася", title).Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.NextRandom(die).Complete("Вася");
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(1));
        return runId;
    }

    private static List<ObjectRemoved> TakenBack(Scenario s) =>
        [.. s.LastEvents<ObjectRemoved>().Where(r => r.Reason == ObjectRemoval.ProofRejected)];

    [Fact]
    public void Coupons_the_move_got_on_shop_cells_go_with_the_rejected_run()
    {
        // Given Вася threw 3: past s1 onto s2, a coupon from each
        var s = Season();
        var run = Throw(s, "Silent Hill", 3);
        Assert.Equal(["shop-coupon", "shop-coupon"], s.Inventory("Вася"));

        // When the run is rejected
        s.Act(new RejectProof(run, Comment));

        // Then both coupons are taken back, and he is back on the start
        ScenarioAssert.Accepted(s);
        Assert.Equal(2, TakenBack(s).Count);
        Assert.Empty(s.Inventory("Вася"));
        Assert.Equal("start", s.Player("Вася").CellId);
    }

    [Fact]
    public void Coupons_of_the_move_go_even_after_a_move_since()
    {
        var s = Season();
        var run = Throw(s, "Silent Hill", 1);
        Throw(s, "Fatal Frame", 1);
        Assert.Equal(["shop-coupon"], s.Inventory("Вася"));

        s.Act(new RejectProof(run, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Single(TakenBack(s));
        Assert.Empty(s.Inventory("Вася"));
    }

    [Fact]
    public void A_coupon_already_used_is_not_taken_back()
    {
        var s = Season();
        var run = Throw(s, "Silent Hill", 3);
        s.Used("Вася", "shop-coupon");
        Assert.Equal(1, s.Player("Вася").Resources["freeShopRolls"]);

        s.Act(new RejectProof(run, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Single(TakenBack(s));
        Assert.Empty(s.Inventory("Вася"));
        Assert.Equal(1, s.Player("Вася").Resources["freeShopRolls"]);
    }

    [Fact]
    public void A_coupon_of_another_move_stays()
    {
        // Given the run stopped on s1 (a coupon), then a banana pushed him 2 cells past s2 (another coupon)
        var s = Season();
        var run = Throw(s, "Silent Hill", 1);
        s.Give("Вася", "banana");
        s.Used("Вася", "banana");
        Assert.Equal(["shop-coupon", "shop-coupon"], s.Inventory("Вася"));
        var pushed = s.Player("Вася").Wallet.Inventory[^1].InstanceId;

        s.Act(new RejectProof(run, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Single(TakenBack(s));
        Assert.Equal(pushed, Assert.Single(s.Player("Вася").Wallet.Inventory).InstanceId);
    }

    [Fact]
    public void A_coupon_someone_took_stays_with_them()
    {
        var s = Season();
        var run = Throw(s, "Silent Hill", 1);
        s.Give("Петя", "magnet");
        s.Used("Петя", "magnet", target: "Вася");
        Assert.Equal(["shop-coupon"], s.Inventory("Петя"));

        s.Act(new RejectProof(run, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Empty(TakenBack(s));
        Assert.Equal(["shop-coupon"], s.Inventory("Петя"));
    }

    [Fact]
    public void Reject_with_the_drop_penalty_takes_the_coupons_back_too()
    {
        var s = Season();
        var run = Throw(s, "Silent Hill", 3);

        s.NextRandom(1, 1).Act(new RejectProofWithDropPenalty(run, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Equal(2, TakenBack(s).Count);
        Assert.Empty(s.Inventory("Вася"));
    }

    [Fact]
    public void Coupons_stay_while_items_are_off()
    {
        // D-420: a mechanic switched off leaves no event
        var s = Season();
        var run = Throw(s, "Silent Hill", 3);
        s.WithRuleset(r => r with { Features = r.Features with { Items = false, Shop = false } });

        s.Act(new RejectProof(run, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<ObjectRemoved>());
        Assert.Equal(2, s.Inventory("Вася").Count);
    }

    [Fact]
    public void Spent_coins_of_the_run_take_the_balance_below_zero_and_nothing_is_bought_in_debt()
    {
        // Given no negative coins by effects (D-417) — a reject is not an effect; the run gave 3 coins, spent on the shop
        var s = Season(MapBuilder.New().Path("start", "a", "b", "c", "d", "finish").Build());
        s.WithRuleset(r => r with { Economy = r.Economy with { AllowNegativeCoins = false } });
        s.WithCoins("Вася", 2);
        var run = Throw(s, "Silent Hill", 1);
        Assert.Equal(5, s.Player("Вася").Coins);
        s.RollShop("Вася");
        Assert.Equal(0, s.Player("Вася").Coins);

        s.Act(new RejectProof(run, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Equal(-3, s.Player("Вася").Coins);
        s.RollShop("Вася");
        Assert.Equal(RejectionCodes.CoinsInDebt, s.Last.Rejection!.Code);
    }

    [Fact]
    public void Steps_an_item_added_after_the_throw_go_with_the_rejected_run()
    {
        // Given Вася threw 1 onto s1, then rerolled his throw to 4 with an item: past s2 onto b
        var s = Season();
        var run = Throw(s, "Silent Hill", 1);
        s.Give("Вася", "reroll-dice");
        s.NextRandom(4).Used("Вася", "reroll-dice");
        Assert.Equal(("b", 4), (s.Player("Вася").CellId, s.Player("Вася").Points));

        // When the run is rejected
        s.Act(new RejectProof(run, Comment));

        // Then the whole move goes: he has not moved since, so he is back where it began
        ScenarioAssert.Accepted(s);
        Assert.Equal(("start", 0), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    [Fact]
    public void Undo_of_the_reject_gives_the_coupons_back()
    {
        var s = Season();
        var run = Throw(s, "Silent Hill", 3);
        var before = s.State;
        s.Act(new RejectProof(run, Comment));
        ScenarioAssert.Accepted(s);

        s.Act(new Engine.Undo.UndoCommand(s.History[^1].CommandId, "Ошибся кнопкой"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(before.Players[s.PlayerId("Вася")].Wallet, s.Player("Вася").Wallet);
        Assert.Equal(before.Runs[run], s.State.Runs[run]);
    }
}

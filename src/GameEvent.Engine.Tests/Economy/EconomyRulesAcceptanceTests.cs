using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// Acceptance of the economy rules of docs/SPEC.md: «Экономика и магазин» (no purchases in debt, personal lots for N minutes
/// counted by the server, a shop reroll dearer within one game and reset after a completion or a drop, the inventory
/// limit), «Взаимодействие игроков» and «Цели» (the first finisher is never a target and does not use items, random targets
/// only among the active), «Ставки» (not on oneself or the first finisher, only in the window after the roll, the stake in
/// pledge, the win ⌊stake × multiplier⌋, lost on a drop or a missed deadline, taken back by a reject) and the feature flags
/// (a disabled mechanic is refused and writes nothing). Pinned test ruleset: shop 3 lots for 15 minutes, roll 5 coins + 5
/// per reroll, inventory limit 5, bets up to 10 coins, 3 open, window 24 h, days 1/3/7, multiplier 1.2 up to 1 h a day,
/// 2.0 up to 3 h, 3.0 above.
/// </summary>
public class EconomyRulesAcceptanceTests
{
    private static readonly int[] s_seeds = [.. Enumerable.Range(1, 20)];

    private static readonly string[] s_titles = ["Silent Hill", "Alan Wake", "Dead Space", "Outlast", "Amnesia", "Soma", "Prey", "Doom"];

    /// <summary>A running season, 6-hour horror games (two d4 each), the doc's items, the given players.</summary>
    private static Scenario Season(int seed = 42, Func<Ruleset, Ruleset>? rules = null, int mapLength = 60, params string[] players)
    {
        var s = EconomyScenario.New(seed);
        if (rules is not null)
        {
            s.WithRuleset(rules);
        }

        s.WithMapLength(mapLength).WithCategory("Horror");
        foreach (var title in s_titles)
        {
            s.WithGame(title, 6, "Horror");
        }

        s.WithPlayers(players.Length == 0 ? ["Вася", "Петя"] : players);
        return s.Ruleset.Features.Items ? s.WithContent(EconomyScenario.ExamplePack()) : s;
    }

    private static Guid Playing(Scenario s, string player)
    {
        s.Roll(player).Start(player);
        return s.Player(player).ActiveRunId!.Value;
    }

    private static Scenario Drop(Scenario s, string player)
    {
        s.Advance(TimeSpan.FromMinutes(61));
        s.Act(new DropRun(s.PlayerId(player)));
        ScenarioAssert.Accepted(s);
        return s;
    }

    private static void RejectedWith(Scenario s, Func<Scenario, Scenario> act, string code) =>
        ScenarioAssert.RejectsWithoutChanges(s, act, code);

    private static int ShopPrice(Scenario s) => Assert.Single(s.LastEvents<ShopRolled>()).Price;

    // ---- «Магазин: ролл стоит монеток и даёт 3 личных лота на N минут» ----

    [Fact]
    public void A_shop_roll_costs_coins_and_gives_three_personal_lots_for_the_lifetime()
    {
        var s = Season().WithCoins("Вася", 100);

        s.RollShop("Вася");

        ScenarioAssert.Accepted(s);
        var rolled = Assert.Single(s.LastEvents<ShopRolled>());
        Assert.Equal((ShopPayment.Coins, 5), (rolled.Payment, rolled.Price));
        Assert.Equal(3, rolled.Lots.Count);
        Assert.Equal(s.Clock.UtcNow.AddMinutes(15), rolled.ExpiresAt);
        Assert.Equal(new CoinsChanged(s.PlayerId("Вася"), -5, CoinsReason.ShopRoll, null), Assert.Single(s.LastEvents<CoinsChanged>()));
        Assert.Equal(95, s.Player("Вася").Coins);

        // Lots are items for sale at their price; the offer is Вася's alone
        foreach (var lot in rolled.Lots)
        {
            var definition = s.State.Catalog.Get(lot.ObjectId);
            Assert.Equal(ObjectKind.Item, definition.Kind);
            Assert.NotNull(definition.Price);
            Assert.Equal((definition.Price!.Value, definition.Rarity!.Value, false), (lot.Price, lot.Rarity, lot.Sold));
        }

        Assert.Equal(rolled.Lots, s.Player("Вася").Wallet.Shop!.Lots);
        Assert.Null(s.Player("Петя").Wallet.Shop);
    }

    [Fact]
    public void Buying_a_lot_puts_the_item_in_the_inventory_for_its_price()
    {
        var s = Season().WithCoins("Вася", 100).RollShop("Вася");
        var lot = s.Player("Вася").Wallet.Shop!.Lots[1];

        s.Buy("Вася", 1);

        ScenarioAssert.Accepted(s);
        var bought = Assert.Single(s.LastEvents<LotBought>());
        Assert.Equal((1, lot.ObjectId, lot.Price), (bought.Lot, bought.Item.ObjectId, bought.Price));
        Assert.Contains(s.LastEvents<CoinsChanged>(), c => c.Delta == -lot.Price && c.Reason == CoinsReason.Purchase);
        Assert.Equal(95 - lot.Price, s.Player("Вася").Coins);
        Assert.Equal([lot.ObjectId], s.Inventory("Вася"));
        Assert.True(s.Player("Вася").Wallet.Shop!.Lots[1].Sold);
    }

    [Fact]
    public void A_sold_lot_cannot_be_bought_twice()
    {
        var s = Season().WithCoins("Вася", 100).RollShop("Вася").Buy("Вася", 0);
        ScenarioAssert.Accepted(s);

        RejectedWith(s, x => x.Buy("Вася", 0), RejectionCodes.ShopLotSold);
    }

    [Fact]
    public void A_lot_outside_the_offer_is_refused()
    {
        var s = Season().WithCoins("Вася", 100).RollShop("Вася");

        RejectedWith(s, x => x.Buy("Вася", 3), RejectionCodes.ShopUnknownLot);
    }

    [Fact]
    public void Nothing_can_be_bought_without_a_shop_roll()
    {
        var s = Season().WithCoins("Вася", 100);

        RejectedWith(s, x => x.Buy("Вася", 0), RejectionCodes.ShopNoOffer);
    }

    [Fact]
    public void Lots_are_personal_another_player_cannot_buy_them()
    {
        var s = Season().WithCoins("Вася", 100).WithCoins("Петя", 100).RollShop("Вася");

        RejectedWith(s, x => x.Buy("Петя", 0), RejectionCodes.ShopNoOffer);
    }

    [Fact]
    public void A_free_shop_roll_from_the_coupon_is_spent_before_coins()
    {
        var s = Season().WithCoins("Вася", 100);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Купон магазина", ResourceDeltas: [new ResourceDelta("freeShopRolls", 1)]));
        ScenarioAssert.Accepted(s);

        s.RollShop("Вася");

        Assert.Equal(ShopPayment.FreeRoll, Assert.Single(s.LastEvents<ShopRolled>()).Payment);
        Assert.Equal((100, 0), (s.Player("Вася").Coins, s.Player("Вася").Resources["freeShopRolls"]));
    }

    // ---- «Время считает сервер, по истечении лоты исчезают» («Копить лоты магазина до поступления денег») ----

    [Fact]
    public void A_lot_can_be_bought_until_its_time_is_over()
    {
        var s = Season().WithCoins("Вася", 100).RollShop("Вася");
        s.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));

        s.Buy("Вася", 0);

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void An_expired_lot_is_refused_even_before_the_timers_fire()
    {
        var s = Season().WithCoins("Вася", 100).RollShop("Вася");
        s.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        RejectedWith(s, x => x.Buy("Вася", 0), RejectionCodes.ShopOfferExpired);
    }

    [Fact]
    public void Fire_timers_removes_an_expired_offer()
    {
        var s = Season().WithCoins("Вася", 100).RollShop("Вася");
        s.Advance(TimeSpan.FromMinutes(16));

        s.FireTimers();

        ScenarioAssert.Accepted(s);
        Assert.Equal(new ShopOfferExpired(s.PlayerId("Вася")), Assert.Single(s.LastEvents<ShopOfferExpired>()));
        Assert.Null(s.Player("Вася").Wallet.Shop);
        Assert.Equal(95, s.Player("Вася").Coins);
        Assert.NotEqual(RejectionCodes.ShopOfferExpired, s.ExpectRejection().Buy("Вася", 0).Last.Rejection?.Code ?? "");
    }

    [Fact]
    public void Fire_timers_keeps_an_offer_that_still_lasts()
    {
        var s = Season().WithCoins("Вася", 100).RollShop("Вася");
        s.Advance(TimeSpan.FromMinutes(10));

        RejectedWith(s, x => x.FireTimers(), RejectionCodes.TimersNothingDue);
        Assert.NotNull(s.Player("Вася").Wallet.Shop);
    }

    // ---- «Реролл магазина в пределах одной игры каждый раз дороже, цена сбрасывается после прохождения или дропа» ----

    [Fact]
    public void Each_shop_reroll_costs_more_and_replaces_the_lots()
    {
        var s = Season().WithCoins("Вася", 100);

        var prices = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            s.RollShop("Вася");
            ScenarioAssert.Accepted(s);
            prices.Add(ShopPrice(s));
        }

        Assert.Equal([5, 10, 15], prices);
        Assert.Equal(70, s.Player("Вася").Coins);
        Assert.Equal(Assert.Single(s.LastEvents<ShopRolled>()).Lots, s.Player("Вася").Wallet.Shop!.Lots);
    }

    [Fact]
    public void The_shop_price_resets_after_a_completion()
    {
        var s = Season().WithCoins("Вася", 100);
        Playing(s, "Вася");
        s.RollShop("Вася").RollShop("Вася");
        Assert.Equal(10, ShopPrice(s));

        s.Complete("Вася");
        Assert.Equal(0, s.Player("Вася").Wallet.ShopRolls);
        s.RollShop("Вася");

        Assert.Equal(5, ShopPrice(s));
    }

    [Fact]
    public void The_shop_price_resets_after_a_drop()
    {
        var s = Season().WithCoins("Вася", 100);
        Playing(s, "Вася");
        s.RollShop("Вася").RollShop("Вася");
        Assert.Equal(10, ShopPrice(s));

        Drop(s, "Вася");
        s.RollShop("Вася");

        Assert.Equal(5, ShopPrice(s));
    }

    [Fact]
    public void The_shop_price_does_not_reset_on_a_game_reroll()
    {
        var s = Season().WithCoins("Вася", 100).Roll("Вася");
        s.RollShop("Вася");

        s.Act(new GameEvent.Engine.Rolls.Reroll(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);
        s.RollShop("Вася");

        Assert.Equal(10, ShopPrice(s));
    }

    // ---- «Минус: … в минусе покупать нельзя» ----

    [Fact]
    public void A_player_in_debt_cannot_roll_the_shop()
    {
        var s = Season().WithCoins("Вася", -1);

        RejectedWith(s, x => x.RollShop("Вася"), RejectionCodes.CoinsInDebt);
    }

    [Fact]
    public void A_player_in_debt_cannot_buy_a_lot()
    {
        var s = Season().WithCoins("Вася", 100).RollShop("Вася").WithCoins("Вася", -1);

        RejectedWith(s, x => x.Buy("Вася", 0), RejectionCodes.CoinsInDebt);
    }

    [Fact]
    public void A_player_driven_into_debt_by_a_thief_cannot_buy()
    {
        var s = Season().WithCoins("Петя", 100).RollShop("Петя").WithCoins("Петя", 1);
        s.Give("Вася", "bird-thief");
        s.NextRandom(4).Used("Вася", "bird-thief", target: "Петя");
        Assert.Equal(-3, s.Player("Петя").Coins);

        RejectedWith(s, x => x.Buy("Петя", 0), RejectionCodes.CoinsInDebt);
    }

    [Fact]
    public void Coins_exactly_equal_to_the_price_buy_the_lot_and_leave_zero()
    {
        var s = Season().WithCoins("Вася", 100).RollShop("Вася");
        var price = s.Player("Вася").Wallet.Shop!.Lots[0].Price;
        s.WithCoins("Вася", price);

        s.Buy("Вася", 0);

        ScenarioAssert.Accepted(s);
        Assert.Equal(0, s.Player("Вася").Coins);
    }

    // ---- «Лимит инвентаря задаётся в конфиге» ----

    [Fact]
    public void A_full_inventory_refuses_a_purchase()
    {
        var s = Season().WithCoins("Вася", 100);
        for (var i = 0; i < 5; i++)
        {
            s.Give("Вася", "orange");
        }

        s.RollShop("Вася");

        RejectedWith(s, x => x.Buy("Вася", 0), RejectionCodes.InventoryFull);
    }

    [Fact]
    public void The_last_free_place_of_the_inventory_takes_a_purchase()
    {
        var s = Season().WithCoins("Вася", 100);
        for (var i = 0; i < 4; i++)
        {
            s.Give("Вася", "orange");
        }

        s.RollShop("Вася").Buy("Вася", 0);

        ScenarioAssert.Accepted(s);
        Assert.Equal(5, s.Player("Вася").Wallet.Items);
    }

    // ---- «Финишировавший первым … не использует предметы на других … Инвентарь заморожен, бить по нему нельзя» ----

    /// <summary>A map of 4 steps; Лёша reaches the finish first (3 + 1) and his run is approved: he is frozen.</summary>
    private static Scenario WithFrozenFirst(int seed = 42, params string[] others)
    {
        var s = Season(seed, mapLength: 4, players: ["Лёша", .. others]);
        var runId = Playing(s, "Лёша");
        s.NextRandom(3, 1).Complete("Лёша");
        s.Act(new ApproveProof(runId, null, "Видел на стриме"));
        ScenarioAssert.Accepted(s);
        Assert.True(s.Player("Лёша").Finish?.Frozen, "Лёша is not the frozen first finisher.");
        s.Advance(TimeSpan.FromHours(1));
        return s;
    }

    [Fact]
    public void The_first_finisher_cannot_use_items()
    {
        var s = Season(mapLength: 4, players: ["Лёша", "Вася"]);
        var orange = s.Give("Лёша", "orange");
        var runId = Playing(s, "Лёша");
        s.NextRandom(3, 1).Complete("Лёша");
        s.Act(new ApproveProof(runId, null, "Видел на стриме"));
        Assert.True(s.Player("Лёша").Finish?.Frozen);
        var before = s.State;
        var log = s.Log.Count;

        s.Use("Лёша", orange);

        Assert.False(s.Last.IsAccepted, "The first finisher used an item.");
        Assert.Contains(s.Last.Rejection!.Code, new[] { RejectionCodes.InventoryFrozen, RejectionCodes.PlayerFinished });
        Assert.Equal(before, s.State);
        Assert.Equal(log, s.Log.Count);
    }

    [Fact]
    public void The_first_finisher_cannot_be_targeted()
    {
        var s = WithFrozenFirst(42, "Вася").WithCoins("Лёша", 10);
        var thief = s.Give("Вася", "bird-thief");

        RejectedWith(s, x => x.Use("Вася", thief, "Лёша"), RejectionCodes.ItemInvalidTarget);
    }

    [Fact]
    public void A_later_finisher_is_still_a_target_of_coin_effects()
    {
        // SPEC «По финишировавшим не первыми работают только эффекты на очки и монетки»
        var s = WithFrozenFirst(42, "Петя", "Вася");
        Playing(s, "Петя");
        s.NextRandom(3, 1).Complete("Петя");
        Assert.NotNull(s.Player("Петя").Finish);
        s.WithCoins("Петя", 20);
        s.Give("Вася", "bird-thief");

        s.NextRandom(2).Used("Вася", "bird-thief", target: "Петя");

        Assert.Equal((18, 2), (s.Player("Петя").Coins, s.Player("Вася").Coins));
    }

    [Fact]
    public void The_first_finisher_cannot_be_bet_on()
    {
        var s = WithFrozenFirst(42, "Вася").WithCoins("Вася", 10);
        Playing(s, "Лёша");

        RejectedWith(s, x => x.Bet("Вася", "Лёша", 7, 5), RejectionCodes.BetOnFirst);
    }

    // ---- «Случайная цель выбирается только среди активных» (Цели: для случайных целей — неактивные исключены) ----

    /// <summary>«Птичкерс» on a random active player, not a doc example: the doc has no item with <c>randomActive</c>.</summary>
    private static ContentPack WithRandomThief()
    {
        var thief = EconomyScenario.Examples()["bird-thief"];
        var random = thief with
        {
            Id = "random-thief",
            Name = "Случайный птичкерс",
            Price = null,
            Effect = thief.Effect! with { Target = new TargetSpec { Selector = TargetSelector.RandomActive, ExcludeSelf = true } },
        };
        var pack = EconomyScenario.ExamplePack();
        return pack with { Objects = [.. pack.Objects, random] };
    }

    [Fact]
    public void A_random_target_is_only_an_active_player_never_an_inactive_one_or_the_first_finisher()
    {
        foreach (var seed in s_seeds)
        {
            var s = WithFrozenFirst(seed, "Петя", "Коля", "Вася").WithContent(WithRandomThief());
            s.Act(new SetPlayerInactive(s.PlayerId("Петя"), true));
            ScenarioAssert.Accepted(s);
            s.Give("Вася", "random-thief");

            s.Used("Вася", "random-thief");

            Assert.Equal([s.PlayerId("Коля")], Assert.Single(s.LastEvents<ItemUsed>()).Targets);
        }
    }

    [Fact]
    public void A_random_target_item_with_no_active_player_to_hit_is_refused()
    {
        var s = WithFrozenFirst(42, "Петя", "Вася").WithContent(WithRandomThief());
        s.Act(new SetPlayerInactive(s.PlayerId("Петя"), true));
        var thief = s.Give("Вася", "random-thief");

        RejectedWith(s, x => x.Use("Вася", thief), RejectionCodes.ItemNoTarget);
    }

    [Fact]
    public void An_inactive_player_can_still_be_chosen_by_name()
    {
        // The inactivity flag excludes only random targets
        var s = Season().WithCoins("Петя", 10);
        s.Act(new SetPlayerInactive(s.PlayerId("Петя"), true));
        s.Give("Вася", "bird-thief");

        s.NextRandom(1).Used("Вася", "bird-thief", target: "Петя");

        Assert.Equal(9, s.Player("Петя").Coins);
    }

    // ---- «Ставки» ----

    /// <summary>Петя plays a 6-hour game rolled now; Вася has 10 coins.</summary>
    private static (Scenario S, Guid RunId) Betting(params string[] morePlayers)
    {
        var s = Season(players: ["Вася", "Петя", .. morePlayers]).WithCoins("Вася", 10);
        var runId = Playing(s, "Петя");
        return (s, runId);
    }

    [Fact]
    public void A_bet_takes_the_stake_into_pledge_on_the_current_run()
    {
        var (s, runId) = Betting();

        s.Bet("Вася", "Петя", 7, 7);

        ScenarioAssert.Accepted(s);
        var bet = Assert.Single(s.LastEvents<BetPlaced>()).Bet;
        Assert.Equal((s.PlayerId("Петя"), runId, 7, BetStatus.Open), (bet.OnPlayerId, bet.RunId, bet.Stake, bet.Status));
        Assert.Equal(bet.PlacedAt.AddDays(7), bet.Deadline);
        Assert.Equal(new CoinsChanged(s.PlayerId("Вася"), -7, CoinsReason.BetStake, null), Assert.Single(s.LastEvents<CoinsChanged>()) with { RunId = null });
        Assert.Equal(3, s.Player("Вася").Coins);
        Assert.Equal(bet, Assert.Single(s.Player("Вася").Wallet.Bets));

        // «Прямых переводов между игроками нет»: the stake goes to the system, not to Петя
        Assert.Equal(0, s.Player("Петя").Coins);
    }

    [Theory]
    [InlineData(7, 1.2)] // 6 h in 7 days: under 1 h a day
    [InlineData(3, 2.0)] // 2 h a day
    [InlineData(1, 3.0)] // 6 h a day
    public void The_multiplier_depends_on_the_hours_a_day_needed_to_make_it(int days, double multiplier)
    {
        var (s, _) = Betting();

        s.Bet("Вася", "Петя", days, 5);

        ScenarioAssert.Accepted(s);
        Assert.Equal((decimal)multiplier, Assert.Single(s.LastEvents<BetPlaced>()).Bet.Multiplier);
    }

    [Fact]
    public void A_bet_on_oneself_is_refused()
    {
        var (s, _) = Betting();
        Playing(s, "Вася");

        RejectedWith(s, x => x.Bet("Вася", "Вася", 7, 5), RejectionCodes.BetOnSelf);
    }

    [Fact]
    public void A_bet_needs_a_current_run_of_the_player()
    {
        var s = Season().WithCoins("Вася", 10);

        RejectedWith(s, x => x.Bet("Вася", "Петя", 7, 5), RejectionCodes.BetNoRun);
    }

    [Fact]
    public void A_bet_is_accepted_until_the_window_after_the_roll_closes()
    {
        var (s, _) = Betting();
        s.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1));

        s.Bet("Вася", "Петя", 7, 5);

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void A_bet_after_the_window_after_the_roll_is_refused()
    {
        var (s, _) = Betting();
        s.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));

        RejectedWith(s, x => x.Bet("Вася", "Петя", 7, 5), RejectionCodes.BetWindowClosed);
    }

    [Fact]
    public void A_deadline_outside_the_options_is_refused()
    {
        var (s, _) = Betting();

        RejectedWith(s, x => x.Bet("Вася", "Петя", 2, 5), RejectionCodes.BetInvalidDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(11)]
    public void A_stake_outside_1_to_the_max_is_refused(int stake)
    {
        var (s, _) = Betting();
        s.WithCoins("Вася", 100);

        RejectedWith(s, x => x.Bet("Вася", "Петя", 7, stake), RejectionCodes.BetInvalidStake);
    }

    [Fact]
    public void More_open_bets_than_the_limit_are_refused()
    {
        var (s, _) = Betting("Коля", "Маша", "Оля");
        s.WithCoins("Вася", 100);
        Playing(s, "Коля");
        Playing(s, "Маша");
        Playing(s, "Оля");
        s.Bet("Вася", "Петя", 7, 1).Bet("Вася", "Коля", 7, 1).Bet("Вася", "Маша", 7, 1);
        ScenarioAssert.Accepted(s);

        RejectedWith(s, x => x.Bet("Вася", "Оля", 7, 1), RejectionCodes.BetTooMany);
    }

    [Fact]
    public void A_won_bet_pays_the_stake_times_the_multiplier_rounded_down()
    {
        var (s, _) = Betting();
        s.Bet("Вася", "Петя", 7, 7);
        s.Advance(TimeSpan.FromHours(5));

        s.Complete("Петя");

        // ⌊7 × 1.2⌋ = 8
        var settled = Assert.Single(s.LastEvents<BetSettled>());
        Assert.Equal((s.PlayerId("Вася"), BetStatus.Won, 8), (settled.PlayerId, settled.Status, settled.Payout));
        Assert.Contains(s.LastEvents<CoinsChanged>(), c => c.PlayerId == s.PlayerId("Вася") && c.Delta == 8 && c.Reason == CoinsReason.BetPayout);
        Assert.Equal(10 - 7 + 8, s.Player("Вася").Coins);
        var bet = Assert.Single(s.Player("Вася").Wallet.Bets);
        Assert.Equal((BetStatus.Won, 8), (bet.Status, bet.Payout));
    }

    [Fact]
    public void A_bet_is_lost_when_the_run_is_dropped()
    {
        var (s, _) = Betting();
        s.Bet("Вася", "Петя", 7, 7);

        Drop(s, "Петя");

        var settled = Assert.Single(s.LastEvents<BetSettled>());
        Assert.Equal((BetStatus.Lost, 0), (settled.Status, settled.Payout));
        Assert.Equal(3, s.Player("Вася").Coins);
    }

    [Fact]
    public void A_bet_is_lost_when_its_deadline_passes()
    {
        var (s, _) = Betting();
        s.Bet("Вася", "Петя", 1, 5);
        s.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));

        s.FireTimers();

        ScenarioAssert.Accepted(s);
        Assert.Equal(BetStatus.Lost, Assert.Single(s.LastEvents<BetSettled>()).Status);
        Assert.Equal(5, s.Player("Вася").Coins);
    }

    [Fact]
    public void A_completion_after_the_deadline_does_not_win_even_before_the_timers_fire()
    {
        var (s, _) = Betting();
        s.Bet("Вася", "Петя", 1, 5);
        s.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));

        s.Complete("Петя");

        Assert.DoesNotContain(s.LastEvents<BetSettled>(), b => b.Status == BetStatus.Won);
        Assert.DoesNotContain(s.LastEvents<CoinsChanged>(), c => c.Reason == CoinsReason.BetPayout);
        Assert.Equal(5, s.Player("Вася").Coins);
        Assert.NotEqual(BetStatus.Won, Assert.Single(s.Player("Вася").Wallet.Bets).Status);
    }

    [Fact]
    public void A_proof_reject_after_the_payout_takes_the_win_back()
    {
        var (s, runId) = Betting();
        s.Bet("Вася", "Петя", 7, 7);
        s.Advance(TimeSpan.FromHours(5));
        s.Complete("Петя");
        Assert.Equal(11, s.Player("Вася").Coins);

        s.Act(new RejectProof(runId, "На скрине другая игра"));

        ScenarioAssert.Accepted(s);
        var settled = Assert.Single(s.LastEvents<BetSettled>());
        Assert.Equal(BetStatus.Revoked, settled.Status);
        Assert.Contains(s.LastEvents<CoinsChanged>(), c => c.PlayerId == s.PlayerId("Вася") && c.Delta == -8 && c.Reason == CoinsReason.BetPayoutRevoked);
        Assert.Equal(3, s.Player("Вася").Coins);
        Assert.Equal(BetStatus.Revoked, Assert.Single(s.Player("Вася").Wallet.Bets).Status);
    }

    // ---- Flags: «Выключенная механика не видна в интерфейсе и отклоняется движком» ----

    private static Ruleset Without(Ruleset r, bool items = true, bool shop = true, bool bets = true) =>
        r with { Features = r.Features with { Items = items, Shop = shop, Bets = bets } };

    [Fact]
    public void With_the_shop_off_shop_commands_are_refused_and_write_nothing()
    {
        var s = Season(rules: r => Without(r, shop: false)).WithCoins("Вася", 100);

        RejectedWith(s, x => x.RollShop("Вася"), RejectionCodes.FeatureDisabled);
        RejectedWith(s, x => x.Buy("Вася", 0), RejectionCodes.FeatureDisabled);
    }

    [Fact]
    public void With_bets_off_a_bet_is_refused_and_writes_nothing()
    {
        var s = Season(rules: r => Without(r, bets: false)).WithCoins("Вася", 10);
        Playing(s, "Петя");

        RejectedWith(s, x => x.Bet("Вася", "Петя", 7, 5), RejectionCodes.FeatureDisabled);
    }

    [Fact]
    public void With_items_off_content_inventory_and_use_are_refused_and_write_nothing()
    {
        // The shop sells items: it cannot be on without them (ruleset check)
        var s = Season(rules: r => Without(r, items: false, shop: false));

        RejectedWith(s, x => x.Act(new PublishContent(EconomyScenario.ExamplePack(), "Контент")), RejectionCodes.FeatureDisabled);
        RejectedWith(s, x => x.Act(new AdjustInventory(x.PlayerId("Вася"), "orange", null, "Выдано")), RejectionCodes.FeatureDisabled);
        RejectedWith(s, x => x.Use("Вася", SequentialIds.Make(0x77000000, 1)), RejectionCodes.FeatureDisabled);
    }

    [Fact]
    public void Items_turned_off_mid_season_cannot_be_used_and_stay_held()
    {
        var s = Season();
        var orange = s.Give("Вася", "orange");
        s.WithRuleset(r => Without(r, items: false, shop: false));

        RejectedWith(s, x => x.Use("Вася", orange), RejectionCodes.FeatureDisabled);
        Assert.Equal(["orange"], s.Inventory("Вася"));
    }

    [Fact]
    public void With_the_economy_off_by_default_nothing_of_it_is_accepted()
    {
        // The pinned ruleset, like docs/ruleset.default.json, ships every economy flag off
        var s = Scenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя").Roll("Петя").Start("Петя");

        RejectedWith(s, x => x.RollShop("Вася"), RejectionCodes.FeatureDisabled);
        RejectedWith(s, x => x.Bet("Вася", "Петя", 7, 5), RejectionCodes.FeatureDisabled);
        RejectedWith(s, x => x.Act(new PublishContent(EconomyScenario.ExamplePack(), "Контент")), RejectionCodes.FeatureDisabled);
    }
}

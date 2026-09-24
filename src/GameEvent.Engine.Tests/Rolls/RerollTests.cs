using FsCheck.Xunit;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Rolls;

/// <summary>
/// «Реролл до начала» (SPEC «Реролл, дроп, тех-реролл»: the first after every roll is free, the next ones cost coins or a
/// bad event; D-07, Q-2, D-93): one command writes <see cref="GameRerolled"/> of the given-up games, the payment event and
/// the new roll, which never offers the games just given up. Payment strictly in order: a free reroll of this roll, then
/// the <c>freeRerolls</c> coupon, then <c>roll.rerollCost</c>. The counter <see cref="SeasonPlayer.RerollsThisRoll"/> is 0
/// whenever the player is not Rolling. The pinned test ruleset has <c>freeRerollsPerRoll</c> 1 and a cost of 5 coins.
/// Seed-dependent outcomes run over many seeds.
/// </summary>
public class RerollTests
{
    private const string Coupon = "freeRerolls";

    private static readonly int[] s_seeds = [.. Enumerable.Range(1, 40)];

    private static readonly string[] s_horror = ["Silent Hill", "Alan Wake", "Dead Space", "Outlast", "Amnesia", "Soma", "Visage"];

    private static Func<Ruleset, Ruleset> ChoiceOf(int count) =>
        r => r with { Roll = r.Roll with { ChoiceCount = count } };

    private static Func<Ruleset, Ruleset> FreePerRoll(int count) =>
        r => r with { Roll = r.Roll with { FreeRerollsPerRoll = count } };

    private static Func<Ruleset, Ruleset> CostInCoins(int amount) =>
        r => r with { Roll = r.Roll with { RerollCost = new RerollCost { Kind = RerollCostKind.Coins, Amount = amount } } };

    private static Ruleset BadEventCost(Ruleset r) =>
        r with { Roll = r.Roll with { RerollCost = new RerollCost { Kind = RerollCostKind.BadEvent } } };

    /// <summary>A Horror category of the given games (12 h each) and players Вася and Петя.</summary>
    private static Scenario Horror(int seed, params string[] games) => Horror(seed, r => r, games);

    private static Scenario Horror(int seed, Func<Ruleset, Ruleset> rules, params string[] games)
    {
        var s = Scenario.New(seed: seed).WithRuleset(rules).WithCategory("Horror");
        foreach (var game in games)
        {
            s.WithGame(game, 12, "Horror");
        }

        return s.WithPlayers("Вася", "Петя");
    }

    /// <summary>The admin gives <paramref name="player"/> coins and reroll coupons (through the log).</summary>
    private static Scenario Give(Scenario s, string player, int coins = 0, int coupons = 0)
    {
        if (coins == 0 && coupons == 0)
        {
            return s;
        }

        s.Act(new AdjustPlayer(
            s.PlayerId(player),
            "Стартовый капитал",
            CoinsDelta: coins,
            ResourceDeltas: coupons == 0 ? default : [new ResourceDelta(Coupon, coupons)]));
        ScenarioAssert.Accepted(s);
        return s;
    }

    private static Scenario Reroll(Scenario s, string player) => s.Act(new Reroll(s.PlayerId(player)));

    /// <summary>Rerolls and checks the command was accepted.</summary>
    private static Scenario Rerolled(Scenario s, string player)
    {
        Reroll(s, player);
        ScenarioAssert.Accepted(s);
        return s;
    }

    private static Guid Offered(Scenario s, string player) => s.Player(player).Offer!.GameId;

    private static List<Guid> OptionGames(Scenario s, string player) =>
        [.. s.Player(player).Choice!.Options.Select(o => o.Game!.GameId)];

    // ---- The first reroll after a roll is free ----

    [Fact]
    public void First_reroll_after_a_roll_is_free_and_rolls_another_game_at_once()
    {
        foreach (var seed in s_seeds)
        {
            // Given Вася has coins and a coupon, and was offered one of three horror games
            var s = Give(Horror(seed, "Silent Hill", "Alan Wake", "Dead Space"), "Вася", coins: 7, coupons: 2).Roll("Вася");
            var givenUp = Offered(s, "Вася");
            var before = s.Player("Вася");
            s.Advance(TimeSpan.FromMinutes(3));

            // When he rerolls
            Reroll(s, "Вася");

            // Then exactly GameRerolled([the offer], FreeThisRoll) and a new GameRolled of another game, rolled now
            ScenarioAssert.Accepted(s);
            Assert.Equal(2, s.Last.Events.Count);
            Assert.Equal(new GameRerolled(s.PlayerId("Вася"), [givenUp], RerollPayment.FreeThisRoll), s.Last.Events[0]);
            var rolled = Assert.IsType<GameRolled>(s.Last.Events[1]);
            Assert.Equal(s.PlayerId("Вася"), rolled.PlayerId);
            Assert.NotEqual(givenUp, rolled.GameId);
            Assert.Equal(s.Clock.UtcNow, rolled.RolledAt);

            // And he is Rolling with the new game; one reroll counted; coins, coupons, points and position untouched
            var after = s.Player("Вася");
            Assert.Equal(TurnPhase.Rolling, after.Phase);
            Assert.Equal(rolled.GameId, after.Offer!.GameId);
            Assert.Null(after.Choice);
            Assert.Equal(1, after.RerollsThisRoll);
            Assert.Equal(before.Coins, after.Coins);
            Assert.Equal(before.Resources, after.Resources);
            Assert.Equal(before.Points, after.Points);
            Assert.Equal(before.CellId, after.CellId);
            Assert.Empty(s.State.ManualEffects);

            // A reroll is not «Уже проходил»: nothing is excluded for good
            Assert.Empty(after.Exclusions);
        }
    }

    [Fact]
    public void Roll_starts_the_counter_at_zero()
    {
        var s = Horror(42, "Silent Hill", "Alan Wake").Roll("Вася");

        Assert.Equal(0, s.Player("Вася").RerollsThisRoll);
    }

    [Fact]
    public void Free_reroll_works_with_no_coins_at_all()
    {
        var s = Horror(42, "Silent Hill", "Alan Wake").Roll("Вася");
        Assert.Equal(0, s.Player("Вася").Coins);

        Rerolled(s, "Вася");

        Assert.Equal(RerollPayment.FreeThisRoll, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        Assert.Equal(0, s.Player("Вася").Coins);
    }

    // ---- The new roll never offers a game just given up ----

    [Fact]
    public void New_roll_never_offers_the_game_just_given_up()
    {
        foreach (var seed in s_seeds)
        {
            // Given Вася has coins for many paid rerolls
            var s = Give(Horror(seed, s_horror), "Вася", coins: 100).Roll("Вася");

            for (var i = 0; i < 8; i++)
            {
                var givenUp = Offered(s, "Вася");

                Rerolled(s, "Вася");

                // Then the new game is another one, and the given-up game is not a miss «Сейчас играет Вася» either
                var rolled = Assert.Single(s.LastEvents<GameRolled>());
                Assert.NotEqual(givenUp, rolled.GameId);
                Assert.DoesNotContain(givenUp, rolled.Misses.Select(m => m.GameId));
                Assert.DoesNotContain(rolled.Misses, m => m.ByPlayerId == s.PlayerId("Вася"));
            }
        }
    }

    [Fact]
    public void Game_given_up_before_the_last_reroll_can_come_back()
    {
        // Given two games: Вася is offered one, rerolls for free and gets the other
        var s = Give(Horror(42, "Silent Hill", "Alan Wake"), "Вася", coins: 5).Roll("Вася");
        var first = Offered(s, "Вася");
        Rerolled(s, "Вася");
        var second = Offered(s, "Вася");
        Assert.NotEqual(first, second);

        // When he rerolls again (paid), only the games just given up are left out (D-93), not every earlier one
        Rerolled(s, "Вася");

        Assert.Equal(first, Offered(s, "Вася"));
        Assert.Equal(RerollPayment.Coins, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
    }

    [Fact]
    public void Given_up_game_is_free_for_other_players()
    {
        // Given two games: Вася gave up his first one and holds the other
        var s = Horror(42, "Silent Hill", "Alan Wake").Roll("Вася");
        var givenUp = Offered(s, "Вася");
        Rerolled(s, "Вася");

        // When Петя rolls, he gets the given-up game: a reroll releases it
        s.Roll("Петя");

        var rolled = Assert.Single(s.LastEvents<GameRolled>());
        Assert.Equal(givenUp, rolled.GameId);
        Assert.Empty(s.Player("Петя").Exclusions);
    }

    [Fact]
    public void Reroll_spins_the_category_wheel_again()
    {
        var checkedSeeds = 0;
        foreach (var seed in s_seeds)
        {
            // Given heavy Horror has only Silent Hill and light Puzzle has two games
            var s = Scenario.New(seed: seed)
                .WithCategory("Horror", weight: 20).WithGame("Silent Hill", 12, "Horror")
                .WithCategory("Puzzle", weight: 1).WithGame("Tetris", 2, "Puzzle").WithGame("Portal", 5, "Puzzle")
                .WithPlayers("Вася")
                .Roll("Вася");
            if (Offered(s, "Вася") != s.GameId("Silent Hill"))
            {
                continue;
            }

            checkedSeeds++;

            // When Вася rerolls Silent Hill, Horror has nothing else: the new game comes from Puzzle
            Rerolled(s, "Вася");

            var rolled = Assert.Single(s.LastEvents<GameRolled>());
            Assert.Equal("Puzzle", rolled.Category);
            Assert.Contains(rolled.GameId, new[] { s.GameId("Tetris"), s.GameId("Portal") });
        }

        Assert.True(checkedSeeds >= 10, $"Over many seeds the heavy Horror must usually come first, got {checkedSeeds}.");
    }

    // ---- Nothing else to roll: refused, nothing spent ----

    [Fact]
    public void Reroll_of_the_only_available_game_is_rejected_and_spends_nothing()
    {
        // Given the pool has one game, offered to Вася, who has coins and a coupon
        var s = Give(Horror(42, "Silent Hill"), "Вася", coins: 20, coupons: 1).Roll("Вася");

        // Then the reroll is refused: no events, the offer and the counter stay
        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.NoAvailableGames);
        Assert.Equal(s.GameId("Silent Hill"), Offered(s, "Вася"));
        Assert.Equal(0, s.Player("Вася").RerollsThisRoll);
    }

    [Fact]
    public void Paid_reroll_with_nothing_else_available_is_rejected_and_spends_nothing()
    {
        // Given two games: Вася used his free reroll, then Петя took the game Вася gave up
        var s = Give(Horror(42, "Silent Hill", "Alan Wake"), "Вася", coins: 10, coupons: 1).Roll("Вася");
        Rerolled(s, "Вася");
        s.Roll("Петя");

        // When Вася rerolls again: the other game is busy, nothing is left for him
        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.NoAvailableGames);

        // Then neither the coupon nor coins are spent, the counter stays at 1
        var player = s.Player("Вася");
        Assert.Equal(10, player.Coins);
        Assert.Equal(1, player.Resources[Coupon]);
        Assert.Equal(1, player.RerollsThisRoll);
    }

    [Fact]
    public void Nothing_else_to_roll_beats_not_enough_coins()
    {
        // D-93: coins cannot help when there is nothing else to roll; that check comes before the payment
        var s = Horror(42, FreePerRoll(0), "Silent Hill").Roll("Вася");
        Assert.Equal(0, s.Player("Вася").Coins);

        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.NoAvailableGames);
    }

    [Fact]
    public void Nothing_else_to_roll_beats_not_enough_coins_after_the_free_reroll()
    {
        // Given two games: Вася used his free reroll with no coins, then Петя took the game Вася gave up
        var s = Horror(42, "Silent Hill", "Alan Wake").Roll("Вася");
        Rerolled(s, "Вася");
        s.Roll("Петя");

        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.NoAvailableGames);
    }

    [Fact]
    public void Reroll_with_every_other_game_busy_is_rejected()
    {
        // Given Петя holds one of two games and Вася was offered the other
        var s = Horror(42, "Silent Hill", "Alan Wake").Roll("Петя").Roll("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.NoAvailableGames);
    }

    [Fact]
    public void Reroll_with_every_other_game_completed_in_the_season_is_rejected()
    {
        // Given Петя completed one of two games; Вася was offered the other
        var s = Horror(42, "Silent Hill", "Alan Wake").Roll("Петя").Start("Петя").Complete("Петя").Roll("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.NoAvailableGames);
    }

    // ---- The coupon: after the free reroll, before coins (D-07) ----

    [Fact]
    public void Second_reroll_spends_a_coupon_before_coins()
    {
        foreach (var seed in s_seeds.Take(10))
        {
            // Given Вася has 10 coins and 2 coupons and used the free reroll
            var s = Give(Horror(seed, s_horror), "Вася", coins: 10, coupons: 2).Roll("Вася");
            Rerolled(s, "Вася");
            var givenUp = Offered(s, "Вася");

            // When he rerolls again
            Rerolled(s, "Вася");

            // Then GameRerolled(FreeRerollResource), ResourceChanged(freeRerolls, -1, Reroll), the new roll
            Assert.Equal(3, s.Last.Events.Count);
            Assert.Equal(new GameRerolled(s.PlayerId("Вася"), [givenUp], RerollPayment.FreeRerollResource), s.Last.Events[0]);
            Assert.Equal(new ResourceChanged(s.PlayerId("Вася"), Coupon, -1, ResourceReason.Reroll), s.Last.Events[1]);
            var rolled = Assert.IsType<GameRolled>(s.Last.Events[2]);
            Assert.NotEqual(givenUp, rolled.GameId);

            // And coins are untouched, one coupon left, two rerolls counted
            var player = s.Player("Вася");
            Assert.Equal(10, player.Coins);
            Assert.Equal(1, player.Resources[Coupon]);
            Assert.Equal(2, player.RerollsThisRoll);
            Assert.Empty(s.LastEvents<CoinsChanged>());
        }
    }

    [Fact]
    public void Coupon_is_not_spent_while_a_free_reroll_is_left()
    {
        var s = Give(Horror(42, s_horror), "Вася", coupons: 1).Roll("Вася");

        Rerolled(s, "Вася");

        Assert.Equal(RerollPayment.FreeThisRoll, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        Assert.Empty(s.LastEvents<ResourceChanged>());
        Assert.Equal(1, s.Player("Вася").Resources[Coupon]);
    }

    [Fact]
    public void Coupon_works_with_no_coins()
    {
        var s = Give(Horror(42, s_horror), "Вася", coupons: 1).Roll("Вася");
        Rerolled(s, "Вася");

        Rerolled(s, "Вася");

        Assert.Equal(RerollPayment.FreeRerollResource, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        Assert.Equal(0, s.Player("Вася").Coins);
        Assert.Equal(0, s.Player("Вася").Resources[Coupon]);
        Assert.DoesNotContain(s.Player("Вася").Resources, r => r.Key == Coupon); // no zero entries in the bag
    }

    [Fact]
    public void Coupons_run_out_then_coins_are_spent()
    {
        // Given one coupon and 10 coins
        var s = Give(Horror(42, s_horror), "Вася", coins: 10, coupons: 1).Roll("Вася");

        // When Вася rerolls four times
        var payments = new List<RerollPayment>();
        for (var i = 0; i < 4; i++)
        {
            Rerolled(s, "Вася");
            payments.Add(Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        }

        // Then free, coupon, coins, coins — and the fifth is refused: 0 coins left
        Assert.Equal([RerollPayment.FreeThisRoll, RerollPayment.FreeRerollResource, RerollPayment.Coins, RerollPayment.Coins], payments);
        Assert.Equal(0, s.Player("Вася").Coins);
        Assert.Equal(4, s.Player("Вася").RerollsThisRoll);
        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.NotEnoughCoins);
    }

    [Fact]
    public void Coupon_is_spent_before_a_bad_event()
    {
        var s = Give(Horror(42, BadEventCost, s_horror), "Вася", coupons: 1).Roll("Вася");
        Rerolled(s, "Вася");

        Rerolled(s, "Вася");

        Assert.Equal(RerollPayment.FreeRerollResource, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        Assert.Empty(s.LastEvents<ManualEffectCreated>());
        Assert.Empty(s.State.ManualEffects);
    }

    // ---- Coins (Q-2) ----

    [Fact]
    public void Paid_reroll_costs_the_coins_of_the_ruleset()
    {
        foreach (var seed in s_seeds.Take(10))
        {
            // Given Вася has 7 coins, no coupons, and used the free reroll
            var s = Give(Horror(seed, s_horror), "Вася", coins: 7).Roll("Вася");
            Rerolled(s, "Вася");
            var givenUp = Offered(s, "Вася");

            // When he rerolls again
            Rerolled(s, "Вася");

            // Then GameRerolled(Coins), CoinsChanged(-5, Reroll, no run), the new roll
            Assert.Equal(3, s.Last.Events.Count);
            Assert.Equal(new GameRerolled(s.PlayerId("Вася"), [givenUp], RerollPayment.Coins), s.Last.Events[0]);
            Assert.Equal(new CoinsChanged(s.PlayerId("Вася"), -5, CoinsReason.Reroll, RunId: null), s.Last.Events[1]);
            Assert.NotEqual(givenUp, Assert.IsType<GameRolled>(s.Last.Events[2]).GameId);
            Assert.Equal(2, s.Player("Вася").Coins);
            Assert.Equal(2, s.Player("Вася").RerollsThisRoll);
        }
    }

    [Fact]
    public void Exactly_enough_coins_pay_for_a_reroll_down_to_zero()
    {
        var s = Give(Horror(42, s_horror), "Вася", coins: 5).Roll("Вася");
        Rerolled(s, "Вася");

        Rerolled(s, "Вася");

        Assert.Equal(RerollPayment.Coins, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        Assert.Equal(0, s.Player("Вася").Coins);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Not_enough_coins_is_rejected_without_events(int coins)
    {
        // Given Вася used his free reroll and has fewer coins than a reroll costs
        var s = Give(Horror(42, s_horror), "Вася", coins: coins).Roll("Вася");
        Rerolled(s, "Вася");
        var offer = s.Player("Вася").Offer;

        // Then the paid reroll is refused and nothing changes: coins never go negative for a reroll
        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.NotEnoughCoins);
        Assert.Equal(coins, s.Player("Вася").Coins);
        Assert.Equal(offer, s.Player("Вася").Offer);
        Assert.Equal(1, s.Player("Вася").RerollsThisRoll);
    }

    [Fact]
    public void Player_in_debt_cannot_pay_for_a_reroll_even_though_the_ruleset_allows_negative_coins()
    {
        // allowNegativeCoins is about effects; a reroll is a purchase, and purchases are never made in debt
        var s = Horror(42, s_horror);
        Assert.True(s.Ruleset.Economy.AllowNegativeCoins);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Штраф", CoinsDelta: -3));
        ScenarioAssert.Accepted(s);
        s.Roll("Вася");
        Rerolled(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.NotEnoughCoins);
        Assert.Equal(-3, s.Player("Вася").Coins);
    }

    [Fact]
    public void Reroll_cost_follows_the_ruleset_amount()
    {
        var s = Give(Horror(42, CostInCoins(3), s_horror), "Вася", coins: 4).Roll("Вася");
        Rerolled(s, "Вася");

        Rerolled(s, "Вася");

        Assert.Equal(new CoinsChanged(s.PlayerId("Вася"), -3, CoinsReason.Reroll, null), Assert.Single(s.LastEvents<CoinsChanged>()));
        Assert.Equal(1, s.Player("Вася").Coins);
    }

    [Fact]
    public void Reroll_is_paid_by_the_rules_in_force_at_the_reroll_not_at_the_roll()
    {
        // SPEC «Сезон»: only what concerns the run is fixed at the roll; the rest comes from the current ruleset
        var s = Give(Horror(42, s_horror), "Вася", coins: 4).Roll("Вася");
        Rerolled(s, "Вася");

        // When the admin lowers the cost to 2 coins before the paid reroll
        s.WithRuleset(CostInCoins(2));
        Rerolled(s, "Вася");

        Assert.Equal(-2, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
        Assert.Equal(2, s.Player("Вася").Coins);
    }

    // ---- A zero price (rerollCost {kind: coins, amount: 0}, D-93) ----

    [Fact]
    public void Paid_reroll_priced_at_zero_coins_writes_no_coins_change_and_works_with_no_coins()
    {
        foreach (var seed in s_seeds.Take(10))
        {
            // Given the paid reroll costs 0 coins; Вася has no coins, no coupons and used the free reroll
            var s = Horror(seed, CostInCoins(0), s_horror).Roll("Вася");
            Rerolled(s, "Вася");
            var givenUp = Offered(s, "Вася");

            // When he rerolls again
            Reroll(s, "Вася");

            // Then exactly GameRerolled(Coins) and the new roll: no CoinsChanged of 0
            ScenarioAssert.Accepted(s);
            Assert.Equal(2, s.Last.Events.Count);
            Assert.Equal(new GameRerolled(s.PlayerId("Вася"), [givenUp], RerollPayment.Coins), s.Last.Events[0]);
            Assert.NotEqual(givenUp, Assert.IsType<GameRolled>(s.Last.Events[1]).GameId);
            Assert.Equal(0, s.Player("Вася").Coins);
            Assert.Equal(2, s.Player("Вася").RerollsThisRoll);

            // And the next one costs nothing again
            Rerolled(s, "Вася");
            Assert.Equal(RerollPayment.Coins, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
            Assert.Empty(s.LastEvents<CoinsChanged>());
        }
    }

    [Fact]
    public void Coupon_is_spent_before_a_zero_coin_price()
    {
        // D-93: the order is literal, the coupon goes first even when coins would cost nothing
        var s = Give(Horror(42, CostInCoins(0), s_horror), "Вася", coupons: 1).Roll("Вася");
        Rerolled(s, "Вася");

        Rerolled(s, "Вася");

        Assert.Equal(RerollPayment.FreeRerollResource, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        Assert.Equal(
            new ResourceChanged(s.PlayerId("Вася"), Coupon, -1, ResourceReason.Reroll),
            Assert.Single(s.LastEvents<ResourceChanged>()));
        Assert.Equal(0, s.Player("Вася").Resources[Coupon]);

        // Coupons gone: the next reroll is paid in (zero) coins
        Rerolled(s, "Вася");
        Assert.Equal(RerollPayment.Coins, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
    }

    // ---- A bad event (rerollCost.kind = badEvent, Q-2, D-10) ----

    [Fact]
    public void Paid_reroll_with_a_bad_event_creates_a_manual_effect()
    {
        foreach (var seed in s_seeds.Take(10))
        {
            // Given the paid reroll costs a bad event; Вася has no coins and used the free reroll
            var s = Horror(seed, BadEventCost, s_horror).Roll("Вася");
            Rerolled(s, "Вася");
            var givenUp = Offered(s, "Вася");

            // When he rerolls again
            Rerolled(s, "Вася");

            // Then GameRerolled(BadEvent), ManualEffectCreated(new id, Вася, Bad, PaidReroll, no run), the new roll
            Assert.Equal(3, s.Last.Events.Count);
            Assert.Equal(new GameRerolled(s.PlayerId("Вася"), [givenUp], RerollPayment.BadEvent), s.Last.Events[0]);
            var created = Assert.IsType<ManualEffectCreated>(s.Last.Events[1]);
            Assert.NotEqual(Guid.Empty, created.EffectId);
            Assert.Equal(
                new ManualEffectCreated(created.EffectId, s.PlayerId("Вася"), EventKind.Bad, ManualEffectSource.PaidReroll, RunId: null),
                created);
            Assert.NotEqual(givenUp, Assert.IsType<GameRolled>(s.Last.Events[2]).GameId);

            // And the effect waits in the season state; coins are untouched
            var effect = Assert.Single(s.State.ManualEffects);
            Assert.Equal(created.EffectId, effect.Key);
            Assert.Equal(new PendingManualEffect(created.EffectId, s.PlayerId("Вася"), EventKind.Bad, ManualEffectSource.PaidReroll, null), effect.Value);
            Assert.Equal(0, s.Player("Вася").Coins);
            Assert.Empty(s.LastEvents<CoinsChanged>());
            Assert.Equal(2, s.Player("Вася").RerollsThisRoll);
        }
    }

    [Fact]
    public void Every_paid_reroll_with_a_bad_event_creates_its_own_effect()
    {
        var s = Horror(42, BadEventCost, s_horror).Roll("Вася");
        Rerolled(s, "Вася");

        Rerolled(s, "Вася");
        Rerolled(s, "Вася");
        Rerolled(s, "Вася");

        var created = s.Log.OfType<ManualEffectCreated>().ToList();
        Assert.Equal(3, created.Count);
        Assert.Equal(3, created.Select(e => e.EffectId).Distinct().Count());
        Assert.Equal(created.Select(e => e.EffectId).Order(), s.State.ManualEffects.Keys);
        Assert.All(s.State.ManualEffects.Values, e => Assert.Equal(s.PlayerId("Вася"), e.PlayerId));
    }

    [Fact]
    public void Free_reroll_creates_no_manual_effect_under_a_bad_event_cost()
    {
        var s = Horror(42, BadEventCost, s_horror).Roll("Вася");

        Rerolled(s, "Вася");

        Assert.Equal(2, s.Last.Events.Count);
        Assert.Empty(s.State.ManualEffects);
    }

    [Fact]
    public void Manual_effects_belong_to_the_player_who_paid()
    {
        var s = Horror(42, r => BadEventCost(FreePerRoll(0)(r)), s_horror).Roll("Вася").Roll("Петя");

        Rerolled(s, "Петя");

        Assert.Equal(s.PlayerId("Петя"), Assert.Single(s.State.ManualEffects).Value.PlayerId);
    }

    // ---- roll.freeRerollsPerRoll ----

    [Fact]
    public void Without_free_rerolls_the_first_reroll_is_already_paid()
    {
        var s = Give(Horror(42, FreePerRoll(0), s_horror), "Вася", coins: 5).Roll("Вася");

        Rerolled(s, "Вася");

        Assert.Equal(RerollPayment.Coins, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        Assert.Equal(-5, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
        Assert.Equal(1, s.Player("Вася").RerollsThisRoll);
    }

    [Fact]
    public void Without_free_rerolls_and_coins_the_first_reroll_is_rejected()
    {
        var s = Horror(42, FreePerRoll(0), s_horror).Roll("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.NotEnoughCoins);
        Assert.Equal(0, s.Player("Вася").RerollsThisRoll);
    }

    [Fact]
    public void Without_free_rerolls_the_coupon_pays_the_first_reroll()
    {
        var s = Give(Horror(42, FreePerRoll(0), s_horror), "Вася", coins: 5, coupons: 1).Roll("Вася");

        Rerolled(s, "Вася");

        Assert.Equal(RerollPayment.FreeRerollResource, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        Assert.Equal(5, s.Player("Вася").Coins);
    }

    [Fact]
    public void Two_free_rerolls_per_roll_are_both_free_and_the_third_is_paid()
    {
        var s = Give(Horror(42, FreePerRoll(2), s_horror), "Вася", coins: 5).Roll("Вася");

        Rerolled(s, "Вася");
        Assert.Equal(RerollPayment.FreeThisRoll, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        Rerolled(s, "Вася");
        Assert.Equal(RerollPayment.FreeThisRoll, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        Assert.Equal(5, s.Player("Вася").Coins);

        Rerolled(s, "Вася");
        Assert.Equal(RerollPayment.Coins, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        Assert.Equal(0, s.Player("Вася").Coins);
        Assert.Equal(3, s.Player("Вася").RerollsThisRoll);
    }

    // ---- The counter: every roll gives its own free rerolls ----

    [Fact]
    public void After_starting_the_game_the_counter_is_zero_and_the_next_roll_has_a_free_reroll_again()
    {
        // Given Вася used the free reroll and a paid one, then started the game
        var s = Give(Horror(42, s_horror), "Вася", coins: 5).Roll("Вася");
        Rerolled(s, "Вася");
        Rerolled(s, "Вася");
        Assert.Equal(2, s.Player("Вася").RerollsThisRoll);

        s.Start("Вася");

        // Then the counter is 0 while playing
        Assert.Equal(0, s.Player("Вася").RerollsThisRoll);

        // When he completes, rolls again and rerolls with 0 coins
        s.Complete("Вася").Roll("Вася");
        Assert.Equal(0, s.Player("Вася").RerollsThisRoll);
        Rerolled(s, "Вася");

        // Then that reroll is free: a new roll has its own free rerolls
        Assert.Equal(RerollPayment.FreeThisRoll, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        Assert.Equal(1, s.Player("Вася").RerollsThisRoll);
    }

    [Fact]
    public void After_already_played_the_counter_is_zero_and_the_next_reroll_is_free()
    {
        // D-92, D-93: the free roll after «Уже проходил» is a new roll with its own free rerolls
        var s = Horror(42, s_horror).Roll("Вася");
        Rerolled(s, "Вася");
        Assert.Equal(1, s.Player("Вася").RerollsThisRoll);

        s.Act(new DeclareAlreadyPlayed(s.PlayerId("Вася"), Offered(s, "Вася")));
        ScenarioAssert.Accepted(s);

        Assert.Equal(TurnPhase.Rolling, s.Player("Вася").Phase);
        Assert.Equal(0, s.Player("Вася").RerollsThisRoll);
        Rerolled(s, "Вася");
        Assert.Equal(RerollPayment.FreeThisRoll, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
    }

    [Fact]
    public void After_already_played_with_nothing_left_the_player_is_idle_with_zero_rerolls()
    {
        // Given two games: Вася rerolled once, and Петя holds the game Вася gave up
        var s = Horror(42, "Silent Hill", "Alan Wake").Roll("Вася");
        Rerolled(s, "Вася");
        s.Roll("Петя");

        // When Вася declares his game «Уже проходил», nothing is left to roll
        s.Act(new DeclareAlreadyPlayed(s.PlayerId("Вася"), Offered(s, "Вася")));
        ScenarioAssert.Accepted(s);

        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
        Assert.Equal(0, s.Player("Вася").RerollsThisRoll);
    }

    [Fact]
    public void After_an_admin_discard_the_counter_is_zero_and_the_next_roll_has_a_free_reroll()
    {
        var s = Horror(42, s_horror).Roll("Вася");
        Rerolled(s, "Вася");

        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Сброс", DiscardOffer: true));
        ScenarioAssert.Accepted(s);

        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
        Assert.Equal(0, s.Player("Вася").RerollsThisRoll);
        s.Roll("Вася");
        Rerolled(s, "Вася");
        Assert.Equal(RerollPayment.FreeThisRoll, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
    }

    [Fact]
    public void After_choosing_from_a_rerolled_choice_the_counter_is_zero()
    {
        var s = Horror(42, ChoiceOf(3), s_horror).Roll("Вася");
        Rerolled(s, "Вася");
        var choice = s.Player("Вася").Choice!;

        s.Act(new MakeChoice(s.PlayerId("Вася"), choice.ChoiceId, choice.Options[0].Id));
        ScenarioAssert.Accepted(s);

        Assert.Equal(TurnPhase.Playing, s.Player("Вася").Phase);
        Assert.Equal(0, s.Player("Вася").RerollsThisRoll);
    }

    [Fact]
    public void Rerolls_of_one_player_do_not_touch_another()
    {
        // Given both rolled; Вася rerolls three times
        var s = Give(Horror(42, s_horror), "Вася", coins: 10).Roll("Вася").Roll("Петя");
        var petya = s.Player("Петя");
        Rerolled(s, "Вася");
        Rerolled(s, "Вася");
        Rerolled(s, "Вася");

        // Then Петя still has his free reroll; his state is untouched except that no game of his was given up
        Assert.Equal(petya, s.Player("Петя"));
        Rerolled(s, "Петя");
        Assert.Equal(RerollPayment.FreeThisRoll, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
    }

    // ---- Misses are free and are not rerolls (D-07, D-46) ----

    [Fact]
    public void Misses_of_a_roll_are_not_rerolls()
    {
        var sawMiss = false;
        foreach (var seed in s_seeds)
        {
            // Given Петя holds one of five games; Вася's roll may hit it and log a miss
            var s = Horror(seed, ChoiceOf(1), "Silent Hill", "Alan Wake").Roll("Петя");
            s.WithGame("Dead Space", 12, "Horror").WithGame("Outlast", 12, "Horror").WithGame("Amnesia", 12, "Horror");
            s.Roll("Вася");
            var rolled = Assert.Single(s.LastEvents<GameRolled>());
            sawMiss |= rolled.Misses.Count > 0;

            // Then whatever the misses, no reroll is counted and the first reroll is free (0 coins)
            Assert.Equal(0, s.Player("Вася").RerollsThisRoll);
            Rerolled(s, "Вася");
            Assert.Equal(RerollPayment.FreeThisRoll, Assert.Single(s.LastEvents<GameRerolled>()).Payment);

            // And misses of the new roll do not count either: one reroll, one step of the counter
            Assert.Equal(1, s.Player("Вася").RerollsThisRoll);
        }

        Assert.True(sawMiss, "Over many seeds the wheel must sometimes hit Петя's game.");
    }

    // ---- On a pending choice: the whole choice is given up (D-93) ----

    [Fact]
    public void Reroll_on_a_pending_choice_gives_up_every_option_and_rolls_a_new_choice_without_them()
    {
        foreach (var seed in s_seeds)
        {
            // Given Вася waits for a choice of 3 out of 7 horror games
            var s = Horror(seed, ChoiceOf(3), s_horror).Roll("Вася");
            var oldChoice = s.Player("Вася").Choice!;
            var givenUp = OptionGames(s, "Вася");

            // When he rerolls (the pending choice does not block it)
            Reroll(s, "Вася");

            // Then GameRerolled lists every option's game, and the new choice has none of them
            ScenarioAssert.Accepted(s);
            Assert.Equal(2, s.Last.Events.Count);
            var rerolled = Assert.IsType<GameRerolled>(s.Last.Events[0]);
            Assert.Equal(s.PlayerId("Вася"), rerolled.PlayerId);
            Assert.Equal(RerollPayment.FreeThisRoll, rerolled.Payment);
            Assert.Equal(givenUp.Order(), rerolled.GameIds.Order());
            var rolled = Assert.IsType<GameChoiceRolled>(s.Last.Events[1]);
            Assert.NotEqual(oldChoice.ChoiceId, rolled.ChoiceId);
            Assert.Equal(3, rolled.Offers.Count);
            Assert.DoesNotContain(rolled.Offers, o => givenUp.Contains(o.GameId));
            Assert.DoesNotContain(rolled.Misses, m => givenUp.Contains(m.GameId));

            var player = s.Player("Вася");
            Assert.Equal(TurnPhase.Rolling, player.Phase);
            Assert.Null(player.Offer);
            Assert.Equal(rolled.ChoiceId, player.Choice!.ChoiceId);
            Assert.Equal(1, player.RerollsThisRoll);
        }
    }

    [Fact]
    public void Reroll_on_a_choice_with_one_other_game_left_is_a_plain_roll()
    {
        // Given four games and a choice of three: only one game is left after giving up the choice
        var s = Horror(42, ChoiceOf(3), "Silent Hill", "Alan Wake", "Dead Space", "Outlast").Roll("Вася");
        var givenUp = OptionGames(s, "Вася");
        var left = new[] { "Silent Hill", "Alan Wake", "Dead Space", "Outlast" }.Select(s.GameId).Single(g => !givenUp.Contains(g));

        Rerolled(s, "Вася");

        var rolled = Assert.IsType<GameRolled>(s.Last.Events[1]);
        Assert.Equal(left, rolled.GameId);
        Assert.Null(s.Player("Вася").Choice);
        Assert.Equal(left, Offered(s, "Вася"));
    }

    [Fact]
    public void Reroll_on_a_choice_with_nothing_else_available_is_rejected()
    {
        // Given three games, all in Вася's choice
        var s = Horror(42, ChoiceOf(3), "Silent Hill", "Alan Wake", "Dead Space").Roll("Вася");
        var choice = s.Player("Вася").Choice;

        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.NoAvailableGames);
        Assert.Equal(choice, s.Player("Вася").Choice);
    }

    [Fact]
    public void Paid_reroll_of_a_choice_follows_the_same_payment_order()
    {
        var s = Give(Horror(42, ChoiceOf(2), s_horror), "Вася", coins: 5, coupons: 1).Roll("Вася");

        var payments = new List<RerollPayment>();
        for (var i = 0; i < 3; i++)
        {
            Rerolled(s, "Вася");
            payments.Add(Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        }

        Assert.Equal([RerollPayment.FreeThisRoll, RerollPayment.FreeRerollResource, RerollPayment.Coins], payments);
        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.NotEnoughCoins);
    }

    // ---- Refused ----

    [Fact]
    public void Reroll_while_idle_is_rejected_with_wrong_phase()
    {
        var s = Give(Horror(42, s_horror), "Вася", coins: 10, coupons: 1);

        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.WrongPhase);
    }

    [Fact]
    public void Reroll_while_playing_is_rejected_with_wrong_phase()
    {
        // Once started it is too late: that is a drop or a tech reroll (C6b)
        var s = Give(Horror(42, s_horror), "Вася", coins: 10).Roll("Вася").Start("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.WrongPhase);
    }

    [Fact]
    public void Reroll_by_an_unknown_player_is_rejected()
    {
        var s = Horror(42, s_horror).Roll("Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new Reroll(SequentialIds.Make(0x0BAD0000, 1))), RejectionCodes.PlayerUnknown);
    }

    [Fact]
    public void Reroll_in_a_closing_season_is_rejected_before_anything_else()
    {
        // Closing: rolls are forbidden (SPEC «Сезон»). The season check comes first, before the payment
        var s = Horror(42, s_horror).Roll("Вася");
        Rerolled(s, "Вася");
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.SeasonNotActive);
    }

    [Fact]
    public void Reroll_in_a_draft_season_is_rejected()
    {
        var s = Scenario.New().AsDraft().WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.SeasonNotActive);
    }

    [Fact]
    public void Wrong_phase_beats_not_enough_coins()
    {
        // Idle without coins and free rerolls: the phase check comes first (D-91)
        var s = Horror(42, FreePerRoll(0), s_horror);

        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.WrongPhase);
    }

    [Fact]
    public void Reroll_from_a_second_tab_is_a_second_reroll_and_must_be_paid()
    {
        // Reroll names no game: a click in a stale second tab is another reroll, paid in order; only the command id
        // guards a repeated request of one click (API idempotency)
        var s = Horror(42, s_horror).Roll("Вася");
        Rerolled(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.NotEnoughCoins);
    }

    // ---- RerollPrice.Next: one function for the command and the screen (D-93) ----

    private static RollRules Rules(int freePerRoll, RerollCostKind kind, int? amount) =>
        TestRuleset.Create().Roll with
        {
            FreeRerollsPerRoll = freePerRoll,
            RerollCost = new RerollCost { Kind = kind, Amount = amount },
        };

    [Theory]
    [InlineData(0, 0, RerollCostKind.Coins, 5)]
    [InlineData(0, 3, RerollCostKind.Coins, 5)]
    [InlineData(0, 0, RerollCostKind.BadEvent, null)]
    [InlineData(1, 2, RerollCostKind.Coins, 0)]
    public void Next_is_free_while_free_rerolls_of_this_roll_are_left(int rerolls, int coupons, RerollCostKind kind, int? amount) =>
        Assert.Equal((RerollPayment.FreeThisRoll, 0), RerollPrice.Next(rerolls, coupons, Rules(2, kind, amount)));

    [Theory]
    [InlineData(1, 1, RerollCostKind.Coins, 5)]
    [InlineData(3, 4, RerollCostKind.Coins, 5)]
    [InlineData(1, 1, RerollCostKind.BadEvent, null)]
    [InlineData(1, 1, RerollCostKind.Coins, 0)]
    public void Next_spends_a_coupon_once_the_free_rerolls_are_used(int rerolls, int coupons, RerollCostKind kind, int? amount) =>
        Assert.Equal((RerollPayment.FreeRerollResource, 0), RerollPrice.Next(rerolls, coupons, Rules(1, kind, amount)));

    [Fact]
    public void Next_with_no_free_rerolls_per_roll_goes_straight_to_the_coupon_or_the_cost()
    {
        Assert.Equal((RerollPayment.FreeRerollResource, 0), RerollPrice.Next(0, 1, Rules(0, RerollCostKind.Coins, 5)));
        Assert.Equal((RerollPayment.Coins, 5), RerollPrice.Next(0, 0, Rules(0, RerollCostKind.Coins, 5)));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(4, 0)]
    public void Next_costs_a_bad_event_with_no_coupons(int rerolls, int coupons) =>
        Assert.Equal((RerollPayment.BadEvent, 0), RerollPrice.Next(rerolls, coupons, Rules(1, RerollCostKind.BadEvent, null)));

    [Theory]
    [InlineData(5)]
    [InlineData(3)]
    [InlineData(0)]
    public void Next_costs_the_amount_of_coins_with_no_coupons(int amount) =>
        Assert.Equal((RerollPayment.Coins, amount), RerollPrice.Next(1, 0, Rules(1, RerollCostKind.Coins, amount)));

    [Fact]
    public void Next_counts_a_missing_amount_as_zero_coins() =>
        Assert.Equal((RerollPayment.Coins, 0), RerollPrice.Next(1, 0, Rules(1, RerollCostKind.Coins, null)));

    [Fact]
    public void Next_rejects_null_rules() =>
        Assert.Throws<ArgumentNullException>(() => RerollPrice.Next(0, 0, null!));

    [Property(MaxTest = 100)]
    public void Reroll_charges_exactly_what_next_returns(
        int seed, byte freePerRoll, byte coupons, byte coins, bool badEvent, byte amount, byte rerolls)
    {
        // Given random free rerolls per roll, coupons, coins and cost
        var cost = amount % 7;
        var rules = (Ruleset r) => r with
        {
            Roll = r.Roll with
            {
                FreeRerollsPerRoll = freePerRoll % 3,
                RerollCost = badEvent
                    ? new RerollCost { Kind = RerollCostKind.BadEvent }
                    : new RerollCost { Kind = RerollCostKind.Coins, Amount = cost },
            },
        };
        var s = Give(Horror(seed, rules, s_horror), "Вася", coins: coins % 13, coupons: coupons % 3).Roll("Вася");

        // When Вася rerolls several times, each reroll is paid exactly as RerollPrice.Next said just before it
        for (var i = 0; i < (rerolls % 6) + 1; i++)
        {
            var before = s.Player("Вася");
            var price = RerollPrice.Next(before.RerollsThisRoll, before.Resources[Coupon], s.Ruleset.Roll);
            var effectsBefore = s.State.ManualEffects.Count;

            if (price.Payment == RerollPayment.Coins && price.Coins > before.Coins)
            {
                ScenarioAssert.RejectsWithoutChanges(s, x => Reroll(x, "Вася"), RejectionCodes.NotEnoughCoins);
                return;
            }

            Rerolled(s, "Вася");

            var after = s.Player("Вася");
            Assert.Equal(price.Payment, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
            Assert.Equal(before.Coins - price.Coins, after.Coins);
            Assert.Equal(
                price.Payment == RerollPayment.FreeRerollResource ? before.Resources[Coupon] - 1 : before.Resources[Coupon],
                after.Resources[Coupon]);
            Assert.Equal(price.Payment == RerollPayment.BadEvent ? effectsBefore + 1 : effectsBefore, s.State.ManualEffects.Count);
            if (price.Coins == 0)
            {
                Assert.Empty(s.LastEvents<CoinsChanged>());
            }
            else
            {
                Assert.Equal(
                    new CoinsChanged(s.PlayerId("Вася"), -price.Coins, CoinsReason.Reroll, null),
                    Assert.Single(s.LastEvents<CoinsChanged>()));
            }
        }
    }

    // ---- Log and state ----

    [Fact]
    public void Replaying_the_log_gives_the_same_state_with_counters_and_manual_effects()
    {
        foreach (var seed in s_seeds.Take(10))
        {
            // Given coins, a coupon and a switch to a bad event cost in the middle
            var s = Give(Horror(seed, s_horror), "Вася", coins: 5, coupons: 1).Roll("Вася").Roll("Петя");
            Rerolled(s, "Вася");
            Rerolled(s, "Вася");
            Rerolled(s, "Вася");
            s.WithRuleset(BadEventCost);
            Rerolled(s, "Вася");
            Rerolled(s, "Петя");

            var replayed = SeasonEngine.Replay(s.Log);

            Assert.Equal(s.State, replayed);
            Assert.Equal(4, replayed.Players[s.PlayerId("Вася")].RerollsThisRoll);
            Assert.Equal(1, replayed.Players[s.PlayerId("Петя")].RerollsThisRoll);
            Assert.Equal(s.State.ManualEffects, replayed.ManualEffects);
            Assert.Single(replayed.ManualEffects);
            Assert.Equal(0, replayed.Players[s.PlayerId("Вася")].Coins);
            Assert.Equal(0, replayed.Players[s.PlayerId("Вася")].Resources[Coupon]);
        }
    }

    [Fact]
    public void Folding_the_events_alone_updates_the_counter_offer_and_effects()
    {
        // The state follows from the events, not from the command: apply them one by one
        var s = Horror(42, r => BadEventCost(FreePerRoll(0)(r)), s_horror).Roll("Вася");
        var before = s.State;
        Rerolled(s, "Вася");

        var folded = s.Last.Events.Aggregate(before, SeasonEngine.Apply);

        Assert.Equal(s.State, folded);
        Assert.Equal(1, folded.Players[s.PlayerId("Вася")].RerollsThisRoll);
        Assert.Single(folded.ManualEffects);
    }

    [Fact]
    public void Same_seed_gives_the_same_rerolls()
    {
        foreach (var seed in s_seeds.Take(10))
        {
            Scenario Play()
            {
                var s = Give(Horror(seed, s_horror), "Вася", coins: 10, coupons: 1).Roll("Вася");
                for (var i = 0; i < 4; i++)
                {
                    Rerolled(s, "Вася");
                }

                return s;
            }

            Assert.Equal(Play().Log, Play().Log);
        }
    }
}

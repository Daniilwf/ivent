using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// EC2: using items (D-401, D-406, D-407, D-409, D-410) — windows, targets and their exclusions, the base actions,
/// references, interception, «не стакается», the limit of hostile effects, manual items and the admin's corrections.
/// </summary>
public class ItemUseTests
{
    private static Scenario Season(int? mapLength = null)
    {
        var s = EconomyScenario.New()
            .WithCategory("Horror").WithCategory("RPG")
            .WithGame("Silent Hill", 6, "Horror").WithGame("Dead Space", 9, "Horror").WithGame("Gothic", 30, "RPG");
        if (mapLength is { } length)
        {
            s.WithMapLength(length);
        }

        s.WithPlayers("Вася", "Петя", "Коля");
        return s.WithContent(EconomyScenario.RepositoryPack());
    }

    private static Scenario OnMap(MapGraph map, params ObjectDefinition[] extra)
    {
        var s = EconomyScenario.New().WithMap(map).WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя");
        var pack = EconomyScenario.RepositoryPack();
        return s.WithContent(pack with { Objects = [.. pack.Objects, .. extra] });
    }

    [Fact]
    public void A_used_item_leaves_the_inventory_and_its_actions_follow()
    {
        var s = Season();
        var orange = s.Give("Вася", "orange");

        s.Use("Вася", orange);

        Assert.Equal(["object-removed", "item-used", "player-moved"], s.LastTypes());
        var used = Assert.Single(s.LastEvents<ItemUsed>());
        Assert.Equal((orange, "orange", s.PlayerId("Вася")), (used.InstanceId, used.ObjectId, Assert.Single(used.Targets)));
        Assert.Empty(s.Inventory("Вася"));
        Assert.Equal(("c1", 0), (s.Player("Вася").CellId, s.Player("Вася").Points));
        Assert.Equal(MoveReason.Item, Assert.Single(s.LastEvents<PlayerMoved>()).Reason);
    }

    [Fact]
    public void Only_an_item_held_is_used()
    {
        var s = Season();
        var orange = s.Give("Вася", "orange");

        s.Use("Петя", orange);
        Assert.Equal(RejectionCodes.ItemUnknown, s.Last.Rejection!.Code);

        s.Use("Вася", Guid.NewGuid());
        Assert.Equal(RejectionCodes.ItemUnknown, s.Last.Rejection!.Code);
    }

    [Fact]
    public void An_effect_or_special_roll_held_is_not_used_by_hand()
    {
        var s = Season();
        var shield = s.Give("Вася", "shield-effect");

        s.Use("Вася", shield);

        Assert.Equal(RejectionCodes.ItemNotAnItem, s.Last.Rejection!.Code);
    }

    [Theory]
    [InlineData("short-game", "idle", true)]
    [InlineData("short-game", "rolling", false)]
    [InlineData("short-game", "playing", false)]
    [InlineData("lucky-die", "playing", true)]
    [InlineData("lucky-die", "idle", false)]
    [InlineData("reroll-dice", "afterCompletion", true)]
    [InlineData("reroll-dice", "idle", false)]
    [InlineData("reroll-dice", "playing", false)]
    [InlineData("orange", "rolling", true)]
    [InlineData("orange", "playing", true)]
    public void An_item_is_used_only_in_its_window(string item, string phase, bool allowed)
    {
        var s = Season();
        switch (phase)
        {
            case "rolling":
                s.RollTitle("Вася", "Silent Hill");
                break;
            case "playing":
                s.RollTitle("Вася", "Silent Hill").Start("Вася");
                break;
            case "afterCompletion":
                s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(2, 2).Complete("Вася");
                break;
            default:
                break;
        }

        var id = s.Give("Вася", item);
        s.Use("Вася", id);

        Assert.Equal(allowed, s.Last.IsAccepted);
        if (!allowed)
        {
            Assert.Equal(RejectionCodes.ItemWrongWindow, s.Last.Rejection!.Code);
        }
    }

    [Fact]
    public void A_later_roll_closes_the_window_after_the_throw()
    {
        var s = Season();
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(2, 2).Complete("Вася").RollTitle("Вася", "Dead Space");
        var reroll = s.Give("Вася", "reroll-dice");

        s.Use("Вася", reroll);

        Assert.Equal(RejectionCodes.ItemWrongWindow, s.Last.Rejection!.Code);
    }

    [Fact]
    public void Items_are_refused_while_the_flag_is_off_after_the_deadline_and_to_unknown_players()
    {
        var s = Season();
        var orange = s.Give("Вася", "orange");

        s.Act(new UseItem(Guid.NewGuid(), orange));
        Assert.Equal(RejectionCodes.PlayerUnknown, s.Last.Rejection!.Code);

        s.WithRuleset(r => r with { Features = r.Features with { Items = false, Shop = false } });
        s.Use("Вася", orange);
        Assert.Equal(RejectionCodes.FeatureDisabled, s.Last.Rejection!.Code);
        Assert.Empty(s.Last.Events);

        s.WithRuleset(r => r with { Features = r.Features with { Items = true } });
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow.AddHours(1)));
        s.Advance(TimeSpan.FromHours(2));
        s.Use("Вася", orange);
        Assert.Equal(RejectionCodes.SeasonDeadlinePassed, s.Last.Rejection!.Code);
    }

    [Fact]
    public void A_chosen_target_is_required_and_must_be_allowed()
    {
        var s = Season();
        var shove = s.Give("Вася", "shove");

        s.Use("Вася", shove);
        Assert.Equal(RejectionCodes.ItemTargetRequired, s.Last.Rejection!.Code);

        s.Use("Вася", shove, target: "Вася");
        Assert.Equal(RejectionCodes.ItemInvalidTarget, s.Last.Rejection!.Code);

        s.Act(new UseItem(s.PlayerId("Вася"), shove, Guid.NewGuid()));
        Assert.Equal(RejectionCodes.ItemInvalidTarget, s.Last.Rejection!.Code);
    }

    [Fact]
    public void A_push_moves_the_target_back_and_never_past_the_start()
    {
        var s = Season();
        s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Вперёд", CellId: "c3"));

        s.Give("Вася", "shove");
        s.Used("Вася", "shove", target: "Петя");

        Assert.Equal("c1", s.Player("Петя").CellId);
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal((-2, MoveReason.Item), (moved.Steps, moved.Reason));

        s.Give("Вася", "shove");
        s.Used("Вася", "shove", target: "Петя");
        Assert.Equal("start", s.Player("Петя").CellId);

        s.Give("Вася", "shove");
        s.Used("Вася", "shove", target: "Петя");
        Assert.Empty(s.LastEvents<PlayerMoved>());
    }

    [Fact]
    public void A_push_forward_stops_a_cell_before_the_finish()
    {
        var s = Season(mapLength: 3);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Почти", CellId: "c2"));

        s.Give("Вася", "banana");
        s.Used("Вася", "banana");

        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal("c2", s.Player("Вася").CellId);
        Assert.Null(s.Player("Вася").Finish);
    }

    [Fact]
    public void Dice_in_a_move_are_rolled_and_logged()
    {
        var s = Season();
        s.Give("Вася", "sneakers");

        s.NextRandom(3).Used("Вася", "sneakers");

        var rolled = Assert.Single(s.LastEvents<EffectRolled>());
        Assert.Equal((3, "sneakers"), (rolled.Total, rolled.ObjectId));
        Assert.Equal("c3", s.Player("Вася").CellId);
    }

    [Fact]
    public void The_first_finisher_cannot_be_targeted_or_use_items()
    {
        var s = Season(mapLength: 2);
        s.RollTitle("Петя", "Silent Hill").Start("Петя").NextRandom(1, 1).Complete("Петя");
        Assert.NotNull(s.Player("Петя").Finish);

        s.Give("Вася", "shove");
        s.Use("Вася", s.Held("Вася", "shove").InstanceId, target: "Петя");
        Assert.Equal(RejectionCodes.ItemInvalidTarget, s.Last.Rejection!.Code);

        var orange = s.Give("Петя", "orange");
        s.Use("Петя", orange);
        Assert.Equal(RejectionCodes.InventoryFrozen, s.Last.Rejection!.Code);
    }

    [Fact]
    public void A_later_finisher_is_out_of_reach_of_position_effects_but_not_of_coins()
    {
        var s = Season(mapLength: 2);
        s.RollTitle("Петя", "Silent Hill").Start("Петя").NextRandom(1, 1).Complete("Петя");
        s.RollTitle("Коля", "Dead Space").Start("Коля").NextRandom(1, 1, 1).Complete("Коля");
        Assert.Equal(2, s.Player("Коля").Finish!.Order);

        s.Give("Вася", "shove");
        s.Use("Вася", s.Held("Вася", "shove").InstanceId, target: "Коля");
        Assert.Equal(RejectionCodes.ItemInvalidTarget, s.Last.Rejection!.Code);

        s.Give("Вася", "bird-thief");
        s.NextRandom(2).Used("Вася", "bird-thief", target: "Коля");
        Assert.Contains(s.LastEvents<CoinsChanged>(), c => c.PlayerId == s.PlayerId("Коля") && c.Delta == -2);
    }

    [Fact]
    public void Stealing_coins_takes_the_rolled_amount_from_the_target_and_gives_it_to_the_user()
    {
        var s = Season();
        s.WithCoins("Петя", 5);
        s.Give("Вася", "bird-thief");

        s.NextRandom(3).Used("Вася", "bird-thief", target: "Петя");

        Assert.Equal((2, 3), (s.Player("Петя").Coins, s.Player("Вася").Coins));
        Assert.All(s.LastEvents<CoinsChanged>(), c => Assert.Equal(CoinsReason.Item, c.Reason));
        Assert.Single(s.LastEvents<HostileReceived>(), h => h.PlayerId == s.PlayerId("Петя"));
        Assert.Equal(1, s.Player("Петя").Wallet.HostileReceived);
    }

    [Fact]
    public void Coins_may_go_negative_from_an_effect_when_the_rules_allow_it()
    {
        var s = Season();
        s.Give("Вася", "bird-thief");

        s.NextRandom(4).Used("Вася", "bird-thief", target: "Петя");

        Assert.Equal(-4, s.Player("Петя").Coins);
    }

    [Fact]
    public void Coins_stop_at_zero_when_the_rules_forbid_debt()
    {
        var s = Season();
        s.WithRuleset(r => r with { Economy = r.Economy with { AllowNegativeCoins = false } });
        s.WithCoins("Петя", 1);
        s.Give("Вася", "bird-thief");

        s.NextRandom(4).Used("Вася", "bird-thief", target: "Петя");

        Assert.Equal(0, s.Player("Петя").Coins);
        Assert.Contains(s.LastEvents<CoinsChanged>(), c => c.PlayerId == s.PlayerId("Петя") && c.Delta == -1);
    }

    [Fact]
    public void A_shield_stops_the_next_hostile_effect_of_another_player_and_is_spent()
    {
        var s = Season();
        s.WithCoins("Петя", 5);
        s.Give("Петя", "shield");
        s.Used("Петя", "shield");
        Assert.Equal(["shield-effect"], s.Inventory("Петя"));

        s.Give("Вася", "bird-thief");
        s.Used("Вася", "bird-thief", target: "Петя");

        var stopped = Assert.Single(s.LastEvents<HostileIntercepted>());
        Assert.Equal(("bird-thief", s.PlayerId("Вася")), (stopped.ObjectId, stopped.FromPlayerId));
        Assert.Empty(s.LastEvents<CoinsChanged>());
        Assert.Empty(s.LastEvents<EffectRolled>());
        Assert.Empty(s.Inventory("Петя"));
        Assert.Equal((5, 0), (s.Player("Петя").Coins, s.Player("Петя").Wallet.HostileReceived));

        // The next one gets through
        s.Give("Вася", "bird-thief");
        s.NextRandom(1).Used("Вася", "bird-thief", target: "Петя");
        Assert.Equal(4, s.Player("Петя").Coins);
    }

    [Fact]
    public void A_non_stackable_item_is_refused_while_its_last_use_still_waits()
    {
        var s = Season();
        s.Give("Вася", "short-game");
        s.Give("Вася", "short-game");
        s.Used("Вася", "short-game");

        s.Use("Вася", s.Held("Вася", "short-game").InstanceId);

        Assert.Equal(RejectionCodes.ItemNotStackable, s.Last.Rejection!.Code);
    }

    [Fact]
    public void A_non_stackable_item_that_gives_an_effect_is_refused_while_the_target_holds_it()
    {
        var s = Season();
        s.Give("Вася", "curse");
        s.Give("Вася", "curse");
        s.Used("Вася", "curse", target: "Петя");

        s.Use("Вася", s.Held("Вася", "curse").InstanceId, target: "Петя");
        Assert.Equal(RejectionCodes.ItemNotStackable, s.Last.Rejection!.Code);

        s.Use("Вася", s.Held("Вася", "curse").InstanceId, target: "Коля");
        Assert.True(s.Last.IsAccepted);
    }

    [Fact]
    public void Stackable_items_are_used_several_times()
    {
        var s = Season();
        s.Give("Вася", "orange");
        s.Give("Вася", "orange");

        s.Used("Вася", "orange").Used("Вася", "orange");

        Assert.Equal("c2", s.Player("Вася").CellId);
    }

    [Fact]
    public void The_limit_of_hostile_effects_refuses_a_second_one_when_on()
    {
        var s = Season();
        s.WithRuleset(r => r with { Effects = r.Effects with { HostileCap = new HostileCap { Enabled = true, MaxActive = 1 } } });
        s.Give("Вася", "curse");
        s.Used("Вася", "curse", target: "Петя");
        s.WithContent(EconomyScenario.RepositoryPack() with
        {
            Objects = [.. EconomyScenario.RepositoryPack().Objects.Select(o => o.Id == "dirty-trick" ? o with { Effect = o.Effect! with { Target = new TargetSpec { Selector = TargetSelector.Chosen } } } : o)],
        });
        s.Give("Коля", "dirty-trick");

        s.Use("Коля", s.Held("Коля", "dirty-trick").InstanceId, "Петя", "Horror");

        Assert.Equal(RejectionCodes.ItemHostileCap, s.Last.Rejection!.Code);
    }

    [Fact]
    public void Attacks_only_on_higher_points_keep_hostile_effects_on_those_with_more_points()
    {
        var s = Season();
        s.WithRuleset(r => r with { Effects = r.Effects with { AttacksOnlyOnHigherPoints = true } });
        s.WithPoints("Петя", 5).WithPoints("Вася", 5);
        s.Give("Вася", "shove");

        s.Use("Вася", s.Held("Вася", "shove").InstanceId, target: "Петя");
        Assert.Equal(RejectionCodes.ItemInvalidTarget, s.Last.Rejection!.Code);

        s.WithPoints("Петя", 6);
        s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Вперёд", CellId: "c3"));
        s.Use("Вася", s.Held("Вася", "shove").InstanceId, target: "Петя");
        Assert.True(s.Last.IsAccepted);
    }

    [Fact]
    public void Stealing_points_takes_from_a_player_with_more_points_only()
    {
        var s = Season();
        s.WithPoints("Петя", 10).WithPoints("Коля", 1).WithPoints("Вася", 5);
        s.Give("Вася", "point-thief");

        s.Use("Вася", s.Held("Вася", "point-thief").InstanceId, target: "Коля");
        Assert.Equal(RejectionCodes.ItemInvalidTarget, s.Last.Rejection!.Code);

        s.NextRandom(4).Used("Вася", "point-thief", target: "Петя");
        Assert.Equal((6, 9), (s.Player("Петя").Points, s.Player("Вася").Points));
        Assert.All(s.LastEvents<PointsChanged>(), p => Assert.Equal(PointsReason.Item, p.Reason));
    }

    [Fact]
    public void A_choice_must_be_one_of_the_options_and_as_many_answers_as_asked()
    {
        var s = Season();
        s.WithPoints("Петя", 3);
        var trick = s.Give("Вася", "dirty-trick");

        s.Use("Вася", trick, "Петя");
        Assert.Equal(RejectionCodes.ItemInvalidChoice, s.Last.Rejection!.Code);

        s.Use("Вася", trick, "Петя", "Strategy");
        Assert.Equal(RejectionCodes.ItemInvalidChoice, s.Last.Rejection!.Code);

        s.Use("Вася", trick, "Петя", "Horror", "RPG");
        Assert.Equal(RejectionCodes.ItemInvalidChoice, s.Last.Rejection!.Code);

        s.Use("Вася", trick, "Петя", "Horror");
        Assert.True(s.Last.IsAccepted);
        var forced = s.Held("Петя", "forced-genre");
        Assert.Equal(("Horror", true, (Guid?)s.PlayerId("Вася")), (forced.Params!["tag"], forced.Hostile, forced.FromPlayerId));
    }

    [Fact]
    public void Stealing_an_item_moves_it_to_the_user_and_a_bomb_destroys_it()
    {
        var s = Season();
        s.Give("Петя", "orange");
        s.Give("Петя", "shield");

        s.Give("Вася", "magnet");
        s.NextRandom(1).Used("Вася", "magnet", target: "Петя");
        Assert.Equal(["shield"], s.Inventory("Вася"));
        Assert.Equal(["orange"], s.Inventory("Петя"));
        Assert.Single(s.LastEvents<ObjectTransferred>());

        s.Give("Вася", "bomb");
        s.NextRandom(0).Used("Вася", "bomb", target: "Петя");
        Assert.Empty(s.Inventory("Петя"));
        Assert.Equal(ObjectRemoval.Destroyed, s.LastEvents<ObjectRemoved>().Last().Reason);
    }

    [Fact]
    public void A_stolen_item_that_does_not_fit_is_lost()
    {
        var s = Season();
        foreach (var _ in Enumerable.Range(0, 4))
        {
            s.Give("Вася", "orange");
        }

        s.Give("Петя", "shield");
        s.Give("Вася", "magnet");

        // The limit lowered below what the player holds: the stolen shield has no room
        s.WithRuleset(r => r with { Economy = r.Economy with { InventoryLimit = 3 } });
        s.NextRandom(0).Used("Вася", "magnet", target: "Петя");

        Assert.Empty(s.Inventory("Петя"));
        Assert.DoesNotContain("shield", s.Inventory("Вася"));
        Assert.Single(s.LastEvents<ObjectLost>());
    }

    [Fact]
    public void Nothing_to_take_is_nothing()
    {
        var s = Season();

        s.Give("Вася", "magnet");
        s.Used("Вася", "magnet", target: "Петя");

        Assert.Equal(["object-removed", "item-used", "hostile-received"], s.LastTypes());
    }

    [Fact]
    public void A_loot_box_spins_its_wheel_and_gives_the_object()
    {
        var s = Season();
        s.Give("Вася", "loot-box");

        // Weights: orange 30, banana 20, piggy-bank 20, reroll-coupon 15, shop-coupon 10, shield 5 → ticket 95 is the shield
        s.NextRandom(95).Used("Вася", "loot-box");

        var spun = Assert.Single(s.LastEvents<WheelSpun>());
        Assert.Equal(("lootbox", "shield"), (spun.WheelId, spun.ObjectId));
        Assert.Equal(["shield"], s.Inventory("Вася"));
    }

    [Fact]
    public void A_gift_that_does_not_fit_is_lost()
    {
        var s = Season();
        foreach (var _ in Enumerable.Range(0, 5))
        {
            s.Give("Вася", "orange");
        }

        s.Act(new AdjustInventory(s.PlayerId("Вася"), null, s.Held("Вася", "orange").InstanceId, "Место"));
        s.Give("Вася", "loot-box");
        s.NextRandom(0).Used("Вася", "loot-box");
        Assert.Equal(5, s.Player("Вася").Wallet.Items);

        // Now full: the wheel's orange does not fit
        s.Give("Петя", "loot-box");
        s.Act(new AdjustInventory(s.PlayerId("Вася"), "loot-box", null, "Шестой?"));
        Assert.Equal(RejectionCodes.InventoryFull, s.Last.Rejection!.Code);
    }

    [Fact]
    public void Effects_and_special_rolls_do_not_count_against_the_inventory_limit()
    {
        var s = Season();
        foreach (var _ in Enumerable.Range(0, 5))
        {
            s.Give("Вася", "orange");
        }

        s.Give("Вася", "shield-effect");
        s.Give("Вася", "talisman-effect");

        Assert.Equal((5, 7), (s.Player("Вася").Wallet.Items, s.Player("Вася").Wallet.Inventory.Count));
    }

    [Fact]
    public void A_manual_item_goes_to_manual_resolution_with_its_text()
    {
        var s = Season();
        s.Give("Вася", "gift-of-fate");

        s.Used("Вася", "gift-of-fate");

        var manual = Assert.Single(s.LastEvents<ManualEffectCreated>());
        Assert.Equal((ManualEffectSource.Item, "gift-of-fate", (EventKind?)null), (manual.Source, manual.ObjectId, manual.DrawEvent));
        Assert.Equal("gift-of-fate", s.State.ManualEffects[manual.EffectId].ObjectId);

        s.Act(new ResolveManualEffect(manual.EffectId, ManualEffectOutcome.Applied, "Выдал кубик", null));
        Assert.True(s.Last.IsAccepted);
    }

    [Fact]
    public void Drawing_an_event_while_events_are_off_is_a_manual_effect()
    {
        var s = Season();
        s.Give("Вася", "good-omen");

        s.Used("Вася", "good-omen");

        var manual = Assert.Single(s.LastEvents<ManualEffectCreated>());
        Assert.Equal((ManualEffectSource.Item, (EventKind?)EventKind.Good, (string?)null), (manual.Source, manual.DrawEvent, manual.ObjectId));
    }

    [Fact]
    public void A_teleport_ticket_takes_the_nearest_shortcut_ahead_without_triggering_the_destination()
    {
        var s = OnMap(MapBuilder.New().Path("start", "a", "t", "b1", "b2", "finish").Teleport("t", "b2").Bonus("b2", 3).Build());
        s.Give("Вася", "shortcut-ticket");

        s.Used("Вася", "shortcut-ticket");

        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal(("b2", 0, MoveReason.ItemTeleport), (moved.To, moved.Steps, moved.Reason));
        Assert.Equal(0, s.Player("Вася").Points);
    }

    [Fact]
    public void A_push_that_stops_on_a_cell_triggers_it()
    {
        var s = OnMap(MapBuilder.New().Path("start", "a", "b", "finish").Bonus("b", 3).Build());
        s.Give("Вася", "banana");

        s.Used("Вася", "banana");

        Assert.Equal(("b", 3), (s.Player("Вася").CellId, s.Player("Вася").Points));
        Assert.Equal(PointsReason.CellBonus, Assert.Single(s.LastEvents<PointsChanged>()).Reason);
    }

    [Fact]
    public void A_move_of_a_player_choosing_a_branch_is_refused()
    {
        var s = OnMap(MapBuilder.New().Path("start", "f", "x", "finish").Path("f", "y", "finish").Build());
        s.RollTitle("Петя", "Silent Hill").Start("Петя").NextRandom(1, 1).Complete("Петя");
        Assert.Equal(ChoiceKind.Branch, s.Player("Петя").Choice!.Kind);
        s.Give("Вася", "shove");

        s.Use("Вася", s.Held("Вася", "shove").InstanceId, target: "Петя");

        Assert.Equal(RejectionCodes.BranchChoicePending, s.Last.Rejection!.Code);
    }

    // A hostile item on a random active player, on everyone, or on the leader: one coin each.
    private static ObjectDefinition CoinTax(string id, TargetSelector selector, bool excludeSelf = true) =>
        new()
        {
            Id = id,
            Kind = ObjectKind.Item,
            Name = "Налог",
            Description = "Минус монетка.",
            Rarity = Rarity.Common,
            Window = UseWindow.Anytime,
            Hostile = true,
            Stackable = true,
            Effect = new EffectSpec
            {
                Target = new TargetSpec { Selector = selector, ExcludeSelf = excludeSelf },
                Actions = [new ChangeResourceAction { Resource = "coins", Amount = ContentValue.Of(-1) }],
            },
        };

    [Fact]
    public void Random_targets_are_active_players_only()
    {
        var s = Season();
        s.WithContent(EconomyScenario.RepositoryPack() with { Objects = [.. EconomyScenario.RepositoryPack().Objects, CoinTax("tax", TargetSelector.RandomActive)] });
        s.Act(new SetPlayerInactive(s.PlayerId("Коля"), true));
        s.Give("Вася", "tax");

        s.NextRandom(0).Used("Вася", "tax");

        Assert.Equal([s.PlayerId("Петя")], Assert.Single(s.LastEvents<ItemUsed>()).Targets);
        Assert.Equal((-1, 0), (s.Player("Петя").Coins, s.Player("Коля").Coins));
    }

    [Fact]
    public void A_random_target_among_nobody_refuses_the_use()
    {
        var s = Season();
        s.WithContent(EconomyScenario.RepositoryPack() with { Objects = [.. EconomyScenario.RepositoryPack().Objects, CoinTax("tax", TargetSelector.RandomActive)] });
        s.Act(new SetPlayerInactive(s.PlayerId("Коля"), true));
        s.Act(new SetPlayerInactive(s.PlayerId("Петя"), true));
        var tax = s.Give("Вася", "tax");

        s.Use("Вася", tax);

        Assert.Equal(RejectionCodes.ItemNoTarget, s.Last.Rejection!.Code);
    }

    [Fact]
    public void Everyone_and_the_leader_are_reached_as_the_selector_says()
    {
        var s = Season();
        var pack = EconomyScenario.RepositoryPack();
        s.WithContent(pack with { Objects = [.. pack.Objects, CoinTax("tax-all", TargetSelector.All), CoinTax("tax-leader", TargetSelector.Leader, excludeSelf: false), CoinTax("tax-lower", TargetSelector.LowerPoints)] });
        s.WithPoints("Петя", 4).WithPoints("Коля", 9).WithPoints("Вася", 5);

        s.Give("Вася", "tax-all");
        s.Used("Вася", "tax-all");
        Assert.Equal([s.PlayerId("Петя"), s.PlayerId("Коля")], Assert.Single(s.LastEvents<ItemUsed>()).Targets.Order());

        s.Give("Вася", "tax-leader");
        s.Used("Вася", "tax-leader");
        Assert.Equal([s.PlayerId("Коля")], Assert.Single(s.LastEvents<ItemUsed>()).Targets);

        s.Give("Вася", "tax-lower");
        s.Used("Вася", "tax-lower");
        Assert.Equal([s.PlayerId("Петя")], Assert.Single(s.LastEvents<ItemUsed>()).Targets);
    }

    [Fact]
    public void The_admin_gives_and_removes_objects_with_a_comment()
    {
        var s = Season();

        s.Act(new AdjustInventory(s.PlayerId("Вася"), "orange", null, "Компенсация"));
        Assert.Equal(["inventory-adjusted", "object-given"], s.LastTypes());
        Assert.Equal(ObjectSource.Admin, Assert.Single(s.LastEvents<ObjectGiven>()).Source);

        s.Act(new AdjustInventory(s.PlayerId("Вася"), null, s.Held("Вася", "orange").InstanceId, "Ошибся"));
        Assert.Equal(ObjectRemoval.Admin, Assert.Single(s.LastEvents<ObjectRemoved>()).Reason);
        Assert.Empty(s.Inventory("Вася"));
    }

    [Theory]
    [InlineData("noComment")]
    [InlineData("longComment")]
    [InlineData("nothing")]
    [InlineData("unknownObject")]
    [InlineData("notHeld")]
    [InlineData("unknownPlayer")]
    [InlineData("flagOff")]
    [InlineData("finished")]
    public void The_admin_correction_is_refused_when_it_cannot_apply(string what)
    {
        var s = Season();
        var player = s.PlayerId("Вася");
        ICommand command = what switch
        {
            "noComment" => new AdjustInventory(player, "orange", null, " "),
            "longComment" => new AdjustInventory(player, "orange", null, new string('я', Limits.MaxCommentLength + 1)),
            "nothing" => new AdjustInventory(player, null, null, "Ничего"),
            "unknownObject" => new AdjustInventory(player, "no-such", null, "Выдать"),
            "notHeld" => new AdjustInventory(player, null, Guid.NewGuid(), "Забрать"),
            "unknownPlayer" => new AdjustInventory(Guid.NewGuid(), "orange", null, "Выдать"),
            _ => new AdjustInventory(player, "orange", null, "Выдать"),
        };
        if (what == "flagOff")
        {
            s.WithRuleset(r => r with { Features = r.Features with { Items = false, Shop = false } });
        }
        else if (what == "finished")
        {
            s.MoveStatusTo(SeasonStatus.Finished);
        }

        s.Act(command);

        Assert.False(s.Last.IsAccepted);
        Assert.Equal(
            what switch
            {
                "noComment" => RejectionCodes.CommentRequired,
                "longComment" => RejectionCodes.CommentTooLong,
                "nothing" => RejectionCodes.NothingToChange,
                "unknownObject" => RejectionCodes.ObjectUnknown,
                "notHeld" => RejectionCodes.ItemUnknown,
                "unknownPlayer" => RejectionCodes.PlayerUnknown,
                "flagOff" => RejectionCodes.FeatureDisabled,
                _ => RejectionCodes.SeasonClosed,
            },
            s.Last.Rejection!.Code);
    }
}

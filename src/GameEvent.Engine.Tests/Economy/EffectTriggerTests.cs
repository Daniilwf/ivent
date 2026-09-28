using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// EC3: effects and special rolls acting on their own (D-405, D-408, D-412) — the throw's pipeline with changes waiting for
/// it and <c>beforeDice</c> effects, changes after the throw, roll changes and special rolls fixed for a roll, the
/// triggers of the turn, lifetimes, conditions, the chain limit with real content, and effects of others only on future
/// steps.
/// </summary>
public class EffectTriggerTests
{
    private static Scenario Season(params ObjectDefinition[] extra)
    {
        var s = EconomyScenario.New()
            .WithCategory("Horror").WithCategory("RPG")
            .WithGame("Silent Hill", 6, "Horror").WithGame("Dead Space", 9, "Horror").WithGame("Gothic", 30, "RPG")
            .WithPlayers("Вася", "Петя");
        var pack = EconomyScenario.RepositoryPack();
        return s.WithContent(pack with { Objects = [.. pack.Objects, .. extra] });
    }

    private static ObjectDefinition Effect(string id, Trigger trigger, DurationSpec? duration, ConditionSpec? condition, params ActionSpec[] actions) =>
        new()
        {
            Id = id,
            Kind = ObjectKind.Effect,
            Name = id,
            Description = "Эффект для теста.",
            Rarity = Rarity.Common,
            Effect = new EffectSpec { Trigger = trigger, Duration = duration, Condition = condition, Actions = [.. actions] },
        };

    private static ChangeResourceAction Coins(int amount) => new() { Resource = "coins", Amount = ContentValue.Of(amount) };

    [Fact]
    public void A_change_waiting_for_the_next_throw_applies_to_the_completion_and_is_spent()
    {
        var s = Season();
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        s.Give("Вася", "lucky-die");
        s.Used("Вася", "lucky-die");
        Assert.Single(s.Player("Вася").Wallet.NextDice);

        s.NextRandom(2, 2, 5).Complete("Вася");

        var modified = Assert.Single(s.LastEvents<RunDiceModified>());
        Assert.Equal((5, 1), (modified.Mods.Added, modified.SpentNext));
        Assert.Equal([new Die(6, 5)], modified.Rolled);
        Assert.Equal(9, s.Player("Вася").Points);
        Assert.Equal("c9", s.Player("Вася").CellId);
        Assert.Empty(s.Player("Вася").Wallet.NextDice);
        Assert.Equal(9, RunTotal.Of(s.State.Runs.Values.Single().Dice, [], s.State.Runs.Values.Single().Snapshot, s.State.Runs.Values.Single().Mods));
    }

    [Fact]
    public void A_curse_lowers_the_targets_next_throw_but_not_below_one()
    {
        var s = Season();
        s.Give("Вася", "curse");
        s.Used("Вася", "curse", target: "Петя");
        var curse = s.Held("Петя", "curse-effect");
        Assert.Equal((true, (Guid?)s.PlayerId("Вася")), (curse.Hostile, curse.FromPlayerId));

        s.RollTitle("Петя", "Silent Hill").Start("Петя").NextRandom(1, 1, 6).Complete("Петя");

        Assert.Equal("curse-effect", Assert.Single(s.LastEvents<EffectTriggered>()).ObjectId);
        var mods = Assert.Single(s.LastEvents<RunDiceModified>()).Mods;
        Assert.Equal((-6, 1), (mods.Added, mods.Min));
        Assert.Equal(1, s.Player("Петя").Points);
        Assert.Empty(s.Inventory("Петя"));
    }

    [Fact]
    public void A_curse_that_takes_less_than_the_dice_leaves_the_rest()
    {
        var s = Season();
        s.Give("Вася", "curse");
        s.Used("Вася", "curse", target: "Петя");

        s.RollTitle("Петя", "Silent Hill").Start("Петя").NextRandom(4, 4, 2).Complete("Петя");

        Assert.Equal(6, s.Player("Петя").Points);
    }

    [Fact]
    public void Count_multiply_and_bounds_run_in_the_pipelines_order()
    {
        var s = Season();
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        s.Give("Вася", "extra-die");
        s.Give("Вася", "double-dice");
        s.Used("Вася", "extra-die").Used("Вася", "double-dice");

        s.NextRandom(1, 2, 3).Complete("Вася");

        var mods = Assert.Single(s.LastEvents<RunDiceModified>()).Mods;
        Assert.Equal([new Die(4, 3)], mods.ExtraDice);
        Assert.Equal(2, mods.Multiplier);
        Assert.Equal(12, s.Player("Вася").Points);
    }

    [Fact]
    public void A_safety_net_raises_a_low_throw()
    {
        var s = Season();
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        s.Give("Вася", "safety-net");
        s.Used("Вася", "safety-net");

        s.NextRandom(1, 1).Complete("Вася");

        Assert.Equal(4, s.Player("Вася").Points);
    }

    [Fact]
    public void A_reroll_after_the_throw_replaces_the_dice_and_moves_by_the_difference()
    {
        var s = Season();
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Complete("Вася");
        Assert.Equal(("c2", 2), (s.Player("Вася").CellId, s.Player("Вася").Points));
        s.Give("Вася", "reroll-dice");
        s.Give("Вася", "reroll-dice");

        s.NextRandom(4, 4).Used("Вася", "reroll-dice");

        var rerolled = Assert.Single(s.LastEvents<RunDiceRerolled>());
        Assert.Equal([new Die(4, 4), new Die(4, 4)], (IReadOnlyList<Die>)rerolled.Dice);
        Assert.Equal((6, PointsReason.DiceModified), (Assert.Single(s.LastEvents<PointsChanged>()).Delta, s.LastEvents<PointsChanged>().Single().Reason));
        Assert.Equal(MoveReason.DiceModified, Assert.Single(s.LastEvents<PlayerMoved>()).Reason);
        Assert.Equal(("c8", 8), (s.Player("Вася").CellId, s.Player("Вася").Points));

        // Final: the same item does not reroll the same throw again
        s.Use("Вася", s.Held("Вася", "reroll-dice").InstanceId);
        Assert.Equal(RejectionCodes.ItemNotStackable, s.Last.Rejection!.Code);
    }

    [Fact]
    public void A_worse_reroll_takes_back_points_and_cells()
    {
        var s = Season();
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(4, 4).Complete("Вася");
        s.Give("Вася", "reroll-dice");

        s.NextRandom(1, 2).Used("Вася", "reroll-dice");

        Assert.Equal(("c3", 3), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    [Fact]
    public void An_hours_correction_keeps_what_items_did_to_the_throw()
    {
        var s = Season();
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        s.Give("Вася", "lucky-die");
        s.Used("Вася", "lucky-die");
        s.NextRandom(2, 2, 5).Complete("Вася");
        var run = s.State.Runs.Values.Single();

        s.NextRandom(3).Act(new CorrectRunHours(run.RunId, 9, "По HLTB 9 часов"));

        Assert.True(s.Last.IsAccepted);
        Assert.Equal(12, s.Player("Вася").Points);
        Assert.Equal(5, s.State.Runs[run.RunId].DiceMods.Added);
    }

    [Fact]
    public void A_forced_genre_is_the_targets_next_roll_and_is_spent()
    {
        var s = Season();
        s.Give("Вася", "dirty-trick");
        s.WithPoints("Петя", 3);
        s.Used("Вася", "dirty-trick", "Петя", "RPG");

        s.NextRandom(0, 0).Roll("Петя");

        var applied = Assert.Single(s.LastEvents<RollModifiersApplied>());
        Assert.Equal(["RPG"], Assert.Single(applied.Modifiers).Filter!.Tags!.Value);
        Assert.Equal(s.GameId("Gothic"), Assert.Single(s.LastEvents<GameRolled>()).GameId);
        Assert.Equal(["RPG"], Assert.Single(s.LastEvents<GameRolled>()).Sectors);
        Assert.Contains(s.LastEvents<ObjectRemoved>(), r => r.ObjectId == "forced-genre");

        // Held for the roll's reroll, dropped at the start
        s.WithGame("Gothic 2", 20, "RPG");
        s.NextRandom(0, 0).Act(new Reroll(s.PlayerId("Петя")));
        Assert.Equal(s.GameId("Gothic 2"), Assert.Single(s.LastEvents<GameRolled>()).GameId);
        s.Start("Петя");
        Assert.Empty(s.Player("Петя").Wallet.CurrentRoll);
    }

    [Fact]
    public void Effects_of_others_never_touch_a_roll_already_made()
    {
        var s = Season();
        s.WithPoints("Петя", 3);
        s.RollTitle("Петя", "Silent Hill");
        s.Give("Вася", "dirty-trick");

        s.Used("Вася", "dirty-trick", "Петя", "RPG");
        s.NextRandom(0, 0).Act(new Reroll(s.PlayerId("Петя")));

        Assert.Empty(s.LastEvents<RollModifiersApplied>());
        Assert.Equal(s.GameId("Dead Space"), Assert.Single(s.LastEvents<GameRolled>()).GameId);
        Assert.Equal(["forced-genre"], s.Inventory("Петя"));
    }

    [Fact]
    public void Pick_of_three_offers_a_choice_of_three_for_the_roll_and_its_reroll()
    {
        var s = Season();
        s.WithGame("Amnesia", 5, "Horror").WithGame("Outlast", 6, "Horror").WithGame("Soma", 10, "Horror").WithGame("Alien", 15, "Horror").WithGame("Prey", 12, "Horror");
        s.Give("Вася", "pick-of-three");
        s.Used("Вася", "pick-of-three");

        s.NextRandom(0, 0, 0, 0).Roll("Вася");
        Assert.Equal(3, Assert.Single(s.LastEvents<GameChoiceRolled>()).Offers.Count);

        s.NextRandom(0, 0, 0).Act(new Reroll(s.PlayerId("Вася")));
        Assert.Single(s.LastEvents<GameChoiceRolled>());
    }

    [Fact]
    public void Short_game_keeps_long_games_out_of_the_next_roll()
    {
        var s = Season();
        s.Give("Вася", "short-game");
        s.Used("Вася", "short-game");

        s.NextRandom(0, 0).Roll("Вася");

        Assert.Equal(["Horror"], Assert.Single(s.LastEvents<GameRolled>()).Sectors);
    }

    [Fact]
    public void A_special_roll_whose_filter_leaves_nothing_leaves_the_roll_empty()
    {
        var s = Season();
        s.Give("Вася", "dirty-trick");
        s.WithPoints("Петя", 3);
        s.WithCategory("Racing");
        s.Used("Вася", "dirty-trick", "Петя", "Racing");

        s.ExpectRejection().Roll("Петя");

        Assert.Equal(RejectionCodes.NoAvailableGames, s.Last.Rejection!.Code);
        Assert.Equal(["forced-genre"], s.Inventory("Петя"));
    }

    [Fact]
    public void Old_school_needs_a_known_year()
    {
        var s = Season();
        s.Give("Вася", "old-school");
        s.Used("Вася", "old-school");

        s.ExpectRejection().Roll("Вася");

        Assert.Equal(RejectionCodes.NoAvailableGames, s.Last.Rejection!.Code);
    }

    [Fact]
    public void A_talisman_pays_on_each_of_its_runs_then_expires()
    {
        var s = Season();
        s.Give("Вася", "talisman");
        s.Used("Вася", "talisman");

        // Silent Hill (2 dice), Dead Space (3 dice; the wheel still has both categories), Gothic (10 dice; only RPG is left)
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Complete("Вася");
        Assert.Equal(2, s.LastEvents<CoinsChanged>().Where(c => c.Reason == CoinsReason.Item).Sum(c => c.Delta));
        Assert.Equal(2, s.Held("Вася", "talisman-effect").RunsLeft);
        s.NextRandom(0, 1).Roll("Вася").Start("Вася").NextRandom(1, 1, 1).Complete("Вася");
        Assert.Equal(2, s.LastEvents<CoinsChanged>().Where(c => c.Reason == CoinsReason.Item).Sum(c => c.Delta));
        s.NextRandom(0, 0).Roll("Вася").Start("Вася").NextRandom([.. Enumerable.Repeat(1, 10)]).Complete("Вася");
        Assert.Equal(2, s.LastEvents<CoinsChanged>().Where(c => c.Reason == CoinsReason.Item).Sum(c => c.Delta));

        Assert.Empty(s.Inventory("Вася"));
        Assert.Equal(ObjectRemoval.Expired, s.LastEvents<ObjectRemoved>().Single().Reason);
    }

    [Theory]
    [InlineData(Trigger.AfterRoll)]
    [InlineData(Trigger.RunCompleted)]
    [InlineData(Trigger.AfterDice)]
    [InlineData(Trigger.Drop)]
    [InlineData(Trigger.TechReroll)]
    [InlineData(Trigger.Stop)]
    [InlineData(Trigger.Pass)]
    [InlineData(Trigger.MoveStep)]
    public void Every_trigger_of_the_turn_fires_its_effects(Trigger trigger)
    {
        var s = Season(Effect("bonus", trigger, null, null, Coins(3)));
        s.Give("Вася", "bonus");

        s.NextRandom(0, 0).Roll("Вася");
        if (trigger != Trigger.AfterRoll)
        {
            s.Start("Вася");
            switch (trigger)
            {
                case Trigger.Drop:
                    s.NextRandom(1, 1).Act(new DropRun(s.PlayerId("Вася")));
                    break;
                case Trigger.TechReroll:
                    s.NextRandom(0, 0).Act(new TechReroll(s.PlayerId("Вася"), TechRerollReason.WeakPc, null));
                    break;
                default:
                    s.NextRandom(2, 2).Complete("Вася");
                    break;
            }
        }

        var fired = Assert.Single(s.LastEvents<EffectTriggered>());
        Assert.Equal((trigger, "bonus"), (fired.Trigger, fired.ObjectId));
        Assert.Contains(s.LastEvents<CoinsChanged>(), c => c.Reason == CoinsReason.Item && c.Delta == 3);
    }

    [Fact]
    public void A_condition_on_the_run_decides_whether_the_effect_fires()
    {
        var hard = Effect("hard-bonus", Trigger.RunCompleted, null, new ConditionSpec { DifficultyAtLeast = Difficulty.Hard }, Coins(5));
        var s = Season(hard);
        s.Give("Вася", "hard-bonus");

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Complete("Вася", Difficulty.Normal);
        Assert.Empty(s.LastEvents<EffectTriggered>());

        s.RollTitle("Вася", "Dead Space").Start("Вася").NextRandom(1, 1, 1).Complete("Вася", Difficulty.Hard);
        Assert.Single(s.LastEvents<EffectTriggered>());
    }

    [Theory]
    [InlineData("game", true)]
    [InlineData("hours", true)]
    [InlineData("allMax", true)]
    [InlineData("streak", false)]
    [InlineData("hostile", false)]
    public void Conditions_on_the_game_and_on_statistics(string what, bool fires)
    {
        var condition = what switch
        {
            "game" => new ConditionSpec { Game = new GameFilterSpec { Tags = ["horror"] } },
            "hours" => new ConditionSpec { Stat = ContentStat.RunHours, Gte = 6 },
            "allMax" => new ConditionSpec { Stat = ContentStat.AllDiceMax, MinDice = 2 },
            "streak" => new ConditionSpec { Stat = ContentStat.CompletedStreakWithTag, Tag = "Horror", Gte = 2 },
            _ => new ConditionSpec { Stat = ContentStat.HostileReceived, Gte = 1 },
        };
        var s = Season(Effect("cond", Trigger.RunCompleted, null, condition, Coins(1)));
        s.Give("Вася", "cond");

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(4, 4).Complete("Вася");

        Assert.Equal(fires, s.LastEvents<EffectTriggered>().Any());
    }

    [Fact]
    public void A_streak_counts_completed_runs_in_a_row()
    {
        var s = Season(Effect("streak", Trigger.RunCompleted, null, new ConditionSpec { Stat = ContentStat.CompletedStreakWithTag, Tag = "Horror", Gte = 2 }, Coins(1)));
        s.Give("Вася", "streak");
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Complete("Вася");

        s.RollTitle("Вася", "Dead Space").Start("Вася").NextRandom(1, 1, 1).Complete("Вася");

        Assert.Single(s.LastEvents<EffectTriggered>());
    }

    [Fact]
    public void Hostile_effects_received_count_for_the_statistic()
    {
        var s = Season(Effect("tough", Trigger.RunCompleted, null, new ConditionSpec { Stat = ContentStat.HostileReceived, Gte = 1 }, Coins(1)));
        s.Give("Петя", "tough");
        s.Give("Вася", "shove");
        s.Used("Вася", "shove", target: "Петя");

        s.RollTitle("Петя", "Silent Hill").Start("Петя").NextRandom(1, 1).Complete("Петя");

        Assert.Single(s.LastEvents<EffectTriggered>());
    }

    [Fact]
    public void An_effect_until_triggered_fires_once()
    {
        var s = Season(Effect("once", Trigger.AfterRoll, new DurationSpec { UntilTriggered = true }, null, Coins(1)));
        s.Give("Вася", "once");

        s.NextRandom(0, 0).Roll("Вася");

        Assert.Empty(s.Inventory("Вася"));
        Assert.Equal(ObjectRemoval.Used, Assert.Single(s.LastEvents<ObjectRemoved>()).Reason);
    }

    [Fact]
    public void An_effect_of_several_uses_counts_them_down()
    {
        var s = Season(Effect("twice", Trigger.AfterRoll, new DurationSpec { Uses = 2 }, null, Coins(1)));
        s.Give("Вася", "twice");

        s.NextRandom(0, 0).Roll("Вася");
        Assert.Equal(1, s.Held("Вася", "twice").UsesLeft);

        s.NextRandom(0, 0).Act(new Reroll(s.PlayerId("Вася")));
        Assert.Empty(s.Inventory("Вася"));
    }

    [Fact]
    public void A_loop_of_pushes_and_stops_is_cut_by_the_chain_limit()
    {
        // Every stop pushes one cell further: each push stops again — the chain is cut at the depth limit (SPEC «Лимит цепочки»)
        var s = Season(Effect("bouncy", Trigger.Stop, null, null, new MoveAction { Steps = ContentValue.Of(1) }));
        s.Give("Вася", "bouncy");
        s.Give("Вася", "orange");

        s.Used("Вася", "orange");

        var cut = Assert.Single(s.LastEvents<EffectChainCut>());
        Assert.Equal(EffectChainLimit.Depth, cut.Limit);
        Assert.Equal("c4", s.Player("Вася").CellId);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void The_first_finishers_effects_do_not_fire()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Dead Space", 9, "Horror").WithMapLength(2).WithPlayers("Вася", "Петя");
        var pack = EconomyScenario.RepositoryPack();
        s.WithContent(pack with { Objects = [.. pack.Objects, Effect("bonus", Trigger.RunCompleted, null, null, Coins(3))] });
        s.Give("Вася", "bonus");

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Complete("Вася");
        Assert.NotNull(s.Player("Вася").Finish);
        Assert.Empty(s.LastEvents<EffectTriggered>());

        s.RollTitle("Вася", "Dead Space").Start("Вася").NextRandom(1, 1, 1).Complete("Вася");
        Assert.Empty(s.LastEvents<EffectTriggered>());
    }

    [Fact]
    public void A_triggered_effect_may_target_others_and_be_intercepted()
    {
        var sting = Effect("sting", Trigger.RunCompleted, null, null, Coins(-2)) with
        {
            Hostile = true,
            Effect = new EffectSpec { Trigger = Trigger.RunCompleted, Target = new TargetSpec { Selector = TargetSelector.All, ExcludeSelf = true }, Actions = [Coins(-2)] },
        };
        var s = Season(sting);
        s.Give("Вася", "sting");
        s.Give("Петя", "shield");
        s.Used("Петя", "shield");

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Complete("Вася");

        Assert.Single(s.LastEvents<HostileIntercepted>());
        Assert.Equal(0, s.Player("Петя").Coins);
    }

    [Fact]
    public void A_shop_cell_grants_its_coupon_on_a_stop_and_on_a_pass()
    {
        var map = MapBuilder.New().Path("start", "a", "shop", "b", "shop2", "finish").Build();
        map = map with
        {
            Cells = [.. map.Cells.Select(c => c.Id.StartsWith("shop", StringComparison.Ordinal) ? c with { Type = CellType.Shop, Grants = "shop-coupon" } : c)],
        };
        var s = EconomyScenario.New().WithMap(map).WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithPlayers("Вася");
        s.WithContent(EconomyScenario.RepositoryPack());

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1, 1, 1).Complete("Вася");

        Assert.Equal("shop2", s.Player("Вася").CellId);
        Assert.Equal(["shop-coupon", "shop-coupon"], s.Inventory("Вася"));
        Assert.All(s.LastEvents<ObjectGiven>(), g => Assert.Equal(ObjectSource.Cell, g.Source));
    }

    [Fact]
    public void A_push_through_a_shop_grants_the_coupon_too()
    {
        var map = MapBuilder.New().Path("start", "a", "shop", "b", "finish").Build();
        map = map with { Cells = [.. map.Cells.Select(c => c.Id == "shop" ? c with { Type = CellType.Shop, Grants = "shop-coupon" } : c)] };
        var s = EconomyScenario.New().WithMap(map).WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithPlayers("Вася");
        s.WithContent(EconomyScenario.RepositoryPack());
        s.Give("Вася", "banana");
        s.Give("Вася", "orange");

        s.Used("Вася", "banana").Used("Вася", "orange");

        Assert.Equal(["shop-coupon"], s.Inventory("Вася"));
    }
}

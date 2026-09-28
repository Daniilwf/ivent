using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>Findings of the code review of stage 4, each reproduced first (TESTING.md «баг сначала падающим тестом»).</summary>
public class ReviewRegressionTests
{
    private static ObjectDefinition AfterDiceBonus() =>
        new()
        {
            Id = "late-bonus",
            Kind = ObjectKind.Effect,
            Name = "Поздний бонус",
            Description = "+2 к броску после него.",
            Rarity = Rarity.Common,
            Effect = new EffectSpec { Trigger = Trigger.AfterDice, Actions = [new ModifyDiceAction { When = DiceWhen.Current, Stage = DiceStage.Add, Value = ContentValue.Of(2) }] },
        };

    private static Scenario Season(MapGraph? map = null, params ObjectDefinition[] extra)
    {
        var s = EconomyScenario.New();
        if (map is not null)
        {
            s.WithMap(map);
        }

        s.WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Dead Space", 9, "Horror").WithPlayers("Вася", "Петя");
        var pack = EconomyScenario.RepositoryPack();
        return s.WithContent(pack with { Objects = [.. pack.Objects, .. extra] });
    }

    [Fact]
    public void An_after_dice_change_leaves_a_throw_waiting_at_a_fork()
    {
        var s = Season(MapBuilder.New().Path("start", "f", "x", "y", "finish").Path("f", "z", "finish").Build(), AfterDiceBonus());
        s.Give("Вася", "late-bonus");

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(2, 2).Complete("Вася");

        Assert.Equal(("f", ChoiceKind.Branch), (s.Player("Вася").CellId, s.Player("Вася").Choice!.Kind));
        s.ChooseBranch("Вася", "z");
        Assert.True(s.Last.IsAccepted, s.Last.Rejection?.ToString());
    }

    [Fact]
    public void Undoing_the_start_of_a_run_someone_bet_on_waits_for_the_bet()
    {
        var s = Season().WithCoins("Вася", 10);
        s.RollTitle("Петя", "Silent Hill").Start("Петя");
        var started = s.LastCommandId;
        s.Bet("Вася", "Петя", 3, 5);

        s.Act(new UndoCommand(started, "Старт по ошибке"));

        Assert.Equal(RejectionCodes.UndoDependents, s.Last.Rejection!.Code);
    }

    [Fact]
    public void A_gift_of_coins_to_a_player_in_debt_is_given_whole_when_debt_is_forbidden()
    {
        var s = Season();
        s.WithRuleset(r => r with { Economy = r.Economy with { AllowNegativeCoins = false } });
        s.WithCoins("Вася", -5);
        s.Give("Вася", "talisman");
        s.Used("Вася", "talisman");

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Complete("Вася");

        Assert.Contains(s.LastEvents<Scoring.CoinsChanged>(), c => c.Reason == Scoring.CoinsReason.Item && c.Delta == 2);
    }

    [Fact]
    public void A_difficulty_change_keeps_the_sides_an_item_set()
    {
        var bigDie = new ObjectDefinition
        {
            Id = "big-die",
            Kind = ObjectKind.Item,
            Name = "Большой кубик",
            Description = "d8.",
            Rarity = Rarity.Common,
            Window = UseWindow.BeforeDice,
            Effect = new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.Self }, Actions = [new ModifyDiceAction { When = DiceWhen.Next, Stage = DiceStage.Sides, Value = ContentValue.Of(8) }] },
        };
        var s = Season(null, bigDie);
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        s.Give("Вася", "big-die");
        s.Used("Вася", "big-die");
        s.NextRandom(5, 7).Complete("Вася");
        var run = s.State.Runs.Values.Single();

        s.Act(new ChangeRunDifficulty(run.RunId, Difficulty.Hard, "По пруфу"));

        Assert.All(s.State.Runs[run.RunId].Dice, d => Assert.Equal(8, d.Sides));
        Assert.Equal(12, s.Player("Вася").Points);
    }

    [Fact]
    public void A_lot_whose_definition_was_removed_since_the_roll_is_not_sold()
    {
        var s = Season().WithCoins("Вася", 30);
        s.WithContent(EconomyScenario.ExamplePack("orange"));
        s.RollShop("Вася");
        s.WithContent(EconomyScenario.ExamplePack("reroll-coupon"));

        s.Buy("Вася", 0);

        Assert.Equal(RejectionCodes.ObjectUnknown, s.Last.Rejection!.Code);
    }

    [Fact]
    public void A_move_to_the_finish_still_passes_its_shops_and_a_pass_back_gives_nothing()
    {
        var map = MapBuilder.New().Path("start", "a", "shop", "b", "c", "d", "finish").Build();
        map = map with { Cells = [.. map.Cells.Select(c => c.Id == "shop" ? c with { Type = CellType.Shop, Grants = "shop-coupon" } : c)] };
        var s = Season(map);
        s.Act(new Engine.Players.AdjustPlayer(s.PlayerId("Петя"), "На клетку b", CellId: "b"));

        // A push back from b passes the shop and stops on a: no coupon
        s.Give("Вася", "shove");
        s.Used("Вася", "shove", target: "Петя");
        Assert.Equal("a", s.Player("Петя").CellId);
        Assert.Empty(s.Inventory("Петя"));

        // Five steps from a reach the finish, passing the shop on the way
        s.RollTitle("Петя", "Dead Space").Start("Петя").NextRandom(2, 2, 1).Complete("Петя");
        Assert.NotNull(s.Player("Петя").Finish);
        Assert.Equal(["shop-coupon"], s.Inventory("Петя"));
    }
}

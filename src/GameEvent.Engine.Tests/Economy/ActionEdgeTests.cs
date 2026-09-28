using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// EC5, EC7: the base actions and pipeline stages the content files do not use yet — transform and annotate, sides and a
/// ceiling, changes of the throw after it was made by an afterDice effect, outcomes, a fixed list of choices, a chosen pick.
/// </summary>
public class ActionEdgeTests
{
    private static readonly TargetSpec s_self = new() { Selector = TargetSelector.Self };

    private static ObjectDefinition Item(string id, EffectSpec effect, UseWindow window = UseWindow.Anytime, bool hostile = false) =>
        new() { Id = id, Kind = ObjectKind.Item, Name = id, Description = "Тест.", Rarity = Rarity.Common, Window = window, Stackable = true, Hostile = hostile, Effect = effect };

    private static ObjectDefinition Effect(string id, Trigger trigger, params ActionSpec[] actions) =>
        new() { Id = id, Kind = ObjectKind.Effect, Name = id, Description = "Тест.", Rarity = Rarity.Common, Effect = new EffectSpec { Trigger = trigger, Actions = [.. actions] } };

    private static ModifyDiceAction Dice(DiceWhen when, DiceStage stage, string value) =>
        new() { When = when, Stage = stage, Value = int.TryParse(value, out var n) ? ContentValue.Of(n) : ContentValue.TryParse(value)! };

    private static Scenario Season(params ObjectDefinition[] extra)
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Dead Space", 9, "Horror").WithPlayers("Вася", "Петя");
        var pack = EconomyScenario.RepositoryPack();
        return s.WithContent(pack with { Objects = [.. pack.Objects, .. extra] });
    }

    [Fact]
    public void Transform_turns_an_object_into_another_and_annotate_adds_a_note()
    {
        var s = Season(
            Item("alchemy", new EffectSpec { Target = s_self, Actions = [new TransformObjectAction { Mode = TransformMode.Transform, Filter = new ObjectFilterSpec { ObjectId = "orange" }, Into = "banana" }] }),
            Item("pen", new EffectSpec { Target = s_self, Actions = [new TransformObjectAction { Mode = TransformMode.Annotate, Filter = new ObjectFilterSpec { Kind = ObjectKind.Item, Rarity = Rarity.Common, Hostile = false }, Note = "Помечен" }] }));
        var orange = s.Give("Вася", "orange");
        s.Give("Вася", "alchemy");
        s.Give("Вася", "pen");

        s.NextRandom(0).Used("Вася", "alchemy");
        var banana = s.Held("Вася", "banana");
        Assert.Equal(orange, banana.InstanceId);

        s.NextRandom(0).Used("Вася", "pen");
        Assert.Equal(["Помечен"], s.Held("Вася", "banana").Notes);
    }

    [Fact]
    public void Sides_and_a_ceiling_change_the_next_throw()
    {
        var s = Season(Item("big-die", new EffectSpec { Target = s_self, Actions = [Dice(DiceWhen.Next, DiceStage.Sides, "8"), Dice(DiceWhen.Next, DiceStage.Max, "10")] }, UseWindow.BeforeDice));
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        s.Give("Вася", "big-die");
        s.Used("Вася", "big-die");

        s.NextRandom(8, 7).Complete("Вася");

        var run = Assert.Single(s.State.Runs.Values);
        Assert.All(run.Dice, d => Assert.Equal(8, d.Sides));
        Assert.Equal((8, 10), (run.Mods!.Sides, run.Mods.Max));
        Assert.Equal(10, s.Player("Вася").Points);
    }

    [Fact]
    public void An_after_dice_effect_changes_the_throw_just_made()
    {
        var s = Season(Effect("tailwind-late", Trigger.AfterDice, Dice(DiceWhen.Current, DiceStage.Add, "2"), Dice(DiceWhen.Current, DiceStage.Count, "1"), Dice(DiceWhen.Current, DiceStage.Sides, "8")));
        s.Give("Вася", "tailwind-late");

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1, 3).Complete("Вася");

        var run = Assert.Single(s.State.Runs.Values);
        Assert.Equal((2, 1), (run.DiceMods.Added, run.DiceMods.ExtraDice.Count));
        Assert.Equal(7, s.Player("Вася").Points);
        Assert.Equal("c7", s.Player("Вася").CellId);
        Assert.Contains(s.LastEvents<PointsChanged>(), p => p.Reason == PointsReason.DiceModified);
    }

    [Fact]
    public void Outcomes_pick_the_case_of_the_roll()
    {
        var gamble = Item("gamble", new EffectSpec
        {
            Target = s_self,
            Outcomes = new OutcomesSpec
            {
                Roll = ContentValue.TryParse("1d6")!,
                Cases =
                [
                    new OutcomeCase { From = 1, To = 3, Actions = [new ChangeResourceAction { Resource = "coins", Amount = ContentValue.Of(-3) }] },
                    new OutcomeCase { From = 4, To = 6, Actions = [new ChangeResourceAction { Resource = "coins", Amount = ContentValue.TryParse("$roll")! }] },
                ],
            },
        });
        var s = Season(gamble);
        s.Give("Вася", "gamble");
        s.Give("Вася", "gamble");

        s.NextRandom(5).Used("Вася", "gamble");
        Assert.Equal(5, s.Player("Вася").Coins);
        s.NextRandom(2).Used("Вася", "gamble");
        Assert.Equal(2, s.Player("Вася").Coins);
    }

    [Fact]
    public void A_fixed_list_of_choices_and_a_chosen_pick_are_checked()
    {
        var picky = Item("picky", new EffectSpec
        {
            Target = new TargetSpec { Selector = TargetSelector.Chosen, ExcludeSelf = true },
            Actions =
            [
                new RequestChoiceAction { Prompt = "Сколько?", Options = new ChoiceOptionsSpec { List = ["1", "2"] } },
                new ChangeResourceAction { Resource = "stars", Amount = ContentValue.TryParse("$choice")! },
                new TakeObjectAction { Mode = TakeMode.Destroy, Pick = Pick.Chosen },
            ],
        }, hostile: true);
        var s = Season(picky);
        var orange = s.Give("Петя", "orange");
        s.Give("Вася", "picky");

        s.Use("Вася", s.Held("Вася", "picky").InstanceId, "Петя", "2", Guid.NewGuid().ToString("N"));
        Assert.Equal(Engine.Kernel.RejectionCodes.ItemInvalidChoice, s.Last.Rejection!.Code);

        s.Used("Вася", "picky", "Петя", "2", orange.ToString("N"));
        Assert.Equal(2, s.Player("Петя").Resources["stars"]);
        Assert.Empty(s.Inventory("Петя"));
        Assert.Equal(ObjectRemoval.Destroyed, s.LastEvents<ObjectRemoved>().Last().Reason);
    }

    [Fact]
    public void Players_and_games_are_options_too()
    {
        var pick = Item("pick", new EffectSpec
        {
            Target = s_self,
            Actions =
            [
                new RequestChoiceAction { Prompt = "Кто?", Options = new ChoiceOptionsSpec { From = ChoiceSource.Players } },
                new RequestChoiceAction { Prompt = "Что?", Options = new ChoiceOptionsSpec { From = ChoiceSource.Games } },
                new GiveObjectAction { ObjectId = "forced-genre", Params = new ContentParamDictionary([new("tag", "$choice")]) },
            ],
        });
        var s = Season(pick);
        s.Give("Вася", "pick");

        s.Used("Вася", "pick", null, s.PlayerId("Петя").ToString("N"), s.GameId("Dead Space").ToString("N"));

        Assert.Equal(s.GameId("Dead Space").ToString("N"), s.Held("Вася", "forced-genre").Params!["tag"]);
    }

    [Fact]
    public void A_before_roll_effect_fires_with_the_roll()
    {
        var s = Season(Effect("omen", Trigger.BeforeRoll, new ChangeResourceAction { Resource = "coins", Amount = ContentValue.Of(1) }));
        s.Give("Вася", "omen");

        s.NextRandom(0, 0).Roll("Вася");

        Assert.Equal(1, s.Player("Вася").Coins);
        Assert.Equal(Trigger.BeforeRoll, Assert.Single(s.LastEvents<EffectTriggered>()).Trigger);
    }
}

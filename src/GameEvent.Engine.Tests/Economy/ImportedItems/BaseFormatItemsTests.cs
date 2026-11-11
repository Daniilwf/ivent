using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Economy.ImportedItems.ImportedItemsSeason;

namespace GameEvent.Engine.Tests.Economy.ImportedItems;

/// <summary>
/// Step 1 of the import queue (EC40, D-470…D-472): the items of the old pack the base format of CONTENT.md plays, plus
/// object tags and <c>excludeTags</c>. Each imported item does what its description for players says: given → used →
/// the events and the state.
/// </summary>
public class BaseFormatItemsTests
{
    // ---- coins: «Получаешь N монеток» / «Теряешь N монеток» ----

    [Theory]
    [InlineData("candy", 1)]
    [InlineData("chocolate", 2)]
    [InlineData("cupcake", 3)]
    [InlineData("shashlik", 4)]
    [InlineData("pizza", 5)]
    [InlineData("sweet-roll", 6)]
    [InlineData("mythic-jam", 30)]
    [InlineData("seaweed", -1)]
    [InlineData("fish-oil", -2)]
    [InlineData("surstromming", -4)]
    [InlineData("broccoli", -5)]
    [InlineData("fly-agaric", -6)]
    public void A_coin_item_changes_the_users_coins_only(string item, int delta)
    {
        var s = ImportedItemsSeason.New();
        s.Give("Вася", item);

        s.Used("Вася", item);

        var change = Assert.Single(s.LastEvents<CoinsChanged>());
        Assert.Equal((s.PlayerId("Вася"), delta, CoinsReason.Item), (change.PlayerId, change.Delta, change.Reason));
        Assert.Equal((20 + delta, 0, "start"), (s.Player("Вася").Coins, s.Player("Вася").Points, s.Player("Вася").CellId));
        Assert.Empty(s.Inventory("Вася"));
    }

    // ---- moves: «Иди на N клеток вперёд/назад» ----

    [Theory]
    [InlineData("light-beer", 1)]
    [InlineData("better-light-beer", 2)]
    [InlineData("rare-light-beer", 3)]
    [InlineData("dark-beer", -1)]
    [InlineData("better-dark-beer", -2)]
    [InlineData("rare-dark-beer", -3)]
    public void A_beer_moves_the_user_and_gives_no_points(string item, int steps)
    {
        var s = ImportedItemsSeason.New().At("Вася", "c5");
        s.Give("Вася", item);

        s.Used("Вася", item);

        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal((steps, MoveReason.Item, $"c{5 + steps}"), (moved.Steps, moved.Reason, moved.To));
        Assert.Equal(0, s.Player("Вася").Points);
        Assert.Empty(s.LastEvents<PointsChanged>());
    }

    [Theory]
    [InlineData("epic-light-beer", "3", "c8")]
    [InlineData("epic-light-beer", "1", "c6")]
    [InlineData("epic-dark-beer", "2", "c3")]
    public void An_epic_beer_moves_as_many_cells_as_the_player_chose(string item, string answer, string cell)
    {
        var s = ImportedItemsSeason.New().At("Вася", "c5");
        s.Give("Вася", item);

        s.Used("Вася", item, null, answer);

        Assert.Equal(cell, s.Player("Вася").CellId);
    }

    [Fact]
    public void An_epic_beer_refuses_a_number_of_cells_it_does_not_offer()
    {
        var s = ImportedItemsSeason.New();
        var beer = s.Give("Вася", "epic-light-beer");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", beer, null, "4"), RejectionCodes.ItemInvalidChoice);
    }

    [Fact]
    public void A_bounty_bar_moves_the_user_and_the_chosen_player_one_cell_forward()
    {
        var s = ImportedItemsSeason.New();
        s.Give("Вася", "bounty-bar");

        s.Used("Вася", "bounty-bar", target: "Петя");

        Assert.Equal(("c1", "c1", "start"), (s.Player("Вася").CellId, s.Player("Петя").CellId, s.Player("Коля").CellId));
        Assert.False(s.State.Catalog.Get("bounty-bar").Hostile);
    }

    [Fact]
    public void A_bounty_bar_needs_another_player()
    {
        var s = ImportedItemsSeason.New();
        var bar = s.Give("Вася", "bounty-bar");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", bar, "Вася"), RejectionCodes.ItemInvalidTarget);
    }

    // ---- rolls: «Брось dN …» ----

    [Theory]
    [InlineData("gooseberry", 6, 5)]
    [InlineData("currant", 4, 3)]
    public void A_berry_gives_as_many_coins_as_its_die_shows(string item, int sides, int face)
    {
        var s = ImportedItemsSeason.New();
        s.Give("Вася", item);

        s.NextRandom(face).Used("Вася", item);

        var rolled = Assert.Single(s.LastEvents<EffectRolled>());
        Assert.Equal((sides, face), (Assert.Single(rolled.Dice).Sides, rolled.Total));
        Assert.Equal(20 + face, s.Player("Вася").Coins);
    }

    [Fact]
    public void A_retreat_potion_goes_back_as_far_as_its_d8_and_pays_as_much()
    {
        var s = ImportedItemsSeason.New().At("Вася", "c10");
        s.Give("Вася", "retreat-potion");

        s.NextRandom(7).Used("Вася", "retreat-potion");

        Assert.Equal(8, Assert.Single(Assert.Single(s.LastEvents<EffectRolled>()).Dice).Sides);
        Assert.Equal(("c3", 27, 0), (s.Player("Вася").CellId, s.Player("Вася").Coins, s.Player("Вася").Points));
    }

    [Fact]
    public void A_tomato_goes_back_by_its_d4_and_pays_two_coins()
    {
        var s = ImportedItemsSeason.New().At("Вася", "c10");
        s.Give("Вася", "tomato");

        s.NextRandom(3).Used("Вася", "tomato");

        Assert.Equal(("c7", 22), (s.Player("Вася").CellId, s.Player("Вася").Coins));
    }

    [Fact]
    public void A_retreat_never_goes_behind_the_start()
    {
        var s = ImportedItemsSeason.New().At("Вася", "c2");
        s.Give("Вася", "retreat-potion");

        s.NextRandom(8).Used("Вася", "retreat-potion");

        // Back along the walked path, never past the start (D-306); the coins are the whole roll
        Assert.Equal(("start", 28), (s.Player("Вася").CellId, s.Player("Вася").Coins));
    }

    [Theory]
    [InlineData(1, -3, 0)]
    [InlineData(2, 1, 0)]
    [InlineData(3, 2, 0)]
    [InlineData(4, 0, 1)]
    public void A_cherry_does_what_its_d4_says(int face, int coins, int steps)
    {
        var s = ImportedItemsSeason.New();
        s.Give("Вася", "cherry");

        s.NextRandom(face).Used("Вася", "cherry");

        Assert.Equal((20 + coins, steps == 0 ? "start" : "c1"), (s.Player("Вася").Coins, s.Player("Вася").CellId));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void A_raspberry_flips_a_coin_for_one_or_two_coins(int face, int coins)
    {
        var s = ImportedItemsSeason.New();
        s.Give("Вася", "raspberry");

        s.NextRandom(face).Used("Вася", "raspberry");

        Assert.Equal(2, Assert.Single(Assert.Single(s.LastEvents<EffectRolled>()).Dice).Sides);
        Assert.Equal(20 + coins, s.Player("Вася").Coins);
    }

    [Theory]
    [InlineData(1, 21, "c6")]
    [InlineData(2, 20, "c4")]
    public void A_sausage_flips_a_coin_a_coin_and_a_step_forward_or_a_step_back(int face, int coins, string cell)
    {
        var s = ImportedItemsSeason.New().At("Вася", "c5");
        s.Give("Вася", "sausage");

        s.NextRandom(face).Used("Вася", "sausage");

        Assert.Equal((coins, cell), (s.Player("Вася").Coins, s.Player("Вася").CellId));
    }

    // ---- the next completion throw ----

    [Theory]
    [InlineData("blueberry", 2)]
    [InlineData("strawberry", 3)]
    [InlineData("boots", 4)]
    [InlineData("better-boots", 5)]
    [InlineData("rare-boots", 6)]
    [InlineData("epic-boots", 7)]
    public void Boots_and_berries_add_to_the_next_completion_throw(string item, int add)
    {
        var s = ImportedItemsSeason.New();
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        s.Give("Вася", item);

        s.Used("Вася", item);
        var waiting = Assert.Single(s.Player("Вася").Wallet.NextDice);
        Assert.Equal((item, DiceStage.Add, add), (waiting.ObjectId, waiting.Stage, waiting.Value.Number));

        // Silent Hill, 6 hours: two d4
        s.NextRandom(1, 2).Complete("Вася");
        Assert.Equal(3 + add, s.Player("Вася").Points);
        Assert.Equal($"c{3 + add}", s.Player("Вася").CellId);
    }

    [Fact]
    public void Cursed_boots_add_five_and_cost_ten_coins_at_once()
    {
        var s = ImportedItemsSeason.New();
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        s.Give("Вася", "cursed-boots");

        s.Used("Вася", "cursed-boots");
        Assert.Equal(10, s.Player("Вася").Coins);

        s.NextRandom(1, 1).Complete("Вася");
        Assert.Equal(7, s.Player("Вася").Points);
    }

    [Fact]
    public void A_gandonopithecus_throws_one_more_die()
    {
        var s = ImportedItemsSeason.New();
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        s.Give("Вася", "gandonopithecus");

        s.Used("Вася", "gandonopithecus");
        s.NextRandom(1, 2, 4).Complete("Вася");

        Assert.Equal(7, s.Player("Вася").Points);
    }

    [Theory]
    [InlineData("blueberry")]
    [InlineData("gandonopithecus")]
    [InlineData("cursed-boots")]
    public void A_change_of_the_throw_is_used_only_while_playing(string item)
    {
        var s = ImportedItemsSeason.New();
        var held = s.Give("Вася", item);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", held), RejectionCodes.ItemWrongWindow);
    }

    [Fact]
    public void A_reroll_gives_a_free_reroll_of_the_game()
    {
        var s = ImportedItemsSeason.New();
        s.Give("Вася", "reroll");

        s.Used("Вася", "reroll");

        Assert.Equal(1, s.Player("Вася").Resources["freeRerolls"]);
    }

    // ---- rats: «Укради предмет на выбор … Крысиные предметы не крадёт» ----

    [Fact]
    public void A_rat_steals_the_chosen_item_but_not_a_rat_item()
    {
        var s = ImportedItemsSeason.New();
        var candy = s.Give("Петя", "candy");
        var otherRat = s.Give("Петя", "kind-rat");
        var rat = s.Give("Вася", "rat");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", rat, "Петя", Answer(otherRat)), RejectionCodes.ItemInvalidChoice);

        s.Use("Вася", rat, "Петя", Answer(candy));
        ScenarioAssert.Accepted(s);
        var stolen = Assert.Single(s.LastEvents<ObjectTransferred>());
        Assert.Equal((candy, s.PlayerId("Вася")), (stolen.InstanceId, stolen.ToPlayerId));
        Assert.Equal(["candy"], s.Inventory("Вася"));
        Assert.Equal(["kind-rat"], s.Inventory("Петя"));
    }

    [Fact]
    public void A_kind_rat_pays_its_victim_two_coins_and_a_step_forward()
    {
        var s = ImportedItemsSeason.New();
        var candy = s.Give("Петя", "candy");
        s.Give("Вася", "kind-rat");

        s.Used("Вася", "kind-rat", "Петя", Answer(candy));

        Assert.Equal(["candy"], s.Inventory("Вася"));
        Assert.Equal((22, "c1"), (s.Player("Петя").Coins, s.Player("Петя").CellId));
        Assert.Equal((20, "start"), (s.Player("Вася").Coins, s.Player("Вася").CellId));
    }

    [Fact]
    public void A_gift_rat_steals_even_a_rat_item_and_leaves_a_rat_in_return()
    {
        var s = ImportedItemsSeason.New();
        var kindRat = s.Give("Петя", "kind-rat");
        s.Give("Вася", "gift-rat");

        s.Used("Вася", "gift-rat", "Петя", Answer(kindRat));

        Assert.Equal(["kind-rat"], s.Inventory("Вася"));
        Assert.Equal(["rat"], s.Inventory("Петя"));
    }

    [Fact]
    public void An_effect_rat_steals_the_chosen_effect()
    {
        var s = ImportedItemsSeason.New();
        s.Give("Петя", "shield");
        s.Used("Петя", "shield");
        var shield = s.Held("Петя", "shield-effect").InstanceId;
        var curse = s.Give("Петя", "curse-effect");
        s.Give("Вася", "effect-rat");

        // The shield stops the first hostile effect: the rat is gone and nothing is stolen
        s.Used("Вася", "effect-rat", "Петя", Answer(curse));
        Assert.Single(s.LastEvents<HostileIntercepted>());
        Assert.Equal(["curse-effect"], s.Inventory("Петя"));

        s.Give("Вася", "effect-rat");
        s.Used("Вася", "effect-rat", "Петя", Answer(curse));
        Assert.Equal(["curse-effect"], s.Inventory("Вася"));
        Assert.Empty(s.Inventory("Петя"));
        Assert.NotEqual(shield, curse);
    }

    // ---- gremlins: «Уничтожь … у любого игрока, хоть у себя» ----

    [Fact]
    public void A_gremlin_destroys_the_chosen_item_of_another_player_for_a_coin()
    {
        var s = ImportedItemsSeason.New();
        var candy = s.Give("Петя", "candy");
        s.Give("Вася", "gremlin");

        s.Used("Вася", "gremlin", "Петя", Answer(candy));

        var destroyed = Assert.Single(s.LastEvents<ObjectRemoved>(), r => r.Reason == ObjectRemoval.Destroyed);
        Assert.Equal((s.PlayerId("Петя"), candy), (destroyed.PlayerId, destroyed.InstanceId));
        Assert.Empty(s.Inventory("Петя"));
        Assert.Equal((19, 20), (s.Player("Вася").Coins, s.Player("Петя").Coins));
    }

    [Fact]
    public void A_gremlin_may_destroy_the_users_own_item_but_never_a_gremlin_item()
    {
        var s = ImportedItemsSeason.New();
        var candy = s.Give("Вася", "candy");
        var fed = s.Give("Вася", "fed-gremlin");
        var gremlin = s.Give("Вася", "gremlin");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", gremlin, "Вася", Answer(fed)), RejectionCodes.ItemInvalidChoice);

        s.Use("Вася", gremlin, "Вася", Answer(candy));
        ScenarioAssert.Accepted(s);
        Assert.Equal(["fed-gremlin"], s.Inventory("Вася"));

        // A hostile item on oneself is not an attack: no interception, nothing received
        Assert.Equal(0, s.Player("Вася").Wallet.HostileReceived);
    }

    [Fact]
    public void A_fed_gremlin_is_free_and_an_effect_gremlin_destroys_an_effect_for_two_coins()
    {
        var s = ImportedItemsSeason.New();
        var candy = s.Give("Петя", "candy");
        var curse = s.Give("Петя", "curse-effect");
        s.Give("Вася", "fed-gremlin");
        s.Give("Вася", "effect-gremlin");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", s.Held("Вася", "fed-gremlin").InstanceId, "Петя", Answer(curse)), RejectionCodes.ItemInvalidChoice);
        s.Used("Вася", "fed-gremlin", "Петя", Answer(candy));
        Assert.Equal(20, s.Player("Вася").Coins);

        s.Used("Вася", "effect-gremlin", "Петя", Answer(curse));
        Assert.Empty(s.Inventory("Петя"));
        Assert.Equal(18, s.Player("Вася").Coins);
    }

    [Fact]
    public void A_gremlin_and_a_rat_do_not_reach_finishers_but_a_bounty_bar_needs_an_active_player()
    {
        // A destroyed or stolen item is not points or coins: a later finisher is out of reach (D-424), the first one always
        var s = WithFinishers();
        s.Give("Коля", "candy");
        var gremlin = s.Give("Вася", "gremlin");
        var rat = s.Give("Вася", "rat");
        var bar = s.Give("Вася", "bounty-bar");

        foreach (var (item, target) in new[] { (gremlin, "Коля"), (gremlin, "Петя"), (rat, "Коля"), (rat, "Петя"), (bar, "Коля"), (bar, "Петя") })
        {
            ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", item, target), RejectionCodes.ItemInvalidTarget);
        }
    }

    // ---- object tags and the filter (D-470) ----

    [Fact]
    public void Object_tags_and_excluded_tags_are_checked()
    {
        var bad = Gremlin() with { Tags = ["Rat", "rat", "rat"] };

        var errors = ContentValidator.Check(bad).Select(e => e.Path).ToList();

        Assert.Contains("$.tags[0]", errors);
        Assert.Contains("$.tags", errors);
        var empty = Gremlin() with
        {
            Effect = Gremlin().Effect! with { Actions = [new TakeObjectAction { Mode = TakeMode.Destroy, Filter = new ObjectFilterSpec { ExcludeTags = [] } }] },
        };
        Assert.Contains("$.effect.actions[0].filter.excludeTags", ContentValidator.Check(empty).Select(e => e.Path));
        Assert.Empty(ContentValidator.Check(Gremlin()));
    }

    [Fact]
    public void Tags_are_written_only_when_set_so_older_logs_stay_as_they_were()
    {
        var orange = EconomyScenario.RepositoryPack().Objects.Single(o => o.Id == "orange");

        Assert.DoesNotContain("\"tags\"", ContentJson.Write(orange), StringComparison.Ordinal);
        Assert.Contains("\"tags\":[\"gremlin\"]", ContentJson.Write(Gremlin()), StringComparison.Ordinal);
        Assert.Equal(Gremlin(), ContentJson.Parse<ObjectDefinition>(ContentJson.Write(Gremlin())));
    }

    private static ObjectDefinition Gremlin() => EconomyScenario.RepositoryPack().Objects.Single(o => o.Id == "gremlin");
}

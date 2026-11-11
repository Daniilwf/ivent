using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy.ImportedItems;

/// <summary>
/// Step 5 of the import queue (EC44, D-477): <c>move.direction</c> — <c>chosen</c> (the player answers <c>forward</c> or
/// <c>back</c>) and <c>away</c> (forward when the target is ahead of the user, back when behind). Pushes go through
/// <see cref="Movement.Push"/> (the finish only by a run's own move, D-322) and back along the walked edges, never past a
/// checkpoint (D-306).
/// </summary>
public class MoveDirectionTests
{
    private static Scenario Season(MapGraph? map = null)
    {
        var s = EconomyScenario.New();
        if (map is not null)
        {
            s.WithMap(map);
        }

        s.WithRuleset(r => r with { Effects = r.Effects with { Reflect = new CoinReflect { CoinsAtMost = -30, Multiplier = 2 } } })
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя", "Коля");
        return s.WithContent(EconomyScenario.RepositoryPack()).WithCoins("Вася", 20).WithCoins("Петя", 20);
    }

    [Theory]
    [InlineData("pistol", "forward", "c11")]
    [InlineData("pistol", "back", "c9")]
    [InlineData("better-pistol", "forward", "c12")]
    [InlineData("rare-pistol", "back", "c7")]
    [InlineData("epic-pistol", "forward", "c14")]
    public void A_pistol_moves_the_chosen_player_the_way_the_user_chose_and_takes_two_coins(string pistol, string way, string cell)
    {
        var s = Season().At("Петя", "c10");
        s.Give("Вася", pistol);

        s.Used("Вася", pistol, "Петя", way);

        Assert.Equal((cell, 18), (s.Player("Петя").CellId, s.Player("Петя").Coins));
        Assert.Equal(MoveReason.Item, Assert.Single(s.LastEvents<PlayerMoved>()).Reason);
    }

    [Theory]
    [InlineData("вперёд")]
    [InlineData("left")]
    public void A_pistol_refuses_a_way_it_does_not_know(string way)
    {
        var s = Season().At("Петя", "c10");
        var pistol = s.Give("Вася", "pistol");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", pistol, "Петя", way), RejectionCodes.ItemInvalidChoice);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", pistol, "Петя"), RejectionCodes.ItemInvalidChoice);
    }

    [Fact]
    public void A_pistol_forward_stops_before_the_finish_and_back_stops_on_a_checkpoint()
    {
        var map = MapBuilder.New().Path("start", "a", "b", "cp", "d", "e", "f", "finish").Checkpoint("cp").Build();
        var s = Season(map).At("Петя", "f").At("Коля", "e");
        s.Give("Вася", "epic-pistol");
        s.Give("Вася", "rare-pistol");

        // Four forward from f: the finish only by a run's own move (D-322)
        s.Used("Вася", "epic-pistol", "Петя", "forward");
        Assert.Equal("f", s.Player("Петя").CellId);
        Assert.Null(s.Player("Петя").Finish);

        // Three back from e: d, then the checkpoint holds (D-306)
        s.Used("Вася", "rare-pistol", "Коля", "back");
        Assert.Equal("cp", s.Player("Коля").CellId);
    }

    [Fact]
    public void A_push_back_follows_the_edges_the_player_walked()
    {
        // A fork: Петя took the side branch s1–s2 to j; one back from j returns along s2, not along the main line
        var map = MapBuilder.New().Path("start", "fk", "m1", "m2", "j", "finish").Path("fk", "s1", "s2", "j").Build();
        var s = Season(map);
        s.RollTitle("Петя", "Silent Hill").Start("Петя").NextRandom(2, 2).Complete("Петя");
        s.ChooseBranch("Петя", "s1");
        Assert.Equal("j", s.Player("Петя").CellId);
        s.Give("Вася", "pistol");

        s.Used("Вася", "pistol", "Петя", "back");

        Assert.Equal("s2", s.Player("Петя").CellId);
    }

    [Theory]
    [InlineData("c15", "c16")]
    [InlineData("c5", "c4")]
    public void A_slingshot_moves_the_target_away_from_the_user(string targetCell, string after)
    {
        var s = Season().At("Вася", "c10").At("Петя", targetCell);
        s.Give("Вася", "slingshot");

        s.Used("Вася", "slingshot", "Петя");

        Assert.Equal((after, 18), (s.Player("Петя").CellId, s.Player("Петя").Coins));
    }

    [Fact]
    public void A_slingshot_on_the_users_own_cell_moves_nobody_but_still_hurts()
    {
        var s = Season().At("Вася", "c10").At("Петя", "c10");
        s.Give("Вася", "slingshot");

        s.Used("Вася", "slingshot", "Петя");

        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal(("c10", 18), (s.Player("Петя").CellId, s.Player("Петя").Coins));
    }

    [Fact]
    public void A_slingshot_reflects_like_the_other_weapons()
    {
        var s = Season().At("Вася", "c10").At("Петя", "c12").WithCoins("Петя", -30);
        s.Give("Вася", "slingshot");

        s.Used("Вася", "slingshot", "Петя");

        Assert.Equal(("c13", -30, 16), (s.Player("Петя").CellId, s.Player("Петя").Coins, s.Player("Вася").Coins));
    }

    [Fact]
    public void A_shield_takes_the_pistols_answer_with_it()
    {
        var s = Season().At("Петя", "c10");
        s.Give("Петя", "shield");
        s.Used("Петя", "shield");
        s.Give("Вася", "pistol");

        s.Used("Вася", "pistol", "Петя", "back");

        Assert.Single(s.LastEvents<HostileIntercepted>());
        Assert.Equal("c10", s.Player("Петя").CellId);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("triggered")]
    public void A_direction_is_checked(string variant)
    {
        var move = new MoveAction { Steps = ContentValue.Of(variant == "negative" ? -1 : 1), Direction = MoveDirection.Chosen };
        var item = new ObjectDefinition
        {
            Id = "bad-way",
            Kind = variant == "triggered" ? ObjectKind.Effect : ObjectKind.Item,
            Name = "x",
            Description = "x",
            Window = variant == "triggered" ? null : UseWindow.Anytime,
            Effect = new EffectSpec { Trigger = variant == "triggered" ? Trigger.RunCompleted : null, Actions = [move] },
        };

        Assert.NotEmpty(ContentValidator.Check(item).Concat(ContentPublishing.Check(new ContentPack { Objects = [item] })));
    }
}

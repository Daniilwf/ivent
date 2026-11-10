using GameEvent.Engine.Content;
using GameEvent.Engine.Map;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// Pushes of items and effects follow the engine's movement rules (D-421): only a run's own move reaches the finish — a
/// push stops on the cell before it, on both map models (D-322, <c>Movement.Push</c>); a player choosing a branch is not
/// moved by anyone's item or effect (D-305, D-310 (8)): a use that would move them is refused, a triggered push passes them
/// by. Games of 3 hours give one d4.
/// </summary>
public class PushRulesTests
{
    private static Scenario Season(MapGraph map, params ObjectDefinition[] extra)
    {
        var s = EconomyScenario.New().WithMap(map).WithCategory("Horror")
            .WithGame("Silent Hill", 3, "Horror").WithGame("Fatal Frame", 3, "Horror")
            .WithPlayers("Вася", "Петя");
        var pack = EconomyScenario.RepositoryPack();
        return s.WithContent(pack with { Objects = [.. pack.Objects, .. extra] });
    }

    private static ObjectDefinition Rocket() =>
        new()
        {
            Id = "rocket",
            Kind = ObjectKind.Item,
            Name = "Ракета",
            Description = "Иди на 10 клеток вперёд.",
            Rarity = Rarity.Common,
            Window = UseWindow.Anytime,
            Effect = new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.Self }, Actions = [new MoveAction { Steps = ContentValue.Of(10) }] },
        };

    // An effect firing on its owner's completion: every other player (or the owner) is pushed by steps.
    private static ObjectDefinition Quake(TargetSelector selector, int steps) =>
        new()
        {
            Id = "quake",
            Kind = ObjectKind.Effect,
            Name = "Толчок",
            Description = "Эффект для теста.",
            Rarity = Rarity.Common,
            Effect = new EffectSpec
            {
                Trigger = Trigger.RunCompleted,
                Target = new TargetSpec { Selector = selector, ExcludeSelf = selector != TargetSelector.Self },
                Actions = [new MoveAction { Steps = ContentValue.Of(steps) }],
            },
        };

    private static MapGraph Fork() => MapBuilder.New().Path("start", "f", "x", "finish").Path("f", "y", "finish").Build();

    [Fact]
    public void An_item_push_on_a_graph_stops_before_the_finish_along_the_default_branch()
    {
        var s = Season(Fork(), Rocket());
        s.Give("Вася", "rocket");

        s.Used("Вася", "rocket");

        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal(["f", "x"], (IReadOnlyList<string>)moved.Path);
        Assert.Equal(("x", (Engine.Finish.FinishState?)null), (s.Player("Вася").CellId, s.Player("Вася").Finish));
    }

    [Fact]
    public void An_item_push_from_the_cell_before_the_finish_moves_nothing()
    {
        var s = Season(MapBuilder.New().Path("start", "a", "finish").Build(), Rocket());
        s.Give("Вася", "rocket");
        s.Used("Вася", "rocket");
        Assert.Equal("a", s.Player("Вася").CellId);
        s.Give("Вася", "rocket");

        s.Used("Вася", "rocket");

        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal("a", s.Player("Вася").CellId);
        Assert.Null(s.Player("Вася").Finish);
    }

    [Fact]
    public void An_effect_push_after_the_own_move_stops_before_the_finish()
    {
        var s = Season(MapBuilder.New().Path("start", "a", "b", "c", "finish").Build(), Quake(TargetSelector.Self, 10));
        s.Give("Вася", "quake");

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1).Complete("Вася");

        Assert.Equal(("c", (Engine.Finish.FinishState?)null), (s.Player("Вася").CellId, s.Player("Вася").Finish));
        Assert.Equal([MoveReason.CompletionRoll, MoveReason.Item], s.LastEvents<PlayerMoved>().Select(m => m.Reason));
    }

    [Fact]
    public void An_effect_push_passes_by_a_player_choosing_a_branch()
    {
        // Given Петя's throw waits at the fork
        var s = Season(Fork(), Quake(TargetSelector.All, -1));
        s.RollTitle("Петя", "Fatal Frame").Start("Петя").NextRandom(2).Complete("Петя");
        Assert.Equal(("f", ChoiceKind.Branch), (s.Player("Петя").CellId, s.Player("Петя").Choice!.Kind));
        s.Give("Вася", "quake");

        // When Вася's completion fires his effect on everyone else
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1).Complete("Вася");

        // Then his completion stands, Петя is not moved and still chooses
        ScenarioAssert.Accepted(s);
        Assert.DoesNotContain(s.LastEvents<PlayerMoved>(), m => m.PlayerId == s.PlayerId("Петя"));
        Assert.Equal(("f", ChoiceKind.Branch), (s.Player("Петя").CellId, s.Player("Петя").Choice!.Kind));
        s.ChooseBranch("Петя", "y");
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void An_own_effect_does_not_push_a_throw_waiting_at_a_fork()
    {
        var s = Season(Fork(), Quake(TargetSelector.Self, 1));
        s.Give("Вася", "quake");

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(2).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(("f", ChoiceKind.Branch), (s.Player("Вася").CellId, s.Player("Вася").Choice!.Kind));
        Assert.Equal(MoveReason.CompletionRoll, Assert.Single(s.LastEvents<PlayerMoved>()).Reason);
    }
}

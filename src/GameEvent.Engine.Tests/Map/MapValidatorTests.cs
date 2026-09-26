using GameEvent.Engine.Content;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Map;

/// <summary>
/// The checks before a map is published (SPEC «Редактор», D-302): every problem at once, each with a code and the cell,
/// arrow or zone it is about, so the editor can show it where it is.
/// </summary>
public class MapValidatorTests
{
    private static readonly Ruleset s_rules = TestRuleset.Create() with
    {
        Features = TestRuleset.Create().Features with { MapMode = MapMode.Graph },
    };

    private static IReadOnlyList<MapError> Check(MapBuilder map, Ruleset? rules = null) => MapValidator.Validate(map.Build(), rules ?? s_rules);

    private static MapBuilder Fork() =>
        MapBuilder.New().Path("start", "a", "f", "b1", "j", "finish").Path("f", "c1", "j");

    private static void AssertOnly(IReadOnlyList<MapError> errors, string code, string subject) =>
        Assert.Contains(errors, e => e.Code == code && e.Subject == subject);

    // ---- Valid maps ----

    [Fact]
    public void Linear_map_is_a_valid_graph()
    {
        // MP1: stage 1's chain is the same graph and passes every check
        Assert.Empty(MapValidator.Validate(LinearMap.Generate(30), s_rules));
        Assert.Empty(MapValidator.Validate(LinearMap.Generate(1), s_rules));
    }

    [Fact]
    public void Fork_with_a_merge_teleport_checkpoint_bonus_and_zone_is_valid()
    {
        var map = Fork()
            .Teleport("a", to: "j")
            .Checkpoint("b1")
            .Bonus("c1", 3)
            .Zone(new ZoneDefinition { Id = "swamp", Name = "Болото", RollFilter = new GameFilterSpec { Tags = ["Horror"] } }, "c1", "j");

        Assert.Empty(Check(map));
    }

    // ---- Cells and arrows ----

    [Fact]
    public void Map_needs_exactly_one_start_and_a_finish()
    {
        var noStart = MapBuilder.New().Path("s", "finish");
        AssertOnly(Check(noStart), MapErrorCodes.StartCount, "map");

        var twoStarts = MapBuilder.New().Path("start", "finish").Cell("s2", CellType.Start).Path("s2", "finish");
        AssertOnly(Check(twoStarts), MapErrorCodes.StartCount, "map");

        var noFinish = MapBuilder.New().Path("start", "a").Path("a", "start");
        AssertOnly(Check(noFinish), MapErrorCodes.NoFinish, "map");
    }

    [Fact]
    public void Cell_ids_are_unique_and_well_formed()
    {
        var duplicate = MapBuilder.New().Path("start", "a", "finish");
        var map = duplicate.Build();
        map = map with { Cells = [.. map.Cells, new Cell("a", CellType.Empty)] };
        Assert.Contains(MapValidator.Validate(map, s_rules), e => e.Code == MapErrorCodes.CellDuplicate && e.Subject == "a");

        var blank = MapBuilder.New().Path("start", " ", "finish");
        Assert.Contains(Check(blank), e => e.Code == MapErrorCodes.CellIdInvalid);

        var tooLong = MapBuilder.New().Path("start", new string('x', MapValidator.MaxIdLength + 1), "finish");
        Assert.Contains(Check(tooLong), e => e.Code == MapErrorCodes.CellIdInvalid);
    }

    [Fact]
    public void Arrows_join_existing_distinct_cells_once()
    {
        var map = MapBuilder.New().Path("start", "a", "finish").Build();
        map = map with
        {
            Edges =
            [
                .. map.Edges,
                new Edge("a", "ghost", IsDefaultForward: false, IsPrimaryBackward: true),
                new Edge("a", "a", IsDefaultForward: false, IsPrimaryBackward: false),
                new Edge("start", "a", IsDefaultForward: false, IsPrimaryBackward: false),
            ],
        };

        var errors = MapValidator.Validate(map, s_rules);

        Assert.Contains(errors, e => e.Code == MapErrorCodes.EdgeUnknownCell && e.Subject == "a→ghost");
        Assert.Contains(errors, e => e.Code == MapErrorCodes.EdgeSelfLoop && e.Subject == "a→a");
        Assert.Contains(errors, e => e.Code == MapErrorCodes.EdgeDuplicate && e.Subject == "start→a");
    }

    [Fact]
    public void Every_cell_is_reachable_from_the_start()
    {
        // An island a → b → finish that nothing leads into
        var map = MapBuilder.New().Path("start", "x", "finish").Path("island", "finish");

        var errors = Check(map);

        AssertOnly(errors, MapErrorCodes.Unreachable, "island");
        Assert.DoesNotContain(errors, e => e.Code == MapErrorCodes.Unreachable && e.Subject != "island");
    }

    [Fact]
    public void Cell_reached_only_by_a_teleport_is_reachable()
    {
        // The snake's destination hangs on the main line anyway; a branch reached only by the shortcut counts too
        var map = MapBuilder.New().Path("start", "a", "b", "finish").Path("hidden", "b").Teleport("a", to: "hidden");

        Assert.DoesNotContain(Check(map), e => e.Code == MapErrorCodes.Unreachable);
    }

    [Fact]
    public void Finish_is_reachable_from_every_cell()
    {
        // A dead loop: from the fork's second branch l1 ⇄ l2 there is no way on
        var map = MapBuilder.New().Path("start", "f", "finish").Path("f", "l1", "l2", "l1");

        var errors = Check(map);

        AssertOnly(errors, MapErrorCodes.FinishUnreachable, "l1");
        AssertOnly(errors, MapErrorCodes.FinishUnreachable, "l2");
    }

    [Fact]
    public void Finish_has_no_exits_and_other_cells_have_one_default_exit()
    {
        var finishExit = MapBuilder.New().Path("start", "finish", "after").Path("after", "finish");
        AssertOnly(Check(finishExit), MapErrorCodes.FinishHasExits, "finish");

        var deadEnd = MapBuilder.New().Path("start", "a", "finish").Path("start", "dead");
        Assert.Contains(Check(deadEnd), e => e.Code == MapErrorCodes.DeadEnd && e.Subject == "dead");

        var notDefault = MapBuilder.New().Path("start").Edge("start", "a", isDefault: false, isPrimary: true).Path("a", "finish");
        AssertOnly(Check(notDefault), MapErrorCodes.DefaultBranch, "start");
    }

    [Fact]
    public void Fork_has_two_exits_and_exactly_one_default_branch()
    {
        // SPEC «Редактор»: у развилки минимум два выхода и ветка по умолчанию
        var oneExit = MapBuilder.New().Path("start", "f", "finish").Cell("f", CellType.Fork);
        AssertOnly(Check(oneExit), MapErrorCodes.ForkExits, "f");

        var noDefault = MapBuilder.New().Path("start", "f")
            .Edge("f", "a", isDefault: false, isPrimary: true).Edge("f", "b", isDefault: false, isPrimary: true)
            .Path("a", "finish").Path("b", "finish");
        AssertOnly(Check(noDefault), MapErrorCodes.DefaultBranch, "f");

        var twoDefaults = MapBuilder.New().Path("start", "f")
            .Edge("f", "a", isDefault: true, isPrimary: true).Edge("f", "b", isDefault: true, isPrimary: true)
            .Path("a", "finish").Path("b", "finish");
        AssertOnly(Check(twoDefaults), MapErrorCodes.DefaultBranch, "f");
    }

    [Fact]
    public void Cell_with_several_exits_must_be_a_fork()
    {
        var map = Fork().KeepTypes();

        AssertOnly(Check(map), MapErrorCodes.NotAFork, "f");
    }

    [Fact]
    public void Cell_with_several_entries_needs_one_primary_incoming_edge()
    {
        // SPEC «Редактор»: у клеток с несколькими входами задано основное входящее ребро
        var none = Fork().WithoutEdge("c1", "j").Edge("c1", "j", isDefault: true, isPrimary: false)
            .WithoutEdge("b1", "j").Edge("b1", "j", isDefault: true, isPrimary: false);
        AssertOnly(Check(none), MapErrorCodes.PrimaryBackward, "j");

        var two = Fork().WithoutEdge("c1", "j").Edge("c1", "j", isDefault: true, isPrimary: true);
        AssertOnly(Check(two), MapErrorCodes.PrimaryBackward, "j");

        // A single entry needs no mark
        var single = MapBuilder.New().Path("start").Edge("start", "a", isDefault: true, isPrimary: false).Path("a", "finish");
        Assert.Empty(Check(single));
    }

    // ---- Teleports ----

    [Fact]
    public void Teleport_leads_to_another_existing_cell_but_not_the_finish()
    {
        AssertOnly(Check(MapBuilder.New().Path("start", "t", "finish").Retyped("t", CellType.Teleport)), MapErrorCodes.TeleportTarget, "t");
        AssertOnly(Check(MapBuilder.New().Path("start", "t", "finish").Teleport("t", to: "ghost")), MapErrorCodes.TeleportTarget, "t");
        AssertOnly(Check(MapBuilder.New().Path("start", "t", "finish").Teleport("t", to: "t")), MapErrorCodes.TeleportTarget, "t");
        AssertOnly(Check(MapBuilder.New().Path("start", "t", "finish").Teleport("t", to: "finish")), MapErrorCodes.TeleportTarget, "t");
    }

    [Fact]
    public void Teleport_cycles_are_refused()
    {
        // SPEC «Проверки перед публикацией»: нет циклов из телепортов — a → b → a
        var map = MapBuilder.New().Path("start", "a", "b", "finish").Teleport("a", to: "b").Teleport("b", to: "a");

        var errors = Check(map);

        Assert.Contains(errors, e => e.Code == MapErrorCodes.TeleportCycle && e.Subject is "a" or "b");
    }

    [Fact]
    public void Teleport_chain_without_a_cycle_is_allowed()
    {
        // a → b is a shortcut onto another teleport; the destination does not trigger, so b is only used when stopped on
        var map = MapBuilder.New().Path("start", "a", "b", "c", "finish").Teleport("a", to: "b").Teleport("b", to: "c");

        Assert.Empty(Check(map));
    }

    // ---- Parameters and features ----

    [Fact]
    public void Cell_parameters_belong_to_their_type()
    {
        var map = MapBuilder.New().Path("start", "a", "b", "c", "finish")
            .Cell("a", CellType.Empty, c => c with { To = "c" })
            .Cell("b", CellType.PointsBonus)
            .Cell("c", CellType.Checkpoint, c => c with { Amount = 3 });

        var errors = Check(map);

        AssertOnly(errors, MapErrorCodes.CellParameter, "a");
        AssertOnly(errors, MapErrorCodes.CellParameter, "b");
        AssertOnly(errors, MapErrorCodes.CellParameter, "c");
    }

    [Fact]
    public void Event_and_shop_cells_need_their_mechanics_switched_on()
    {
        // D-302: a disabled mechanic does not appear on the map
        var map = MapBuilder.New().Path("start", "e", "s", "finish")
            .Cell("e", CellType.Event, c => c with { Deck = "zone" })
            .Cell("s", CellType.Shop, c => c with { Grants = "shop-coupon" });

        var errors = Check(map);

        AssertOnly(errors, MapErrorCodes.FeatureDisabled, "e");
        AssertOnly(errors, MapErrorCodes.FeatureDisabled, "s");
    }

    // ---- Zones ----

    [Fact]
    public void Cells_name_existing_zones_and_zone_ids_are_unique()
    {
        var zone = new ZoneDefinition { Id = "z", Name = "Зона" };
        var map = MapBuilder.New().Path("start", "a", "finish").Zone(zone, "a").Zone(zone).Cell("b", CellType.Empty, c => c with { Zone = "ghost" }).Path("a", "b").Path("b", "finish");

        var errors = Check(map);

        AssertOnly(errors, MapErrorCodes.ZoneDuplicate, "z");
        AssertOnly(errors, MapErrorCodes.ZoneUnknown, "b");
    }

    [Fact]
    public void Zone_content_is_checked_like_content()
    {
        var map = MapBuilder.New().Path("start", "a", "finish").Zone(new ZoneDefinition { Id = "z", Name = " ", DropPenaltyMultiplier = 0 }, "a");

        Assert.Contains(Check(map), e => e.Code == MapErrorCodes.ZoneInvalid && e.Subject == "z");
    }

    [Theory]
    [InlineData(DiceStage.Sides, "2")]
    [InlineData(DiceStage.Multiply, "2")]
    [InlineData(DiceStage.Reroll, "1")]
    [InlineData(DiceStage.Min, "1")]
    [InlineData(DiceStage.Max, "10")]
    [InlineData(DiceStage.Add, "\"1d6\"")]
    public void Zone_dice_modifier_beyond_count_and_add_by_a_number_is_not_in_this_build(DiceStage stage, string value)
    {
        // D-307: the dice pipeline comes with items (stage 4)
        var zone = new ZoneDefinition { Id = "z", Name = "Зона", DiceModifier = new DiceModifierSpec { Stage = stage, Value = ContentJson.Parse<ContentValue>(value) } };
        var map = MapBuilder.New().Path("start", "a", "finish").Zone(zone, "a");

        AssertOnly(Check(map), MapErrorCodes.ZoneUnsupported, "z");
    }

    [Theory]
    [InlineData(DiceStage.Count)]
    [InlineData(DiceStage.Add)]
    public void Zone_dice_modifier_count_and_add_by_a_number_are_accepted(DiceStage stage)
    {
        var zone = new ZoneDefinition { Id = "z", Name = "Зона", DiceModifier = new DiceModifierSpec { Stage = stage, Value = ContentJson.Parse<ContentValue>("1") } };

        Assert.Empty(Check(MapBuilder.New().Path("start", "a", "finish").Zone(zone, "a")));
    }

    // ---- Everything at once ----

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var map = MapBuilder.New().Path("start", "f", "finish").Cell("f", CellType.Fork).Path("island", "finish").Teleport("island", to: "ghost");

        var codes = Check(map).Select(e => e.Code).ToHashSet();

        Assert.Superset(new HashSet<string> { MapErrorCodes.ForkExits, MapErrorCodes.Unreachable, MapErrorCodes.TeleportTarget }, codes);
    }

    [Fact]
    public void Too_large_map_is_refused_before_the_graph_checks()
    {
        var ids = Enumerable.Range(0, MapValidator.MaxCells).Select(i => $"c{i}").Prepend("start").Append("finish").ToArray();

        var errors = Check(MapBuilder.New().Path(ids));

        Assert.Equal([MapErrorCodes.TooLarge], errors.Select(e => e.Code));
    }
}

internal static class MapBuilderTestExtensions
{
    public static MapBuilder Retyped(this MapBuilder builder, string id, CellType type) => builder.Cell(id, type);
}

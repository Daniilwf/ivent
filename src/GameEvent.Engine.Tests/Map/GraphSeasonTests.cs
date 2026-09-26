using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Map;

/// <summary>
/// The graph map in the season (D-300, D-301, D-308): created with the season in the graph mode, published again by the
/// admin, the cells players stand on kept; the linear mode unchanged.
/// </summary>
public class GraphSeasonTests
{
    private static MapGraph ForkMap() =>
        MapBuilder.New().Path("start", "a", "f", "b1", "j", "k", "finish").Path("f", "c1", "j").Build();

    private static Ruleset GraphRules() => TestRuleset.Create() with
    {
        Features = TestRuleset.Create().Features with { MapMode = MapMode.Graph },
    };

    private static readonly Guid s_seasonId = SequentialIds.Make(0x30000000, 1);

    // ---- Creation ----

    [Fact]
    public void Graph_season_is_created_with_its_map()
    {
        var s = Scenario.New().WithMap(ForkMap()).WithPlayers("Вася");

        Assert.Equal(ForkMap(), s.State.Map);
        Assert.Equal(ForkMap(), Assert.Single(s.Log.OfType<SeasonCreated>()).Map);
        Assert.Equal("start", s.Player("Вася").CellId);
    }

    [Fact]
    public void Graph_season_without_a_map_is_refused()
    {
        var s = Scenario.New();

        s.Act(new CreateSeason(s_seasonId, "Сезон", GraphRules()));

        Assert.Equal(RejectionCodes.MapRequired, s.Last.Rejection!.Code);
    }

    [Fact]
    public void Linear_season_with_a_map_is_refused()
    {
        var s = Scenario.New();

        s.Act(new CreateSeason(s_seasonId, "Сезон", TestRuleset.Create(), Map: ForkMap()));

        Assert.Equal(RejectionCodes.MapNotInLinearMode, s.Last.Rejection!.Code);
    }

    [Fact]
    public void Graph_season_with_an_invalid_map_is_refused_with_every_problem()
    {
        var s = Scenario.New();
        var broken = MapBuilder.New().Path("start", "f", "finish").Cell("f", CellType.Fork).Path("island", "finish").Build();

        s.Act(new CreateSeason(s_seasonId, "Сезон", GraphRules(), Map: broken));

        Assert.Equal(RejectionCodes.MapInvalid, s.Last.Rejection!.Code);
        Assert.Contains(MapErrorCodes.ForkExits, s.Last.Rejection.Detail, StringComparison.Ordinal);
        Assert.Contains(MapErrorCodes.Unreachable, s.Last.Rejection.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Linear_season_keeps_its_generated_chain_and_the_log_of_stage_one()
    {
        // MP1: the flag off is stage 1 exactly — the chain, and a season-created event without any new field
        var s = Scenario.New().WithMapLength(3).WithPlayers("Вася");

        Assert.Equal(LinearMap.Generate(3), s.State.Map);
        var stored = EventCodec.Encode(Assert.Single(s.Log.OfType<SeasonCreated>()));
        Assert.DoesNotContain("zones", stored.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("\"zone\"", stored.Data, StringComparison.Ordinal);
        Assert.Contains("\"cells\":[{\"id\":\"start\",\"type\":\"start\"}", stored.Data, StringComparison.Ordinal);
    }

    [Fact]
    public void Graph_map_survives_the_log_round_trip()
    {
        var map = MapBuilder.New().Path("start", "a", "f", "b", "finish").Path("f", "c", "finish").Teleport("a", to: "c")
            .Zone(new Engine.Content.ZoneDefinition { Id = "z", Name = "Зона", RollFilter = new Engine.Content.GameFilterSpec { Tags = ["Horror"] } }, "c")
            .Cell("b", CellType.PointsBonus, c => c with { Amount = 2, X = 10.5m, Y = 3 })
            .Build();
        var s = Scenario.New().WithMap(map).WithPlayers("Вася");

        var created = Assert.Single(s.Log.OfType<SeasonCreated>());
        var decoded = (SeasonCreated)EventCodec.Decode(EventCodec.Encode(created));

        Assert.Equal(map, decoded.Map);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log.Select(e => EventCodec.Decode(EventCodec.Encode(e)))));
    }

    // ---- Publication ----

    [Fact]
    public void Admin_publishes_a_new_map_with_a_comment()
    {
        var s = Scenario.New().WithMap(ForkMap()).WithPlayers("Вася");
        var next = MapBuilder.New().Path("start", "a", "f", "b1", "j", "k", "l", "finish").Path("f", "c1", "j").Build();

        s.Act(new PublishMap(next, "  Длиннее на клетку  "));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new MapPublished(next, "Длиннее на клетку"), Assert.Single(s.Last.Events));
        Assert.Equal(next, s.State.Map);
    }

    [Fact]
    public void Linear_season_does_not_publish_maps()
    {
        var s = Scenario.New().WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new PublishMap(ForkMap(), "Карта")), RejectionCodes.FeatureDisabled);
    }

    [Fact]
    public void Publication_needs_a_valid_different_map_and_a_comment()
    {
        var s = Scenario.New().WithMap(ForkMap()).WithPlayers("Вася");
        var broken = MapBuilder.New().Path("start", "a").Build();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new PublishMap(broken, "Карта")), RejectionCodes.MapInvalid);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new PublishMap(ForkMap(), "Та же")), RejectionCodes.MapUnchanged);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new PublishMap(LinearMap.Generate(5), " ")), RejectionCodes.CommentRequired);
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new PublishMap(LinearMap.Generate(5), new string('x', Limits.MaxCommentLength + 1))), RejectionCodes.CommentTooLong);
    }

    [Fact]
    public void Map_that_removes_a_cell_a_player_stands_on_is_refused()
    {
        // SPEC «Проверка на дыры»: удалили клетку, где стоит игрок — публикация блокируется
        var s = Scenario.New().WithMap(ForkMap()).WithPlayers("Вася");
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Перенос", CellId: "c1"));
        var withoutC1 = MapBuilder.New().Path("start", "a", "f", "b1", "j", "k", "finish").Path("f", "c2", "j").Build();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new PublishMap(withoutC1, "Карта")), RejectionCodes.MapOccupiedCellRemoved);
    }

    [Fact]
    public void Map_is_published_in_a_draft_and_a_running_season_only()
    {
        var s = Scenario.New().WithMap(ForkMap()).AsDraft().WithPlayers("Вася");
        s.Act(new PublishMap(LinearMap.Generate(5), "Черновик"));
        ScenarioAssert.Accepted(s);

        s.MoveStatusTo(SeasonStatus.Closing);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new PublishMap(ForkMap(), "Поздно")), RejectionCodes.SeasonClosed);
    }

    [Fact]
    public void Path_that_no_longer_lies_on_the_map_restarts_at_the_player()
    {
        // D-308: the walked cells a → f are gone as an arrow; moving back then follows the primary incoming edges
        var s = Scenario.New().WithMap(ForkMap()).WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithPlayers("Вася", "Петя");
        s.Roll("Вася").Start("Вася").NextRandom(1).Complete("Вася");
        Assert.Equal(["start", "a"], s.Player("Вася").Path.Segments[^1].Cells);
        var rerouted = MapBuilder.New().Path("start", "x", "a", "f", "b1", "j", "k", "finish").Path("f", "c1", "j").Build();

        s.Act(new PublishMap(rerouted, "Новый вход"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(PlayerPath.At("a"), s.Player("Вася").Path);
        Assert.Equal(PlayerPath.At("start"), s.Player("Петя").Path);
    }

    [Fact]
    public void Path_that_still_lies_on_the_map_is_kept()
    {
        var s = Scenario.New().WithMap(ForkMap()).WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithPlayers("Вася");
        s.Roll("Вася").Start("Вася").NextRandom(1).Complete("Вася");
        var path = s.Player("Вася").Path;
        var longer = MapBuilder.New().Path("start", "a", "f", "b1", "j", "k", "l", "finish").Path("f", "c1", "j").Build();

        s.Act(new PublishMap(longer, "Длиннее"));

        Assert.Equal(path, s.Player("Вася").Path);
    }

    [Fact]
    public void Publication_is_not_undone()
    {
        var s = Scenario.New().WithMap(ForkMap()).WithPlayers("Вася");
        s.Act(new PublishMap(LinearMap.Generate(5), "Карта"));
        var published = s.LastCommandId;

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new UndoCommand(published, "Назад")), RejectionCodes.UndoNotUndoable);
    }

    [Fact]
    public void Undo_that_would_bring_a_player_back_to_a_removed_cell_is_refused()
    {
        // The admin moved Вася to c1, then published a map without c1 after moving him back by hand
        var s = Scenario.New().WithMap(ForkMap()).WithPlayers("Вася", "Петя");
        s.Act(new AdjustPlayer(s.PlayerId("Петя"), "На c1", CellId: "c1"));
        var moved = s.LastCommandId;
        s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Обратно", CellId: "a"));
        var back = s.LastCommandId;
        s.Act(new PublishMap(MapBuilder.New().Path("start", "a", "f", "b1", "j", "k", "finish").Path("f", "c2", "j").Build(), "Без c1"));

        // Undoing «back» would put Петя on c1 again
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new UndoCommand(back, "Назад")), RejectionCodes.UndoCellNotOnMap);
        Assert.NotEqual(Guid.Empty, moved);
    }

    // ---- Mode ----

    [Fact]
    public void Linear_season_may_switch_to_the_graph_and_then_publish()
    {
        // D-301: the chain is a valid graph
        var s = Scenario.New().WithMapLength(5).WithPlayers("Вася");
        s.WithRuleset(r => r with { Features = r.Features with { MapMode = MapMode.Graph } });

        s.Act(new PublishMap(ForkMap(), "Настоящая карта"));

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Graph_season_does_not_switch_back_to_linear()
    {
        var s = Scenario.New().WithMap(ForkMap()).WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new ChangeRuleset(x.Ruleset with { Features = x.Ruleset.Features with { MapMode = MapMode.Linear } })),
            RejectionCodes.MapModeFixed);
    }
}

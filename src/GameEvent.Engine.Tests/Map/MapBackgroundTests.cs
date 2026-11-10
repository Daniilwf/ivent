using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Map;

/// <summary>
/// The map's background picture (SPEC «Карта — свободный граф клеток поверх фоновой картинки», «Редактор»: загрузка
/// фона; D-277): an optional reference to a stored picture with its natural size, part of the map and published with it.
/// The rules never read it; a map without one reads and writes as before.
/// </summary>
public class MapBackgroundTests
{
    private static readonly Guid s_file = SequentialIds.Make(0x70000000, 1);

    private static readonly Ruleset s_rules = TestRuleset.Create() with
    {
        Features = TestRuleset.Create().Features with { MapMode = MapMode.Graph },
    };

    private static MapGraph ForkMap() =>
        MapBuilder.New().Path("start", "a", "f", "b1", "j", "k", "finish").Path("f", "c1", "j").Build();

    private static MapGraph WithBackground(MapGraph map, int width = 2000, int height = 1400, Guid? file = null) =>
        map with { Background = new MapBackground(file ?? s_file, width, height) };

    [Fact]
    public void Map_with_a_background_is_valid()
    {
        Assert.Empty(MapValidator.Validate(WithBackground(ForkMap()), s_rules));
    }

    [Theory]
    [InlineData(0, 1400)]
    [InlineData(2000, 0)]
    [InlineData(-5, 1400)]
    [InlineData(MapValidator.MaxBackgroundSide + 1, 1400)]
    [InlineData(2000, MapValidator.MaxBackgroundSide + 1)]
    public void Background_has_a_natural_size_within_the_ceiling(int width, int height)
    {
        var errors = MapValidator.Validate(WithBackground(ForkMap(), width, height), s_rules);

        Assert.Equal(MapErrorCodes.BackgroundInvalid, Assert.Single(errors).Code);
        Assert.Equal("background", errors[0].Subject);
    }

    [Fact]
    public void Background_names_its_file()
    {
        var errors = MapValidator.Validate(WithBackground(ForkMap(), file: Guid.Empty), s_rules);

        Assert.Equal(MapErrorCodes.BackgroundInvalid, Assert.Single(errors).Code);
    }

    [Fact]
    public void Background_is_published_with_the_map_and_a_new_one_is_a_change()
    {
        var s = Scenario.New().WithMap(ForkMap()).WithPlayers("Вася");
        var withPicture = WithBackground(ForkMap());

        s.Act(new PublishMap(withPicture, "Фон карты"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new MapPublished(withPicture, "Фон карты"), Assert.Single(s.Last.Events));
        Assert.Equal(withPicture, s.State.Map);

        // The same picture again changes nothing; another picture, or none, is a new version of the map
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new PublishMap(WithBackground(ForkMap()), "Тот же фон")), RejectionCodes.MapUnchanged);
        var replaced = WithBackground(ForkMap(), 1600, 900, SequentialIds.Make(0x70000000, 2));
        s.Act(new PublishMap(replaced, "Новый фон"));
        ScenarioAssert.Accepted(s);
        s.Act(new PublishMap(ForkMap(), "Без фона"));
        ScenarioAssert.Accepted(s);
        Assert.Null(s.State.Map.Background);
    }

    [Fact]
    public void Map_with_an_invalid_background_is_not_published()
    {
        var s = Scenario.New().WithMap(ForkMap()).WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new PublishMap(WithBackground(ForkMap(), 0, 10), "Фон")), RejectionCodes.MapInvalid);
    }

    [Fact]
    public void Background_does_not_move_anyone()
    {
        // A picture under the cells is not a rule: positions and paths stay as they were
        var s = Scenario.New().WithMap(ForkMap()).WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithPlayers("Вася", "Петя");
        s.Roll("Вася").Start("Вася").NextRandom(1).Complete("Вася");
        var before = s.State.Players;

        s.Act(new PublishMap(WithBackground(ForkMap()), "Фон"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(before, s.State.Players);
    }

    [Fact]
    public void Map_without_a_background_is_written_as_before_and_one_with_it_round_trips()
    {
        // Old maps in the log have no background field: they read as a map without a picture, and such a map writes no field
        var plain = JsonSerializer.Serialize(ForkMap(), EngineJson.Options);
        Assert.DoesNotContain("background", plain, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<MapGraph>(plain, EngineJson.Options)!.Background);

        var json = JsonSerializer.Serialize(WithBackground(ForkMap()), EngineJson.Options);
        Assert.Contains("\"background\":{\"fileId\":\"70000000-0000-0000-0000-000000000001\",\"width\":2000,\"height\":1400}", json, StringComparison.Ordinal);
        Assert.Equal(WithBackground(ForkMap()), JsonSerializer.Deserialize<MapGraph>(json, EngineJson.Options));
    }
}

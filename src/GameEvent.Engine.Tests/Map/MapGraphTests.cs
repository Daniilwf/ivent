using GameEvent.Engine.Content;
using GameEvent.Engine.Map;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Map;

/// <summary>Looking cells and zones up on the map graph (SPEC «Карта»; D-300, D-307).</summary>
public class MapGraphTests
{
    [Fact]
    public void Unknown_cell_names_itself()
    {
        var error = Assert.Throws<KeyNotFoundException>(() => LinearMap.Generate(3).CellById("ghost"));

        Assert.Equal("Cell 'ghost' is not on the map.", error.Message);
    }

    [Fact]
    public void Zone_of_a_cell_is_its_zone_rules_or_nothing()
    {
        var swamp = new ZoneDefinition { Id = "swamp", Name = "Болото" };
        var map = MapBuilder.New().Path("start", "a", "b", "finish").Zone(swamp, "a").Cell("b", CellType.Empty, c => c with { Zone = "ghost" }).Build();

        Assert.Equal(swamp, map.ZoneOf("a"));
        Assert.Null(map.ZoneOf("b"));
        Assert.Null(map.ZoneOf("start"));
        Assert.Null(map.ZoneOf("nowhere"));
    }
}

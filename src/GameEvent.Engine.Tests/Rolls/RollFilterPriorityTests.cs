using GameEvent.Engine.Pool;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rolls;

/// <summary>
/// G12: roll filters are predicates over games with priority effect &gt; zone &gt; normal; when filters intersect into
/// nothing, the more important one wins (SPEC «Пул игр и ролл», «Уточнения»: Ролл; D-92). Filters go from the highest
/// priority down; a lower filter that would leave no available game is skipped. The highest filter present is never
/// skipped unless it is a zone filter (SPEC «Уточнения»: пустой пул — only the zone filter is dropped): a special roll
/// without games gives a result with no available game, so the roll is refused and the admin gets a signal. A dropped
/// top zone filter hands the top to the next filter, under the same rule. Stage 1 has no real filters, so the
/// mechanism is checked with test filters on <see cref="RollFilters.Apply"/>.
/// </summary>
public class RollFilterPriorityTests
{
    private static readonly Game s_silentHill = G(1, "Silent Hill", 12, "Horror");
    private static readonly Game s_limbo = G(2, "Limbo", 4, "Horror", "Puzzle");
    private static readonly Game s_tetris = G(3, "Tetris", 2, "Puzzle");
    private static readonly Game s_witcher = G(4, "Witcher 3", 50, "RPG");
    private static readonly Game s_portal = G(5, "Portal", 5, "Puzzle");

    private static readonly IReadOnlyList<Game> s_all = [s_silentHill, s_limbo, s_tetris, s_witcher, s_portal];

    private static bool Everything(Game _) => true;

    private static Game G(int n, string title, decimal hours, params string[] tags) =>
        new(SequentialIds.Make(0x20000000, n), title, [.. tags], hours);

    private static RollFilter Tag(RollFilterPriority priority, string tag) =>
        new(priority, $"tag:{tag}", g => g.Tags.Contains(tag));

    private static RollFilter Short(RollFilterPriority priority, decimal maxHours) =>
        new(priority, $"short:{maxHours}", g => g.Hours <= maxHours);

    private static RollFilter Nothing(RollFilterPriority priority) =>
        new(priority, "nothing", _ => false);

    [Fact]
    public void No_filters_leave_the_candidates_unchanged()
    {
        Assert.Equal(s_all, RollFilters.Apply(s_all, Everything, []));
    }

    [Fact]
    public void No_candidates_give_nothing()
    {
        Assert.Empty(RollFilters.Apply([], Everything, [Tag(RollFilterPriority.Effect, "Horror")]));
    }

    [Theory]
    [InlineData(RollFilterPriority.Effect)]
    [InlineData(RollFilterPriority.Zone)]
    [InlineData(RollFilterPriority.Normal)]
    public void One_filter_narrows_the_candidates_keeping_their_order(RollFilterPriority priority)
    {
        var result = RollFilters.Apply(s_all, Everything, [Tag(priority, "Puzzle")]);

        Assert.Equal([s_limbo, s_tetris, s_portal], result);
    }

    [Fact]
    public void Filters_of_every_priority_that_intersect_are_all_applied()
    {
        // Puzzle games (effect) ∩ up to 5 hours (zone) ∩ not Portal (normal) = Limbo, Tetris
        var notPortal = new RollFilter(RollFilterPriority.Normal, "not portal", g => g.Id != s_portal.Id);

        var result = RollFilters.Apply(
            s_all, Everything, [notPortal, Short(RollFilterPriority.Zone, 5), Tag(RollFilterPriority.Effect, "Puzzle")]);

        Assert.Equal([s_limbo, s_tetris], result);
    }

    [Fact]
    public void Empty_intersection_of_zone_and_effect_drops_the_zone_and_keeps_the_effect()
    {
        // The zone comes first in the list, but the effect is more important (SPEC: the higher priority wins)
        var result = RollFilters.Apply(s_all, Everything, [Tag(RollFilterPriority.Zone, "RPG"), Tag(RollFilterPriority.Effect, "Horror")]);

        Assert.Equal([s_silentHill, s_limbo], result);
    }

    [Fact]
    public void Empty_intersection_of_normal_and_zone_drops_the_normal_filter()
    {
        var result = RollFilters.Apply(s_all, Everything, [Short(RollFilterPriority.Normal, 3), Tag(RollFilterPriority.Zone, "RPG")]);

        Assert.Equal([s_witcher], result);
    }

    [Fact]
    public void Normal_filter_is_dropped_when_it_would_empty_the_effect_and_zone_result()
    {
        // Effect Puzzle ∩ zone up to 5 hours = Limbo, Tetris, Portal; normal RPG would leave nothing
        var result = RollFilters.Apply(
            s_all, Everything, [Tag(RollFilterPriority.Effect, "Puzzle"), Short(RollFilterPriority.Zone, 5), Tag(RollFilterPriority.Normal, "RPG")]);

        Assert.Equal([s_limbo, s_tetris, s_portal], result);
    }

    [Fact]
    public void Effect_that_alone_leaves_nothing_is_kept_and_the_roll_has_no_available_game()
    {
        // The pool has no game for the special roll: it does not silently become an ordinary roll (D-92), the zone
        // below it is not reached; the empty result makes the roll refuse and the admin see the signal
        var result = RollFilters.Apply(s_all, Everything, [Nothing(RollFilterPriority.Effect), Tag(RollFilterPriority.Zone, "Horror")]);

        Assert.Empty(result);
    }

    [Fact]
    public void Every_filter_leaving_nothing_gives_nothing()
    {
        // The effect is the top filter and is kept; the zone and normal filters below it do not matter
        var result = RollFilters.Apply(
            s_all, Everything, [Nothing(RollFilterPriority.Effect), Nothing(RollFilterPriority.Zone), Nothing(RollFilterPriority.Normal)]);

        Assert.Empty(result);
    }

    [Fact]
    public void Normal_filter_alone_leaving_nothing_gives_nothing()
    {
        Assert.Empty(RollFilters.Apply(s_all, Everything, [Nothing(RollFilterPriority.Normal)]));
    }

    [Fact]
    public void Zone_filter_alone_leaving_nothing_is_dropped()
    {
        // SPEC «Уточнения»: пустой пул — the zone filter is dropped, the roll goes over all games
        Assert.Equal(s_all, RollFilters.Apply(s_all, Everything, [Nothing(RollFilterPriority.Zone)]));
    }

    [Fact]
    public void Dropped_zone_filter_hands_the_top_to_the_normal_filter()
    {
        var result = RollFilters.Apply(s_all, Everything, [Nothing(RollFilterPriority.Zone), Tag(RollFilterPriority.Normal, "Puzzle")]);

        Assert.Equal([s_limbo, s_tetris, s_portal], result);
    }

    [Fact]
    public void Zone_dropped_then_normal_alone_leaving_nothing_gives_nothing()
    {
        // The zone is dropped, the normal filter becomes the top one and, not being a zone filter, is kept
        var result = RollFilters.Apply(s_all, Everything, [Nothing(RollFilterPriority.Zone), Nothing(RollFilterPriority.Normal)]);

        Assert.Empty(result);
    }

    [Fact]
    public void Every_zone_filter_leaving_nothing_is_dropped_one_after_another()
    {
        // Two zone filters that each leave nothing: both are dropped, the normal filter below is the top and applies
        var result = RollFilters.Apply(
            s_all, Everything, [Nothing(RollFilterPriority.Zone), Tag(RollFilterPriority.Zone, "Shooter"), Tag(RollFilterPriority.Normal, "RPG")]);

        Assert.Equal([s_witcher], result);
    }

    [Fact]
    public void Second_zone_filter_that_empties_the_first_is_skipped()
    {
        // The first zone filter is the top and kept; the second one is lower and would leave nothing
        var result = RollFilters.Apply(s_all, Everything, [Tag(RollFilterPriority.Zone, "Horror"), Tag(RollFilterPriority.Zone, "RPG")]);

        Assert.Equal([s_silentHill, s_limbo], result);
    }

    [Fact]
    public void Lower_filter_that_leaves_only_unavailable_games_counts_as_empty_and_is_skipped()
    {
        // Silent Hill is only a miss (someone plays it): the zone «Horror, not Limbo» under the effect Horror would leave
        // no available game, so it is skipped
        var available = (Game g) => g.Id != s_silentHill.Id;
        var horrorNotLimbo = new RollFilter(RollFilterPriority.Zone, "horror, not limbo", g => g.Tags.Contains("Horror") && g.Id != s_limbo.Id);

        var result = RollFilters.Apply(s_all, available, [Tag(RollFilterPriority.Effect, "Horror"), horrorNotLimbo]);

        Assert.Equal([s_silentHill, s_limbo], result);
    }

    [Fact]
    public void Top_zone_filter_that_leaves_only_unavailable_games_is_dropped()
    {
        var available = (Game g) => g.Id != s_silentHill.Id;
        var horrorNotLimbo = new RollFilter(RollFilterPriority.Zone, "horror, not limbo", g => g.Tags.Contains("Horror") && g.Id != s_limbo.Id);

        var result = RollFilters.Apply(s_all, available, [horrorNotLimbo, Tag(RollFilterPriority.Normal, "Puzzle")]);

        Assert.Equal([s_limbo, s_tetris, s_portal], result);
    }

    [Theory]
    [InlineData(RollFilterPriority.Effect)]
    [InlineData(RollFilterPriority.Normal)]
    public void Top_non_zone_filter_that_leaves_only_unavailable_games_is_kept_with_no_available_game(RollFilterPriority priority)
    {
        // «Horror, not Limbo» leaves only Silent Hill, a miss: kept, so the result has no available game and the lower
        // Puzzle filter is not reached
        var available = (Game g) => g.Id != s_silentHill.Id;
        var horrorNotLimbo = new RollFilter(priority, "horror, not limbo", g => g.Tags.Contains("Horror") && g.Id != s_limbo.Id);
        var lower = priority == RollFilterPriority.Effect ? RollFilterPriority.Zone : RollFilterPriority.Normal;

        var result = RollFilters.Apply(s_all, available, [horrorNotLimbo, Tag(lower, "Puzzle")]);

        Assert.False(result.Any(available), "The kept top filter leaves only misses.");
        Assert.DoesNotContain(s_tetris, result);
    }

    [Fact]
    public void Filter_that_leaves_one_available_game_is_kept_with_its_misses()
    {
        // Horror leaves Silent Hill (a miss) and Limbo (available): kept; the miss stays so the draw can log it
        var available = (Game g) => g.Id != s_silentHill.Id;

        var result = RollFilters.Apply(s_all, available, [Tag(RollFilterPriority.Effect, "Horror")]);

        Assert.Equal([s_silentHill, s_limbo], result);
    }

    [Fact]
    public void Filters_of_the_same_priority_are_applied_in_the_given_order()
    {
        // Horror and RPG exclude each other: the first given wins, the second would empty and is dropped
        var horrorFirst = RollFilters.Apply(s_all, Everything, [Tag(RollFilterPriority.Effect, "Horror"), Tag(RollFilterPriority.Effect, "RPG")]);
        var rpgFirst = RollFilters.Apply(s_all, Everything, [Tag(RollFilterPriority.Effect, "RPG"), Tag(RollFilterPriority.Effect, "Horror")]);

        Assert.Equal([s_silentHill, s_limbo], horrorFirst);
        Assert.Equal([s_witcher], rpgFirst);
    }

    [Fact]
    public void Same_priority_filters_that_intersect_are_both_applied()
    {
        var result = RollFilters.Apply(s_all, Everything, [Tag(RollFilterPriority.Zone, "Puzzle"), Short(RollFilterPriority.Zone, 4)]);

        Assert.Equal([s_limbo, s_tetris], result);
    }

    [Fact]
    public void Priority_decides_regardless_of_the_order_filters_are_given_in()
    {
        var effect = Tag(RollFilterPriority.Effect, "Horror");
        var zone = Tag(RollFilterPriority.Zone, "RPG");
        var normal = Short(RollFilterPriority.Normal, 4);

        foreach (var order in new RollFilter[][] { [effect, zone, normal], [normal, zone, effect], [zone, normal, effect], [normal, effect, zone] })
        {
            // Effect Horror keeps Silent Hill and Limbo; zone RPG would empty and is dropped; normal ≤ 4 h leaves Limbo
            Assert.Equal([s_limbo], RollFilters.Apply(s_all, Everything, order));
        }
    }
}

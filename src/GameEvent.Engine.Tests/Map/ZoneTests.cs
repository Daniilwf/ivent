using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Content;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Map;

/// <summary>
/// Zones (SPEC «Зоны», «Фильтры ролла», «Пустой пул»; D-307): the roll is filtered by the zone the player stands in
/// (effect &gt; zone &gt; normal), an empty pool drops the zone filter, the zone is fixed at the roll with its dice
/// modifier and drop penalty multiplier. The zone and the cells of CONTENT.md are acceptance examples.
/// </summary>
public class ZoneTests
{
    /// <summary>«Болото ужаса» of CONTENT.md: Horror only, +1 to the dice sum, drop penalty × 1.5.</summary>
    private static ZoneDefinition Swamp() => ContentJson.Parse<ZoneDefinition>(ContentExamplesTests.Example("zone"));

    /// <summary>start → a → f → h1 → h2 → j → k → l → finish, f → n1 → j; h1 and h2 in <paramref name="zone"/>.</summary>
    private static MapGraph ZoneMap(ZoneDefinition zone) =>
        MapBuilder.New().Path("start", "a", "f", "h1", "h2", "j", "k", "l", "finish").Path("f", "n1", "j").Zone(zone, "h1", "h2").Build();

    /// <summary>
    /// One category «Any» on the wheel; the zone filters by the games' other tags. Pool order: Silent Hill, Fatal Frame
    /// (Horror), Tetris, Portal (Puzzle), 3 hours each — one d4.
    /// </summary>
    private static Scenario Season(ZoneDefinition? zone = null, bool puzzlesOnly = false)
    {
        var s = Scenario.New().WithMap(ZoneMap(zone ?? Swamp())).WithCategory("Any");
        if (!puzzlesOnly)
        {
            s.WithGame("Silent Hill", 3, "Any", "Horror").WithGame("Fatal Frame", 3, "Any", "Horror");
        }

        return s.WithGame("Tetris", 3, "Any", "Puzzle").WithGame("Portal", 3, "Any", "Puzzle").WithPlayers("Вася", "Петя");
    }

    private static Scenario At(Scenario s, string cell) => s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Перенос", CellId: cell));

    private static RunState Run(Scenario s) =>
        s.State.Runs.Values.Where(r => r.PlayerId == s.PlayerId("Вася")).OrderBy(r => r.StartedAt).Last();

    /// <summary>In the swamp: the wheel's only sector, then game <paramref name="index"/> among the games the zone leaves.</summary>
    private static Scenario RollInZone(Scenario s, int index = 0) => s.NextRandom(0, index).Roll("Вася");

    private static string RolledTitle(Scenario s) => s.GameTitle(Assert.Single(s.LastEvents<GameRolled>()).GameId);

    [Fact]
    public void Content_zone_example_is_a_valid_zone_of_a_map()
    {
        Assert.Empty(MapValidator.Validate(ZoneMap(Swamp()), Season().Ruleset));
    }

    [Fact]
    public void Roll_in_a_zone_draws_only_its_games()
    {
        var s = At(Season(), "h1");

        RollInZone(s, 1);

        Assert.Equal("Fatal Frame", RolledTitle(s));
        Assert.Equal(new RunZone("horror-swamp", Swamp().DiceModifier, 1.5m), Assert.Single(s.LastEvents<GameRolled>()).Snapshot.Zone);

        // Two Horror games only: a third draw index is outside the zone's games
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Сброс", DiscardOffer: true));
        Assert.Throws<InvalidOperationException>(() => RollInZone(s, 2));
    }

    [Fact]
    public void Roll_outside_any_zone_is_the_ordinary_one_and_logged_as_before()
    {
        var s = Season();

        s.NextRandom(0, 2).Roll("Вася");

        var rolled = Assert.Single(s.LastEvents<GameRolled>());
        Assert.Equal("Tetris", RolledTitle(s));
        Assert.Null(rolled.Snapshot.Zone);
        Assert.DoesNotContain("\"zone\"", EventCodec.Encode(rolled).Data, StringComparison.Ordinal);
    }

    [Fact]
    public void Zone_filter_is_dropped_when_its_pool_is_empty()
    {
        // SPEC «Пустой пул»: фильтр зоны снимается
        var s = At(Season(puzzlesOnly: true), "h1");

        s.NextRandom(0, 1).Roll("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal("Portal", RolledTitle(s));
        Assert.Empty(PoolStats.PlayersWithoutGames(s.State, s.Context().Pool));
    }

    [Fact]
    public void Zone_filter_is_dropped_when_its_games_are_all_taken()
    {
        // Петя plays Silent Hill, Вася dropped Fatal Frame: no Horror game is available to Вася in the swamp
        var s = Season();
        s.NextRandom(0, 0).Roll("Петя").Start("Петя");
        // Silent Hill is a miss («Сейчас играет Петя»), the next draw is Fatal Frame
        s.NextRandom(0, 0, 0).Roll("Вася").Start("Вася").NextRandom(1, 1).Act(new DropRun(s.PlayerId("Вася")));
        Assert.Equal(["Silent Hill", "Fatal Frame"], s.State.Runs.Values.OrderBy(r => r.StartedAt).Select(r => s.GameTitle(r.GameId)));
        At(s, "h1");

        s.Roll("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Contains(RolledTitle(s), new[] { "Tetris", "Portal" });
    }

    [Fact]
    public void Empty_pool_without_the_zone_signals_the_admin()
    {
        // G10: the zone filter is gone and still nothing — the admin gets the signal
        var s = Scenario.New().WithMap(ZoneMap(Swamp())).WithCategory("Any").WithPlayers("Вася");
        At(s, "h1");

        Assert.Equal([s.PlayerId("Вася")], PoolStats.PlayersWithoutGames(s.State, s.Context().Pool));
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.NoAvailableGames);
    }

    [Fact]
    public void Zone_add_changes_the_points_and_the_steps_and_a_reject_takes_them_back()
    {
        var s = At(Season(), "h1");
        RollInZone(s).Start("Вася");

        // One d4 showing 2, +1 of the swamp
        s.NextRandom(2).Complete("Вася");

        Assert.Equal(3, Assert.Single(s.LastEvents<PointsChanged>(), p => p.Reason == PointsReason.CompletionRoll).Delta);
        Assert.Equal(("k", 3), (s.Player("Вася").CellId, s.Player("Вася").Points));

        s.Act(new RejectProof(Run(s).RunId, "нет пруфа"));

        Assert.Equal(("h1", 0), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    [Fact]
    public void Zone_count_adds_dice_after_the_limit_and_corrections_keep_them()
    {
        var zone = new ZoneDefinition
        {
            Id = "fog",
            Name = "Туман",
            RollFilter = new GameFilterSpec { Tags = ["Horror"] },
            DiceModifier = new DiceModifierSpec { Stage = DiceStage.Count, Value = ContentJson.Parse<ContentValue>("1") },
        };
        var s = At(Season(zone), "h1");
        RollInZone(s).Start("Вася");

        s.NextRandom(1, 2).Complete("Вася");

        Assert.Equal(2, Run(s).Dice.Count);
        Assert.Equal(3, s.Player("Вася").Points);

        // 6 hours: two dice by the hours, three with the zone
        s.NextRandom(1).Act(new CorrectRunHours(Run(s).RunId, 6, "часы"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(3, Run(s).Dice.Count);
        Assert.Equal(4, s.Player("Вася").Points);
    }

    [Fact]
    public void Negative_zone_add_never_takes_the_total_below_zero()
    {
        var zone = new ZoneDefinition
        {
            Id = "mud",
            Name = "Грязь",
            DiceModifier = new DiceModifierSpec { Stage = DiceStage.Add, Value = ContentJson.Parse<ContentValue>("-3") },
        };
        var s = At(Season(zone), "h1");
        s.NextRandom(0, 0).Roll("Вася").Start("Вася");

        s.NextRandom(2).Complete("Вася");

        Assert.Equal(("h1", 0), (s.Player("Вася").CellId, s.Player("Вася").Points));
        Assert.Empty(s.LastEvents<PlayerMoved>());

        // A harder difficulty: the die becomes 3, the total still 0
        s.Act(new ChangeRunDifficulty(Run(s).RunId, Difficulty.Hard, "сложная"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(0, s.Player("Вася").Points);
    }

    [Fact]
    public void Drop_penalty_is_multiplied_by_the_zone_of_the_roll()
    {
        var s = At(Season(), "h2");
        RollInZone(s).Start("Вася");

        // 1 + 2 = 3, × 1.5 = 4.5 → 5 (halves away from zero); only four cells lie behind h2
        s.NextRandom(1, 2).Act(new DropRun(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Equal(-5, Assert.Single(s.LastEvents<PointsChanged>()).Delta);
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal((-5, "start"), (moved.Steps, moved.To));
    }

    [Fact]
    public void Zone_is_fixed_at_the_roll()
    {
        // SPEC «Зона фиксируется в момент ролла»: leaving the swamp mid-run keeps its rules for this run…
        var s = At(Season(), "h1");
        RollInZone(s).Start("Вася");
        At(s, "n1");
        s.NextRandom(2).Complete("Вася");
        Assert.Equal(("l", 3), (s.Player("Вася").CellId, s.Player("Вася").Points));

        // …and entering it mid-run changes nothing until the next roll
        s.NextRandom(0, 2).Roll("Вася").Start("Вася");
        At(s, "h2");
        s.NextRandom(2).Complete("Вася");
        Assert.Equal(5, s.Player("Вася").Points);
        Assert.Null(Run(s).Snapshot.Zone);
    }

    [Theory]
    [InlineData("""{ "tags": ["horror"] }""", true)]
    [InlineData("""{ "tags": ["Puzzle", "Horror"] }""", true)]
    [InlineData("""{ "tags": ["Puzzle"] }""", false)]
    [InlineData("""{ "maxHours": 10 }""", true)]
    [InlineData("""{ "maxHours": 5 }""", false)]
    [InlineData("""{ "minHours": 8 }""", true)]
    [InlineData("""{ "minHours": 9 }""", false)]
    [InlineData("""{ "releaseYearBefore": 2000 }""", true)]
    [InlineData("""{ "releaseYearBefore": 1999 }""", false)]
    [InlineData("""{ "tags": ["Horror"], "maxHours": 5 }""", false)]
    public void Zone_filter_is_a_predicate_over_tags_hours_and_year(string filter, bool matches)
    {
        var game = new Game(Guid.Empty, "Silent Hill", ["Horror", "Classic"], 8, ReleaseYear: 1999);

        Assert.Equal(matches, Rolling.Matches(ContentJson.Parse<GameFilterSpec>(filter), game));
    }

    [Fact]
    public void Game_without_hours_or_a_year_does_not_pass_a_bound_on_them()
    {
        var game = new Game(Guid.Empty, "Unknown", ["Horror"], Hours: null);

        Assert.False(Rolling.Matches(new GameFilterSpec { MaxHours = 100 }, game));
        Assert.False(Rolling.Matches(new GameFilterSpec { MinHours = 0 }, game));
        Assert.False(Rolling.Matches(new GameFilterSpec { ReleaseYearBefore = 3000 }, game));
        Assert.True(Rolling.Matches(new GameFilterSpec { Tags = ["Horror"] }, game));
    }

    // ---- The cells of CONTENT.md (acceptance) ----

    /// <summary>The cells example read as map cells, joined by a line: start → cells… → x → c41 → y → finish.</summary>
    private static MapBuilder ContentCells(bool withShopAndEvent)
    {
        var cells = ContentJson.Parse<EquatableArray<Cell>>(ContentExamplesTests.Example("cells"));
        var builder = MapBuilder.New();
        var line = new List<string> { "start" };
        foreach (var cell in cells.Where(c => withShopAndEvent || c.Type is not (CellType.Shop or CellType.Event)))
        {
            builder.Cell(cell.Id, cell.Type, _ => cell);
            line.Add(cell.Id);
        }

        return builder.Path([.. line, "x", "c41", "y", "finish"]);
    }

    [Fact]
    public void Content_cells_read_as_map_cells()
    {
        var cells = ContentJson.Parse<EquatableArray<Cell>>(ContentExamplesTests.Example("cells"));

        Assert.Equal(
            [
                new Cell("c12", CellType.Shop) { Grants = "shop-coupon" },
                new Cell("c17", CellType.Event) { Deck = "zone" },
                new Cell("c23", CellType.Teleport) { To = "c41" },
                new Cell("c30", CellType.PointsBonus) { Amount = 3 },
                new Cell("c40", CellType.Checkpoint),
            ],
            cells);
    }

    [Fact]
    public void Content_shop_and_event_cells_wait_for_their_mechanics()
    {
        var errors = MapValidator.Validate(ContentCells(withShopAndEvent: true).Build(), Season().Ruleset);

        Assert.Equal(
            [(MapErrorCodes.FeatureDisabled, "c12"), (MapErrorCodes.FeatureDisabled, "c17")],
            errors.Select(e => (e.Code, e.Subject)).Order());
    }

    [Fact]
    public void Content_teleport_bonus_and_checkpoint_work_as_the_doc_says()
    {
        // start → c23 → c30 → c40 → x → c41 → y → finish
        var s = Scenario.New().WithMap(ContentCells(withShopAndEvent: false).Build())
            .WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithGame("Fatal Frame", 3, "Horror").WithGame("Siren", 3, "Horror")
            .WithPlayers("Вася");

        // A stop on c23 goes to c41
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1).Complete("Вася");
        Assert.Equal("c41", s.Player("Вася").CellId);

        // A stop on c30 gives 3 points
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Назад", CellId: "c23"));
        s.RollTitle("Вася", "Fatal Frame").Start("Вася").NextRandom(1).Complete("Вася");
        Assert.Equal(("c30", 1 + 1 + 3), (s.Player("Вася").CellId, s.Player("Вася").Points));

        // A push back from y stops on c40
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Вперёд", CellId: "y"));
        s.RollTitle("Вася", "Siren").Start("Вася").NextRandom(4, 4).Act(new DropRun(s.PlayerId("Вася")));
        Assert.Equal("c40", s.Player("Вася").CellId);
    }
}

using GameEvent.Engine.Content;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Content;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Decks;

/// <summary>
/// Acceptance of docs/CONTENT.md «Примеры ивентов» 1, 2, 4 and «Зона, клетки…» (EV14, EV15): each example is published
/// from the doc as written, drawn on a stop on an event cell and played at once on the one who drew it (D-500). Decks are
/// wheels with a kind (D-501); <c>deck: zone</c> draws from the zone of the cell the player stands on, else from
/// <c>events.defaultDeck</c> (risky in these tests, D-502). Cells trigger on any stop, a push by another player's item too,
/// but not on the destination of a teleport (SPEC «Движение»). The first finisher draws nothing (D-510). A deck with nothing
/// to draw leaves a manual effect (D-501). Pinned test ruleset: dice by hours = hours / 3 rounded, normal = d4.
/// </summary>
public class ContentEventAcceptanceTests
{
    private static readonly string[] s_events = ["tailwind", "horror-week", "gamble", "forced-genre", "orange", "pistol"];

    private static WheelDefinition[] StandardDecks() =>
    [
        EventScenario.Deck(EventKind.Good, null, "tailwind"),
        EventScenario.Deck(EventKind.Bad, null, "horror-week"),
        EventScenario.Deck(EventKind.Risky, null, "gamble"),
    ];

    /// <summary>A season on <paramref name="map"/>: one Horror category of 3-hour games (one d4), the players, the doc's events.</summary>
    private static Scenario Season(MapGraph map, int seed = 42, WheelDefinition[]? decks = null)
    {
        var s = EventScenario.New(seed).WithMap(map).WithCategory("Horror")
            .WithGame("Silent Hill", 3, "Horror").WithGame("Fatal Frame", 3, "Horror").WithGame("Siren", 3, "Horror")
            .WithPlayers("Вася", "Петя");
        return s.WithContent(EventScenario.Pack(s_events, decks ?? StandardDecks()));
    }

    private static Scenario At(Scenario s, string player, string cell)
    {
        s.Act(new AdjustPlayer(s.PlayerId(player), "Позиция для теста", CellId: cell));
        return s.Last.IsAccepted ? s : throw new InvalidOperationException($"Moving {player} was rejected: {s.Last.Rejection}");
    }

    /// <summary>The player steps one cell forward with an «Апельсин» (CONTENT.md item 1); <paramref name="random"/> is scripted first.</summary>
    private static Scenario Orange(Scenario s, string player, params int[] random)
    {
        s.Give(player, "orange");
        return s.NextRandom(random).Used(player, "orange");
    }

    private static EventDrawn Drawn(Scenario s) => Assert.Single(s.LastEvents<EventDrawn>());

    // ---- 1. Попутный ветер: «В следующем броске за прохождение на один кубик больше» ----

    [Fact]
    public void Tailwind_drawn_on_an_event_cell_gives_the_next_completion_throw_one_die_more()
    {
        var s = Season(MapBuilder.New().EventCell("e", "good").Path("start", "e", "a", "b", "c", "d", "f", "g", "h", "finish").Build());

        // One d4 = 1 stops on e, then the ticket of the one-entry deck
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 0).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(new EventDrawn(s.PlayerId("Вася"), "good", "tailwind", EventKind.Good, EventSource.Cell), Drawn(s));
        var waiting = Assert.Single(s.Player("Вася").Wallet.NextDice);
        Assert.Equal(DiceStage.Count, waiting.Stage);

        // The throw of the run that stopped there is done: tailwind waits for the next one
        Assert.Equal(("e", 1), (s.Player("Вася").CellId, s.Player("Вася").Points));
        Assert.Equal(0, s.Random.ScriptedLeft);

        // Next 3-hour game: two d4 instead of one, both into points and position
        s.Roll("Вася").Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.NextRandom(2, 3).Complete("Вася");

        var run = s.State.Runs[runId];
        Assert.Equal([4], run.Dice.Select(d => d.Sides));
        Assert.Equal([new Die(4, 3)], run.DiceMods.ExtraDice);
        Assert.Equal(["tailwind"], run.DiceMods.Sources);
        Assert.Equal((6, "f"), (s.Player("Вася").Points, s.Player("Вася").CellId));
        Assert.Empty(s.Player("Вася").Wallet.NextDice);
    }

    [Fact]
    public void Tailwind_works_for_one_throw_only()
    {
        var s = Season(MapBuilder.New().EventCell("e", "good").Path("start", "e", "a", "b", "c", "d", "f", "g", "h", "i", "j", "finish").Build());
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 0).Complete("Вася");
        s.Roll("Вася").Start("Вася").NextRandom(1, 1).Complete("Вася");

        s.Roll("Вася").Start("Вася");
        var third = s.Player("Вася").ActiveRunId!.Value;
        s.NextRandom(1).Complete("Вася");

        Assert.Single(s.State.Runs[third].Dice);
        Assert.Equal(4, s.Player("Вася").Points);
    }

    // ---- 2. Хоррор-неделя: «Следующая игра будет хоррором» ----

    [Fact]
    public void Horror_week_gives_the_drawer_a_forced_genre_and_the_next_roll_is_a_horror()
    {
        foreach (var seed in Enumerable.Range(1, 12))
        {
            var s = EventScenario.New(seed).WithMap(MapBuilder.New().EventCell("e", "bad").Path("start", "e", "a", "finish").Build())
                .WithCategory("Horror").WithCategory("Puzzle")
                .WithGame("Silent Hill", 3, "Horror").WithGame("Tetris", 3, "Puzzle").WithGame("Portal", 3, "Puzzle").WithGame("Braid", 3, "Puzzle")
                .WithPlayers("Вася", "Петя");
            s.WithContent(EventScenario.Pack(s_events, StandardDecks()));

            Orange(s, "Вася", 0);

            Assert.Equal(new EventDrawn(s.PlayerId("Вася"), "bad", "horror-week", EventKind.Bad, EventSource.Cell), Drawn(s));
            var forced = s.Held("Вася", "forced-genre");
            Assert.Equal("Horror", forced.Params!["tag"]);

            s.Roll("Вася");

            Assert.Equal("Silent Hill", s.GameTitle(s.Player("Вася").Offer!.GameId));
            Assert.DoesNotContain("forced-genre", s.Inventory("Вася"));
        }
    }

    // ---- 4. Рискнёшь?: «Кинь d6: на 1–3 назад на 3 клетки, на 4–6 вперёд на 3» ----

    /// <summary>start → a → k(checkpoint) → b → g(event, risky) → c → d → e → f → finish.</summary>
    private static MapGraph RiskyMap() =>
        MapBuilder.New().EventCell("g", "risky").Path("start", "a", "k", "b", "g", "c", "d", "e", "f", "finish").Checkpoint("k").Build();

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void Gamble_on_four_to_six_moves_three_cells_forward_without_points(int face)
    {
        var s = At(Season(RiskyMap()), "Вася", "b");

        Orange(s, "Вася", 0, face);

        Assert.Equal(new EventDrawn(s.PlayerId("Вася"), "risky", "gamble", EventKind.Risky, EventSource.Cell), Drawn(s));
        Assert.Equal("e", s.Player("Вася").CellId);
        Assert.Equal(0, s.Player("Вася").Points);
        Assert.Empty(s.LastEvents<PointsChanged>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Gamble_on_one_to_three_moves_back_but_never_past_a_checkpoint(int face)
    {
        var s = At(Season(RiskyMap()), "Вася", "b");

        Orange(s, "Вася", 0, face);

        Assert.Single(s.LastEvents<EventDrawn>());
        Assert.Equal("k", s.Player("Вася").CellId);
        Assert.Equal(0, s.Player("Вася").Points);
    }

    [Fact]
    public void Gamble_back_three_from_a_cell_after_the_start_stops_on_the_start()
    {
        var s = Season(MapBuilder.New().EventCell("g", "risky").Path("start", "g", "a", "b", "c", "finish").Build());

        Orange(s, "Вася", 0, 2);

        Assert.Single(s.LastEvents<EventDrawn>());
        Assert.Equal("start", s.Player("Вася").CellId);
    }

    [Fact]
    public void Gamble_forward_never_reaches_the_finish_it_stops_a_cell_before()
    {
        var s = At(Season(MapBuilder.New().EventCell("g", "risky").Path("start", "a", "b", "g", "x", "finish").Build()), "Вася", "b");

        Orange(s, "Вася", 0, 5);

        Assert.Single(s.LastEvents<EventDrawn>());
        Assert.Equal("x", s.Player("Вася").CellId);
        Assert.Null(s.Player("Вася").Finish);
    }

    // ---- Zone with a deck and the cell { type: event, deck: zone } (EV5, EV15) ----

    /// <summary>
    /// «Болото ужаса» of CONTENT.md (deck <c>horror-bad</c>) over z1 and z2; z2 is the doc's cell c17 renamed
    /// (<c>{ type: event, deck: zone }</c>), and o is the same cell outside the zone:
    /// start → a → z1 → z2 → b → o → c → d → e → f → finish.
    /// </summary>
    private static Scenario ZoneSeason()
    {
        var swamp = ContentJson.Parse<ZoneDefinition>(ContentExamplesTests.Example("zone"));
        var c17 = ContentJson.Parse<EquatableArray<Cell>>(ContentExamplesTests.Example("cells")).Single(c => c.Id == "c17");
        Assert.Equal(("horror-bad", DeckRef.Zone), (swamp.Deck, c17.Deck));
        var map = MapBuilder.New()
            .Cell("z2", c17.Type, _ => c17 with { Id = "z2" }).Cell("o", c17.Type, _ => c17 with { Id = "o" })
            .Path("start", "a", "z1", "z2", "b", "o", "c", "d", "e", "f", "finish").Zone(swamp, "z1", "z2").Build();
        return Season(map, decks: [.. StandardDecks(), EventScenario.Deck(EventKind.Bad, "horror-bad", "horror-week")]);
    }

    [Fact]
    public void Zone_deck_and_event_cell_draw_from_the_deck_of_the_zone_the_player_stands_in()
    {
        var s = At(ZoneSeason(), "Вася", "z1");

        Orange(s, "Вася", 0);

        Assert.Equal("z2", s.Player("Вася").CellId);
        Assert.Equal(new EventDrawn(s.PlayerId("Вася"), "horror-bad", "horror-week", EventKind.Bad, EventSource.Cell), Drawn(s));
        Assert.Contains("forced-genre", s.Inventory("Вася"));
    }

    [Fact]
    public void Zone_event_cell_outside_a_zone_draws_from_the_default_deck()
    {
        var s = At(ZoneSeason(), "Вася", "b");

        Orange(s, "Вася", 0, 4);

        Assert.Equal("o", s.LastEvents<PlayerMoved>().First().To);
        Assert.Equal(new EventDrawn(s.PlayerId("Вася"), "risky", "gamble", EventKind.Risky, EventSource.Cell), Drawn(s));
        Assert.Equal("e", s.Player("Вася").CellId);
    }

    // ---- Which stops draw ----

    [Fact]
    public void Push_by_another_players_item_onto_an_event_cell_draws_for_the_pushed_player()
    {
        // CONTENT.md item 15 «Пистолет»: the chosen other player goes one cell forward
        var s = At(Season(RiskyMap()), "Петя", "b").WithCoins("Вася", 20).WithCoins("Петя", 20);
        s.Give("Вася", "pistol");

        s.NextRandom(0, 4).Used("Вася", "pistol", "Петя", "forward");

        Assert.Equal(new EventDrawn(s.PlayerId("Петя"), "risky", "gamble", EventKind.Risky, EventSource.Cell), Drawn(s));
        Assert.Equal("e", s.Player("Петя").CellId);
        Assert.Equal("start", s.Player("Вася").CellId);
    }

    [Fact]
    public void Destination_of_a_teleport_does_not_draw()
    {
        // start → t → a → e → b → finish, t a shortcut to the event cell e
        var s = Season(MapBuilder.New().EventCell("e", "risky").Path("start", "t", "a", "e", "b", "c", "finish").Teleport("t", to: "e").Build());

        Orange(s, "Вася");

        Assert.Equal("e", s.Player("Вася").CellId);
        Assert.Equal(MoveReason.Teleport, s.LastEvents<PlayerMoved>().Last().Reason);
        Assert.Empty(s.LastEvents<EventDrawn>());
        Assert.Empty(s.LastEvents<EventDrawMissed>());
    }

    [Fact]
    public void Passing_an_event_cell_does_not_draw()
    {
        var s = Season(MapBuilder.New().EventCell("e", "risky").Path("start", "a", "e", "b", "c", "finish").Build());

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(3).Complete("Вася");

        Assert.Equal("b", s.Player("Вася").CellId);
        Assert.Empty(s.LastEvents<EventDrawn>());
    }

    [Fact]
    public void Deck_missing_from_the_content_leaves_a_manual_draw_of_its_kind()
    {
        // No «special» deck in the pack: the event is not lost (D-501)
        var s = Season(MapBuilder.New().EventCell("e", "special").Path("start", "e", "a", "finish").Build());

        Orange(s, "Вася");

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<EventDrawn>());
        Assert.Equal(new EventDrawMissed(s.PlayerId("Вася"), "special", EventKind.Special, EventSource.Cell), Assert.Single(s.LastEvents<EventDrawMissed>()));
        var manual = Assert.Single(s.LastEvents<ManualEffectCreated>());
        Assert.Equal((s.PlayerId("Вася"), (EventKind?)EventKind.Special, ManualEffectSource.Event), (manual.PlayerId, manual.DrawEvent, manual.Source));
        Assert.Contains(s.State.ManualEffects.Values, m => m.EffectId == manual.EffectId);
    }

    [Fact]
    public void Zone_deck_missing_from_the_content_leaves_a_manual_draw()
    {
        // The swamp names «horror-bad», but the pack has only the standard decks: the zone has a deck, the deck is missing,
        // so D-501 «отсутствующая колода — ручной эффект» rather than D-502 «у зоны без колоды — events.defaultDeck»
        var swamp = ContentJson.Parse<ZoneDefinition>(ContentExamplesTests.Example("zone"));
        var map = MapBuilder.New().EventCell("z", DeckRef.Zone).Path("start", "a", "z", "b", "finish").Zone(swamp, "a", "z").Build();
        var s = At(Season(map), "Вася", "a");

        Orange(s, "Вася");

        Assert.Empty(s.LastEvents<EventDrawn>());
        var missed = Assert.Single(s.LastEvents<EventDrawMissed>());
        Assert.Equal((s.PlayerId("Вася"), "horror-bad", EventSource.Cell), (missed.PlayerId, missed.Deck, missed.Source));
        Assert.Single(s.LastEvents<ManualEffectCreated>(), m => m.Source == ManualEffectSource.Event && m.DrawEvent == missed.Kind);

        // «horror-bad» is not a standard name: the kind is the default deck's (D-535)
        Assert.Equal(EventKind.Risky, missed.Kind);
    }
}

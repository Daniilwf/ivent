using GameEvent.Engine.Content;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Events;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Decks;

/// <summary>
/// Where events come from with <c>features.events</c> on (SPEC «Ивенты»: «клетки карты, дроп (плохой обязателен), платный
/// реролл, предметы, сложность выше сложной»; D-504; EV4, EV6, EV52): each source draws from the deck of its kind and plays the
/// event at once (D-500). The drop draws after its penalty move and the stop it ends on; the paid reroll draws before the new
/// roll, so what the event does to a roll goes to that roll; the difficulty draws after the throw, so its dice change waits
/// for the next throw. A deck with nothing to draw leaves a manual effect (D-501). A reject takes back what an event cell of
/// the run's own move gave (D-503, EV51). Sources of a draw (panel D-484 (2)): <c>Cell</c>, <c>Item</c>, <c>Difficulty</c>,
/// <c>Incoming</c>, <c>RerollCost</c>, <c>DropPenalty</c> (D-548).
/// </summary>
public class EventSourceTests
{
    private static Scenario DropSeason(MapGraph? map = null, params WheelDefinition[] decks) =>
        EventSetup.Season(
            EventSetup.Pack(["coins-minus-3", "coins-plus-2", "horror-week", "forced-genre"], decks.Length > 0 ? decks : [EventSetup.Deck(EventKind.Bad, "coins-minus-3")]),
            map);

    /// <summary>Вася plays Silent Hill from c8 with 10 points and 10 coins.</summary>
    private static Scenario OnC8(Scenario s)
    {
        s.WithPoints("Вася", 10).WithCoins("Вася", 10).At("Вася", "c8");
        s.PlayingFor("Вася");
        return s;
    }

    // ---- The drop: a mandatory bad event, after the penalty move (EV4, EV52) ----

    [Fact]
    public void A_drop_draws_a_bad_event()
    {
        var s = OnC8(DropSeason());

        s.Dropped("Вася", 2, 1, 0);

        var vasya = s.PlayerId("Вася");
        var drawn = Assert.Single(s.LastEvents<EventDrawn>());
        Assert.Equal(new EventDrawn(vasya, "bad", "coins-minus-3", EventKind.Bad, EventSource.DropPenalty), drawn);
        Assert.Contains(s.LastEvents<CoinsChanged>(), c => c.PlayerId == vasya && c.Delta == -3);
        Assert.Equal((7, 7, "c5"), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId));

        // Drawn and played: no manual «разыграй плохой ивент» is left
        Assert.Empty(s.LastEvents<ManualEffectCreated>());
        Assert.Empty(s.State.ManualEffects);
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
    }

    [Fact]
    public void The_bad_event_of_a_drop_comes_after_the_penalty_points_and_move()
    {
        var s = OnC8(DropSeason());

        s.Dropped("Вася", 2, 1, 0);

        var dropped = s.IndexOf(e => e is RunDropped);
        var penalty = s.IndexOf(e => e is PointsChanged { Reason: PointsReason.DropPenalty, Delta: -3 });
        var moved = s.IndexOf(e => e is PlayerMoved { From: "c8", To: "c5" });
        var drawn = s.IndexOf(e => e is EventDrawn);
        var played = s.IndexOf(e => e is CoinsChanged { Delta: -3 });
        Assert.True(dropped >= 0 && penalty > dropped && moved > dropped && drawn > penalty && drawn > moved && played > drawn, string.Join(", ", s.LastTypes()));
    }

    [Fact]
    public void The_event_cell_a_drop_stops_on_draws_before_the_bad_event_of_the_drop()
    {
        // D-504: the penalty move and the stop first, then the event of the drop; both in the one command, within 50 events
        var s = OnC8(DropSeason(EventSetup.Line(("c5", "good")), EventSetup.Deck(EventKind.Bad, "coins-minus-3"), EventSetup.Deck(EventKind.Good, "coins-plus-2")));

        s.Dropped("Вася", 2, 1, 0, 0);

        var draws = s.LastEvents<EventDrawn>().ToList();
        Assert.Equal([(EventSource.Cell, "good"), (EventSource.DropPenalty, "bad")], draws.Select(d => (d.Source, d.Deck)));
        Assert.Equal(10 + 2 - 3, s.Player("Вася").Coins);
        Assert.True(s.Last.Events.Count <= 50);
    }

    [Fact]
    public void Without_a_mandatory_event_a_drop_draws_nothing()
    {
        var s = DropSeason();
        s.WithRuleset(r => r with { Drop = r.Drop with { MandatoryEvent = MandatoryEvent.None } });
        OnC8(s);

        s.Dropped("Вася", 2, 1);

        Assert.Empty(s.LastEvents<EventDrawn>());
        Assert.Empty(s.LastEvents<EventDrawMissed>());
        Assert.Empty(s.State.ManualEffects);
        Assert.Equal(10, s.Player("Вася").Coins);
    }

    [Fact]
    public void A_drop_refused_outside_a_run_draws_nothing()
    {
        var s = DropSeason();
        s.NextRandom(2, 1, 0);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new DropRun(x.PlayerId("Вася"))), RejectionCodes.WrongPhase);
    }

    // ---- A deck with nothing to draw (EV6, D-501) ----

    [Fact]
    public void An_empty_deck_leaves_a_manual_effect()
    {
        // No «bad» deck in the pack: the bad event of the drop is not lost
        var s = OnC8(DropSeason(decks: EventSetup.Deck(EventKind.Good, "coins-plus-2")));

        s.Dropped("Вася", 2, 1);

        Assert.Empty(s.LastEvents<EventDrawn>());
        var missed = Assert.Single(s.LastEvents<EventDrawMissed>());
        Assert.Equal(new EventDrawMissed(s.PlayerId("Вася"), "bad", EventKind.Bad, EventSource.DropPenalty), missed);
        var manual = Assert.Single(s.LastEvents<ManualEffectCreated>());

        // The manual effect keeps where it came from (panel D-508: a buy-out never applies to the event of a drop, D-550)
        Assert.Equal((s.PlayerId("Вася"), (EventKind?)EventKind.Bad, ManualEffectSource.Drop), (manual.PlayerId, manual.DrawEvent, manual.Source));
        Assert.Contains(s.State.ManualEffects.Values, m => m.EffectId == manual.EffectId);
        Assert.Equal(10, s.Player("Вася").Coins);
    }

    // ---- Events off: as on stage 1 (EV35, EV52, D-10) ----

    [Fact]
    public void With_events_off_a_drop_leaves_a_manual_bad_event_as_before()
    {
        var s = Scenario.New(TestRuleset.Create().WithEconomy()).WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithPlayers("Вася");
        s.PlayingFor("Вася");

        s.Dropped("Вася", 2, 1);

        Assert.Empty(s.LastEvents<EventDrawn>());
        Assert.Empty(s.LastEvents<EventDrawMissed>());
        var manual = Assert.Single(s.LastEvents<ManualEffectCreated>());
        Assert.Equal(((EventKind?)EventKind.Bad, ManualEffectSource.Drop, (string?)null), (manual.DrawEvent, manual.Source, manual.ObjectId));
    }

    [Fact]
    public void Events_switched_off_mid_season_make_the_drop_manual_again()
    {
        var s = DropSeason();
        s.WithRuleset(r => r with { Features = r.Features with { Events = false } });
        OnC8(s);

        s.Dropped("Вася", 2, 1);

        Assert.Empty(s.LastEvents<EventDrawn>());
        Assert.Equal(ManualEffectSource.Drop, Assert.Single(s.LastEvents<ManualEffectCreated>()).Source);
        Assert.Equal(10, s.Player("Вася").Coins);
    }

    [Fact]
    public void A_manual_event_made_before_events_were_switched_on_stays_manual()
    {
        // D-504: no draw after the fact for «разыграй ивент» created while events were off
        var s = Scenario.New(TestRuleset.Create().WithEconomy()).WithMap(EventSetup.Line())
            .WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithGame("Fatal Frame", 3, "Horror").WithPlayers("Вася");
        s.PlayingFor("Вася");
        s.Dropped("Вася", 2, 1);
        var pending = Assert.Single(s.State.ManualEffects).Key;
        var logBefore = s.Log.Count;

        s.WithRuleset(r => r.WithEvents());
        s.WithContent(EventSetup.Pack(["coins-minus-3"], EventSetup.Deck(EventKind.Bad, "coins-minus-3")));
        s.Roll("Вася").Start("Вася");

        Assert.True(s.State.ManualEffects.ContainsKey(pending));
        Assert.DoesNotContain(s.Log.Skip(logBefore), e => e is EventDrawn or EventDrawMissed);
        Assert.Equal(0, s.Player("Вася").Coins);
    }

    [Fact]
    public void With_events_off_a_completion_above_hard_leaves_a_manual_good_event()
    {
        var s = Scenario.New(TestRuleset.Create().WithEconomy()).WithCategory("Horror").WithGame("Alan Wake", 6, "Horror").WithPlayers("Вася");
        s.RollTitle("Вася", "Alan Wake").Start("Вася").NextRandom(2, 3).Complete("Вася", Difficulty.Extreme);

        Assert.Empty(s.LastEvents<EventDrawn>());
        var manual = Assert.Single(s.LastEvents<ManualEffectCreated>());
        Assert.Equal(((EventKind?)EventKind.Good, ManualEffectSource.Difficulty), (manual.DrawEvent, manual.Source));
    }

    // ---- The paid reroll for a bad event: drawn before the new roll (EV4) ----

    private static Ruleset BadEventReroll(Ruleset r) =>
        r with { Roll = r.Roll with { FreeRerollsPerRoll = 1, RerollCost = new RerollCost { Kind = RerollCostKind.BadEvent } } };

    /// <summary>Three Horror and three Puzzle games; the bad deck holds «Хоррор-неделя».</summary>
    private static Scenario RerollSeason(int seed)
    {
        var s = Scenario.New(BadEventReroll(TestRuleset.Create().WithEvents()), seed).WithMap(EventSetup.Line())
            .WithCategory("Horror").WithCategory("Puzzle")
            .WithGame("Silent Hill", 3, "Horror").WithGame("Fatal Frame", 3, "Horror").WithGame("Siren", 3, "Horror")
            .WithGame("Tetris", 3, "Puzzle").WithGame("Portal", 3, "Puzzle").WithGame("Braid", 3, "Puzzle")
            .WithPlayers("Вася", "Петя");
        return s.WithContent(EventSetup.Pack(["horror-week", "forced-genre"], EventSetup.Deck(EventKind.Bad, "horror-week")));
    }

    [Fact]
    public void A_paid_reroll_draws_its_bad_event_before_the_new_roll_and_the_roll_gets_it()
    {
        foreach (var seed in Enumerable.Range(1, 10))
        {
            var s = RerollSeason(seed);
            s.Roll("Вася");
            s.Act(new Reroll(s.PlayerId("Вася")));
            ScenarioAssert.Accepted(s);

            s.Act(new Reroll(s.PlayerId("Вася")));

            ScenarioAssert.Accepted(s);
            var vasya = s.PlayerId("Вася");
            Assert.Equal(RerollPayment.BadEvent, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
            var drawn = Assert.Single(s.LastEvents<EventDrawn>());
            Assert.Equal(new EventDrawn(vasya, "bad", "horror-week", EventKind.Bad, EventSource.RerollCost), drawn);
            Assert.True(s.IndexOf(e => e is EventDrawn) < s.IndexOf(e => e is GameRolled), string.Join(", ", s.LastTypes()));

            // «Хоррор-неделя» went to this roll, as an item «перед роллом»: a horror, and the forced genre is spent
            var offered = s.Player("Вася").Offer!.GameId;
            Assert.Contains("Horror", s.PoolGame(offered).Tags);
            Assert.DoesNotContain("forced-genre", s.Inventory("Вася"));
            Assert.Empty(s.LastEvents<ManualEffectCreated>());
            Assert.Empty(s.State.ManualEffects);
        }
    }

    [Fact]
    public void The_free_reroll_draws_no_bad_event()
    {
        var s = RerollSeason(7);
        s.Roll("Вася");

        s.Act(new Reroll(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<EventDrawn>());
        Assert.Empty(s.State.ManualEffects);
    }

    [Fact]
    public void A_paid_reroll_with_nothing_else_to_roll_is_refused_and_draws_nothing()
    {
        // D-93: nothing else to roll is checked before the payment, and the bad event is the payment
        var s = Scenario.New(BadEventReroll(TestRuleset.Create().WithEvents())).WithMap(EventSetup.Line())
            .WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithGame("Fatal Frame", 3, "Horror").WithPlayers("Вася", "Петя");
        s.WithContent(EventSetup.Pack(["horror-week", "forced-genre"], EventSetup.Deck(EventKind.Bad, "horror-week")));
        s.Roll("Вася");
        s.Act(new Reroll(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);
        s.Roll("Петя");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new Reroll(x.PlayerId("Вася"))), RejectionCodes.NoAvailableGames);
    }

    // ---- The difficulty above hard: a good event after the throw (EV4) ----

    [Fact]
    public void A_completion_above_hard_draws_a_good_event_for_the_next_throw()
    {
        var s = EventSetup.Season(EventSetup.Pack(["tailwind"], EventSetup.Deck(EventKind.Good, "tailwind")));
        s.RollTitle("Вася", "Alan Wake").Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;

        s.NextRandom(2, 3, 0).Complete("Вася", Difficulty.Extreme);

        var vasya = s.PlayerId("Вася");
        Assert.Equal(new EventDrawn(vasya, "good", "tailwind", EventKind.Good, EventSource.Difficulty) { RunId = runId }, Assert.Single(s.LastEvents<EventDrawn>()));
        Assert.True(s.IndexOf(e => e is CompletionRolled) < s.IndexOf(e => e is EventDrawn), string.Join(", ", s.LastTypes()));

        // The throw of this run is done: two d6 and nothing more; the extra die waits for the next throw
        Assert.Equal([new Die(6, 2), new Die(6, 3)], s.State.Runs[runId].Dice);
        Assert.Empty(s.State.Runs[runId].DiceMods.ExtraDice);
        Assert.Equal((5, "c5"), (s.Player("Вася").Points, s.Player("Вася").CellId));
        Assert.Single(s.Player("Вася").Wallet.NextDice);
        Assert.Empty(s.State.ManualEffects);

        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        var next = s.Player("Вася").ActiveRunId!.Value;
        s.NextRandom(1, 1).Complete("Вася");
        Assert.Single(s.State.Runs[next].DiceMods.ExtraDice);
    }

    [Theory]
    [InlineData(Difficulty.Easy)]
    [InlineData(Difficulty.Normal)]
    [InlineData(Difficulty.Hard)]
    public void A_completion_up_to_hard_draws_nothing(Difficulty difficulty)
    {
        var s = EventSetup.Season(EventSetup.Pack(["tailwind"], EventSetup.Deck(EventKind.Good, "tailwind")));
        s.RollTitle("Вася", "Alan Wake").Start("Вася");

        s.NextRandom(1, 1).Complete("Вася", difficulty);

        Assert.Empty(s.LastEvents<EventDrawn>());
        Assert.Empty(s.LastEvents<EventDrawMissed>());
        Assert.Empty(s.State.ManualEffects);
    }

    // ---- The difficulty's event after the stop of the run's own move (D-553 (2), D-557) ----

    [Fact]
    public void A_completion_above_hard_onto_an_event_cell_draws_the_cell_first_and_then_the_difficulty()
    {
        var s = EventSetup.Season(
            EventSetup.Pack(["coins-minus-3", "tailwind"], EventSetup.Deck(EventKind.Bad, "coins-minus-3"), EventSetup.Deck(EventKind.Good, "tailwind")),
            EventSetup.Line(("c5", "bad")));
        s.RollTitle("Вася", "Alan Wake").Start("Вася");

        s.NextRandom(2, 3, 0, 0).Complete("Вася", Difficulty.Extreme);

        Assert.Equal([(EventSource.Cell, "bad"), (EventSource.Difficulty, "good")], s.LastEvents<EventDrawn>().Select(d => (d.Source, d.Deck)));
        Assert.True(s.IndexOf(e => e is PlayerMoved { To: "c5" }) < s.IndexOf(e => e is EventDrawn), string.Join(", ", s.LastTypes()));
    }

    [Fact]
    public void The_push_of_the_difficulty_event_does_not_change_the_cell_the_run_stopped_on()
    {
        // c5 draws «the zone's deck» outside any zone: the default deck, bad. c6 and c7 are a swamp whose deck is missing from
        // the content. The cell draws where the run stopped, before the difficulty's event pushes him on to c7
        var swamp = new ZoneDefinition { Id = "swamp", Name = "Болото", Deck = "horror-bad" };
        var map = MapBuilder.New().EventCell("c5", DeckRef.Zone)
            .Path(["start", .. Enumerable.Range(1, 12).Select(i => $"c{i}"), "finish"]).Zone(swamp, "c6", "c7").Build();
        var s = EventSetup.Season(
            EventSetup.Pack(["coins-minus-3", "hop-and-coins"], EventSetup.Deck(EventKind.Bad, "coins-minus-3"), EventSetup.Deck(EventKind.Good, "hop-and-coins")),
            map,
            r => r with { Events = new EventRules { DefaultDeck = EventKind.Bad, IrlProof = new IrlProofRules { MaxFiles = 5 } } });
        s.RollTitle("Вася", "Alan Wake").Start("Вася");

        s.NextRandom(2, 3, 0, 0).Complete("Вася", Difficulty.Extreme);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [(EventSource.Cell, "bad", "coins-minus-3"), (EventSource.Difficulty, "good", "hop-and-coins")],
            s.LastEvents<EventDrawn>().Select(d => (d.Source, d.Deck, d.ObjectId)));
        Assert.Empty(s.LastEvents<EventDrawMissed>());
        Assert.Empty(s.State.ManualEffects);
        Assert.Equal("c7", s.Player("Вася").CellId);
    }

    [Fact]
    public void The_difficulty_event_of_a_walk_paused_at_a_fork_waits_for_the_branch_and_its_push_is_not_lost()
    {
        // start → a → f → b1 → j → k → l → m → finish, f → c1 → j. 1 + 2 from the start: a, f and one step left at the fork.
        // The difficulty's event follows the stop (D-553 (2)): after the branch, so its push of 2 moves him on (D-546 (7))
        var map = MapBuilder.New().Path("start", "a", "f", "b1", "j", "k", "l", "m", "finish").Path("f", "c1", "j").Build();
        var s = EventSetup.Season(EventSetup.Pack(["hop-and-coins"], EventSetup.Deck(EventKind.Good, "hop-and-coins")), map);
        s.RollTitle("Вася", "Alan Wake").Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;

        s.NextRandom(1, 2).Complete("Вася", Difficulty.Extreme);

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<BranchChoiceRequested>());
        Assert.Empty(s.LastEvents<EventDrawn>());
        var coins = s.Player("Вася").Coins;

        s.NextRandom(0).ChooseBranch("Вася", "c1");

        ScenarioAssert.Accepted(s);
        var drawn = Assert.Single(s.LastEvents<EventDrawn>());
        Assert.Equal((EventSource.Difficulty, "hop-and-coins", (Guid?)runId), (drawn.Source, drawn.ObjectId, drawn.RunId));
        Assert.True(s.IndexOf(e => e is PlayerMoved { To: "c1" }) < s.IndexOf(e => e is EventDrawn), string.Join(", ", s.LastTypes()));
        Assert.Equal(("k", coins + 3), (s.Player("Вася").CellId, s.Player("Вася").Coins));

        // Drawn once: the run's difficulty event is not drawn again by a later change of the difficulty
        s.Act(new ChangeRunDifficulty(runId, Difficulty.Hard, "Ошибся"));
        ScenarioAssert.Accepted(s);
        s.NextRandom(0).Act(new ChangeRunDifficulty(runId, Difficulty.Extreme, "Всё-таки выше сложной"));
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<EventDrawn>());
    }

    // ---- The admin changes the difficulty (D-553 (2), D-557) ----

    private static Scenario TailwindSeason() => EventSetup.Season(EventSetup.Pack(["tailwind"], EventSetup.Deck(EventKind.Good, "tailwind")));

    [Fact]
    public void Raising_the_difficulty_above_hard_draws_its_good_event_once()
    {
        var s = TailwindSeason();
        s.RollTitle("Вася", "Alan Wake").Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.NextRandom(2, 3).Complete("Вася", Difficulty.Hard);
        Assert.Empty(s.LastEvents<EventDrawn>());

        s.NextRandom(0).Act(new ChangeRunDifficulty(runId, Difficulty.Extreme, "Это было выше сложной"));

        ScenarioAssert.Accepted(s);
        var drawn = Assert.Single(s.LastEvents<EventDrawn>());
        Assert.Equal((EventSource.Difficulty, "tailwind", (Guid?)runId), (drawn.Source, drawn.ObjectId, drawn.RunId));
        Assert.Single(s.Player("Вася").Wallet.NextDice);
        Assert.Empty(s.State.ManualEffects);
    }

    [Fact]
    public void Lowering_and_raising_the_difficulty_again_draws_no_second_event()
    {
        // Н-4 (whether a lowering takes the drawn event back) waits for the owner; a run never draws its difficulty's event twice
        var s = TailwindSeason();
        s.RollTitle("Вася", "Alan Wake").Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.NextRandom(2, 3, 0).Complete("Вася", Difficulty.Extreme);
        Assert.Single(s.LastEvents<EventDrawn>());

        s.Act(new ChangeRunDifficulty(runId, Difficulty.Hard, "Ошибся"));
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<EventDrawn>());

        s.NextRandom(0).Act(new ChangeRunDifficulty(runId, Difficulty.Extreme, "Всё-таки выше сложной"));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<EventDrawn>());
        Assert.Empty(s.LastEvents<EventDrawMissed>());
        Assert.Empty(s.State.ManualEffects);
        Assert.Single(s.Player("Вася").Wallet.NextDice);
    }

    // ---- The other ways to a drop penalty: a tech reroll turned into a drop, a reject with the drop penalty (D-553 (1)) ----

    private static Scenario PenaltySeason() =>
        EventSetup.Season(
            EventSetup.Pack(["coins-minus-3", "coins-plus-2", "babl", "bubble-effect"], EventSetup.Deck(EventKind.Bad, "coins-minus-3"), EventSetup.Deck(EventKind.Good, "coins-plus-2")),
            EventSetup.Line(("c5", "good")));

    [Fact]
    public void A_tech_reroll_turned_into_a_drop_draws_the_bad_event_of_the_drop_after_the_penalty_move()
    {
        var s = PenaltySeason();
        var bubble = s.Bubbled("Вася");
        var runId = OnC8Run(s);
        s.Act(new TechReroll(s.PlayerId("Вася"), TechRerollReason.DoesNotLaunch, null));
        ScenarioAssert.Accepted(s);

        s.NextRandom(2, 1, 0, 0).Act(new ConvertTechRerollToDrop(runId, "это был дроп"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([(EventSource.Cell, "good"), (EventSource.DropPenalty, "bad")], s.LastEvents<EventDrawn>().Select(d => (d.Source, d.Deck)));
        Assert.True(s.IndexOf(e => e is PlayerMoved { Reason: MoveReason.DropPenalty, To: "c5" }) < s.IndexOf(e => e is EventDrawn), string.Join(", ", s.LastTypes()));
        Assert.Empty(s.LastEvents<BadEventIntercepted>());
        Assert.Contains(s.Player("Вася").Wallet.Inventory, o => o.InstanceId == bubble);
        Assert.Equal((7, 10 + 2 - 3, "c5"), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId));
    }

    [Fact]
    public void A_reject_with_the_drop_penalty_draws_the_bad_event_of_the_drop_on_what_the_reject_left()
    {
        // Вася completes from c8 with a 2 (c10, 12 points); the reject takes the run back (c8, 10 points), the penalty of
        // 2 + 1 pushes him to c5, an event cell, then the drop's bad event: the Бабл never stops a penalty
        var s = PenaltySeason();
        var bubble = s.Bubbled("Вася");
        var runId = OnC8Run(s);
        s.NextRandom(2).Complete("Вася");
        Assert.Equal("c10", s.Player("Вася").CellId);

        s.NextRandom(2, 1, 0, 0).Act(new RejectProofWithDropPenalty(runId, "Обман"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([(EventSource.Cell, "good"), (EventSource.DropPenalty, "bad")], s.LastEvents<EventDrawn>().Select(d => (d.Source, d.Deck)));
        Assert.True(s.IndexOf(e => e is ProofRejectPenalized) < s.IndexOf(e => e is EventDrawn), string.Join(", ", s.LastTypes()));
        Assert.True(s.IndexOf(e => e is PlayerMoved { Reason: MoveReason.DropPenalty, From: "c8", To: "c5" }) < s.IndexOf(e => e is EventDrawn { Source: EventSource.DropPenalty }));
        Assert.Empty(s.LastEvents<BadEventIntercepted>());
        Assert.Contains(s.Player("Вася").Wallet.Inventory, o => o.InstanceId == bubble);
        Assert.Equal((7, "c5"), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    /// <summary>Вася on c8 with 10 points and 10 coins plays Silent Hill past the hour a drop needs; returns the run.</summary>
    private static Guid OnC8Run(Scenario s)
    {
        s.WithPoints("Вася", 10).WithCoins("Вася", 10).At("Вася", "c8");
        return s.PlayingFor("Вася");
    }

    // ---- The paid reroll keeps what waited for the next roll (D-556) ----

    [Fact]
    public void Roll_changes_waiting_for_the_next_roll_keep_waiting_through_a_paid_reroll()
    {
        // After his roll Вася draws «Клад» (pick of three for the next roll: it waits, D-405); then he pays a reroll with
        // «Хоррор-неделя»: the forced genre goes to this roll, «Клад» still waits and reaches the roll after
        var s = Scenario.New(BadEventReroll(TestRuleset.Create().WithEvents()), 3).WithMap(EventSetup.Line())
            .WithCategory("Horror").WithCategory("Puzzle")
            .WithGame("Silent Hill", 3, "Horror").WithGame("Fatal Frame", 3, "Horror").WithGame("Siren", 3, "Horror").WithGame("Kuon", 3, "Horror")
            .WithGame("Tetris", 3, "Puzzle").WithGame("Portal", 3, "Puzzle").WithGame("Braid", 3, "Puzzle").WithGame("Limbo", 3, "Puzzle")
            .WithPlayers("Вася", "Петя");
        s.WithContent(EventSetup.Pack(
            ["horror-week", "forced-genre", "bounty", "shield", "shield-effect", "sack-good"],
            EventSetup.Deck(EventKind.Bad, "horror-week"),
            EventSetup.Deck(EventKind.Good, "bounty")));
        s.Roll("Вася");
        s.Act(new Reroll(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);
        s.Give("Вася", "sack-good");
        s.Used("Вася", "sack-good");
        Assert.Single(s.Player("Вася").Wallet.NextRoll, m => m.ObjectId == "bounty");

        s.Act(new Reroll(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        var reroll = s.LastCommandId;
        var applied = Assert.Single(s.LastEvents<RollModifiersApplied>());
        Assert.Contains(applied.Modifiers, m => m.ObjectId == "forced-genre");
        Assert.DoesNotContain(applied.Modifiers, m => m.ObjectId == "bounty");
        Assert.Single(applied.Waiting, m => m.ObjectId == "bounty");
        Assert.Contains("Horror", s.PoolGame(s.Player("Вася").Offer!.GameId).Tags);
        Assert.Single(s.Player("Вася").Wallet.NextRoll, m => m.ObjectId == "bounty");
        Assert.DoesNotContain(s.Player("Вася").Wallet.CurrentRoll, m => m.ObjectId == "bounty");

        // The undo of the reroll gives back the roll before it with «Клад» still waiting
        var before = s.State;
        s.Act(new UndoCommand(reroll, "Реролл по ошибке"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(SeasonEngine.Replay(s.EffectiveLog).Players, s.State.Players);
        Assert.Single(s.Player("Вася").Wallet.NextRoll, m => m.ObjectId == "bounty");
        Assert.NotEqual(before.Players[s.PlayerId("Вася")].Offer, s.Player("Вася").Offer);

        // Rolled again and played: the next roll gets «Клад»
        s.Act(new Reroll(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);
        s.Start("Вася").NextRandom(1).Complete("Вася");
        s.Roll("Вася");
        Assert.Contains(s.LastEvents<RollModifiersApplied>(), a => a.Modifiers.Any(m => m.ObjectId == "bounty"));
        Assert.Empty(s.Player("Вася").Wallet.NextRoll);
    }

    // ---- drawEvent of an item: the deck of its kind (EV1, EV4) ----

    [Fact]
    public void The_draw_event_of_an_item_draws_from_the_special_deck()
    {
        var s = EventSetup.Season(EventSetup.Pack(["sack-special", "coin-gift"], EventSetup.Deck(EventKind.Special, "coin-gift")));
        var sack = s.Give("Вася", "sack-special");

        s.NextRandom(0).Use("Вася", sack);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new EventDrawn(s.PlayerId("Вася"), "special", "coin-gift", EventKind.Special, EventSource.Item), Assert.Single(s.LastEvents<EventDrawn>()));
        Assert.Equal(1, s.Player("Вася").Coins);
        Assert.DoesNotContain("sack-special", s.Inventory("Вася"));
    }

    // ---- What a draw records (open item of 5.1): who caused it, whether it was hostile ----

    [Fact]
    public void An_own_draw_has_no_other_player_and_is_not_hostile()
    {
        var s = EventSetup.Season(EventSetup.Pack(["sack-bad", "coins-minus-3"], EventSetup.Deck(EventKind.Bad, "coins-minus-3")));
        s.Give("Вася", "sack-bad");

        s.Used("Вася", "sack-bad");

        var drawn = Assert.Single(s.LastEvents<EventDrawn>());
        Assert.Equal(((Guid?)null, false), (drawn.FromPlayerId, drawn.Hostile));
    }

    [Fact]
    public void A_draw_by_another_players_hostile_item_records_him_and_is_hostile()
    {
        // D-504: «Ивенты не враждебны, кроме вытянутых чужим враждебным предметом на тебя»; the target draws and plays it
        var s = EventSetup.Season(EventSetup.Pack(["jinx", "coins-minus-3"], EventSetup.Deck(EventKind.Bad, "coins-minus-3"))).WithCoins("Петя", 10);
        s.Give("Вася", "jinx");

        s.Used("Вася", "jinx", target: "Петя");

        var drawn = Assert.Single(s.LastEvents<EventDrawn>());
        Assert.Equal((s.PlayerId("Петя"), "bad", "coins-minus-3", EventKind.Bad, EventSource.Incoming), (drawn.PlayerId, drawn.Deck, drawn.ObjectId, drawn.Kind, drawn.Source));
        Assert.Equal(((Guid?)s.PlayerId("Вася"), true), (drawn.FromPlayerId, drawn.Hostile));
        Assert.Equal((7, 0), (s.Player("Петя").Coins, s.Player("Вася").Coins));
        Assert.Equal(1, s.Player("Петя").Wallet.HostileReceived);
    }

    // ---- The reject and the event cell of the run's own move (EV51) ----
    // SPEC «Награда за прохождение»: «Реджект: … снимаются очки и клетки, полученные за это прохождение. Остальные
    // последствия (предметы, ивенты клеток) остаются». D-503 would take back what the event of a cell of the run's own move
    // gave; until the owner confirms it through /spec-update the SPEC stands (D-538, D-552). If D-503 is confirmed, these
    // tests turn into «taken back» first.

    /// <summary>start → e (event, good: «Клад») → a → b → … → finish; Вася plays Silent Hill (one d4).</summary>
    private static Scenario BountySeason(string goodEvent = "bounty")
    {
        var map = MapBuilder.New().EventCell("e", "good").Path("start", "e", "a", "b", "c", "d", "f", "g", "finish").Build();
        return EventSetup.Season(EventSetup.Pack([goodEvent, "shield", "shield-effect", "pistol"], EventSetup.Deck(EventKind.Good, goodEvent)), map);
    }

    [Fact]
    public void A_reject_leaves_what_the_cell_event_gave()
    {
        var s = BountySeason();
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.NextRandom(1, 0).Complete("Вася");
        Assert.Equal(EventSource.Cell, Assert.Single(s.LastEvents<EventDrawn>()).Source);
        Assert.Equal((1 + 2, 3 + 5), (s.Player("Вася").Points, s.Player("Вася").Coins));

        s.Act(new RejectProof(runId, "На скрине другая игра"));

        // The points and cells of the run go (the die of 1, the step onto e), its coins too (D-421); the event's stay
        ScenarioAssert.Accepted(s);
        Assert.DoesNotContain(s.LastEvents<ObjectRemoved>(), r => r.ObjectId == "shield");
        Assert.Contains("shield", s.Inventory("Вася"));
        Assert.Single(s.Player("Вася").Wallet.NextRoll);
        Assert.Equal((2, 5, "start"), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId));
    }

    [Fact]
    public void A_reject_does_not_give_back_the_bad_part_and_leaves_the_others_alone()
    {
        // «Пошлина»: −3 to the drawer, −2 to the leader Петя
        var s = BountySeason("toll").WithCoins("Вася", 10).WithCoins("Петя", 10).WithPoints("Петя", 10);
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.NextRandom(1, 0).Complete("Вася");
        Assert.Equal((10 + 3 - 3, 8), (s.Player("Вася").Coins, s.Player("Петя").Coins));

        s.Act(new RejectProof(runId, "На скрине другая игра"));

        ScenarioAssert.Accepted(s);

        // The coins of the run go, the −3 is not returned; Петя keeps his −2
        Assert.Equal((10 - 3, 8), (s.Player("Вася").Coins, s.Player("Петя").Coins));
    }

    [Fact]
    public void An_event_cell_reached_by_another_players_push_is_not_taken_back_by_a_reject()
    {
        // Вася's run of one d4 = 2 reaches a; then Петя's «Пистолет» pushes him back onto e (−2 coins, Вася is rich)
        var s = BountySeason().WithCoins("Вася", 20);
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.NextRandom(2).Complete("Вася");
        Assert.Equal("a", s.Player("Вася").CellId);
        s.Give("Петя", "pistol");
        s.NextRandom(0).Used("Петя", "pistol", "Вася", "back");
        Assert.Equal(EventSource.Cell, Assert.Single(s.LastEvents<EventDrawn>()).Source);

        s.Act(new RejectProof(runId, "На скрине другая игра"));

        ScenarioAssert.Accepted(s);
        Assert.Contains("shield", s.Inventory("Вася"));
        Assert.Single(s.Player("Вася").Wallet.NextRoll);

        // The run's die and coins go; the +2 points and +5 coins of the pushed stop stay
        Assert.Equal((2, 20 + 5 - 2), (s.Player("Вася").Points, s.Player("Вася").Coins));
    }

    // ---- What an event gives its drawer is not hostile (EV55, D-544); drawn onto you by a hostile item it is (D-504) ----

    [Fact]
    public void An_object_an_event_gives_its_drawer_is_not_hostile()
    {
        // «Хоррор-неделя» gives «Навязанный жанр» (hostile: true): from the system, so the Shield lets it through
        var s = EventSetup.Season(EventSetup.Pack(["horror-week", "forced-genre", "shield", "shield-effect", "sack-bad"], EventSetup.Deck(EventKind.Bad, "horror-week")));
        s.Give("Вася", "shield");
        s.Used("Вася", "shield");
        s.Give("Вася", "sack-bad");

        s.NextRandom(0).Used("Вася", "sack-bad");

        Assert.Empty(s.LastEvents<HostileIntercepted>());
        Assert.Contains("forced-genre", s.Inventory("Вася"));
        Assert.Contains("shield-effect", s.Inventory("Вася"));
        Assert.Equal(0, s.Player("Вася").Wallet.HostileReceived);
    }

    [Fact]
    public void An_event_drawn_onto_a_player_by_a_hostile_item_meets_his_shield()
    {
        var s = EventSetup.Season(EventSetup.Pack(["horror-week", "forced-genre", "shield", "shield-effect", "jinx"], EventSetup.Deck(EventKind.Bad, "horror-week")));
        s.Give("Петя", "shield");
        s.Used("Петя", "shield");
        s.Give("Вася", "jinx");

        s.NextRandom(0).Used("Вася", "jinx", target: "Петя");

        Assert.Single(s.LastEvents<HostileIntercepted>());
        Assert.DoesNotContain("forced-genre", s.Inventory("Петя"));
        Assert.DoesNotContain("shield-effect", s.Inventory("Петя"));
    }

    // ---- Undo and replay (EV38, EV49, invariants 13 and 14) ----

    [Fact]
    public void Undoing_a_drop_with_its_event_restores_everything()
    {
        var s = OnC8(DropSeason());
        s.Dropped("Вася", 2, 1, 0);
        var drop = s.LastCommandId;

        s.Act(new UndoCommand(drop, "Дроп по ошибке"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(SeasonEngine.Replay(s.EffectiveLog).Players, s.State.Players);
        Assert.Equal((10, 10, "c8", TurnPhase.Playing), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId, s.Player("Вася").Phase));
    }

    [Fact]
    public void Undoing_a_drop_waits_for_the_roll_its_event_went_to()
    {
        var s = OnC8(DropSeason(decks: EventSetup.Deck(EventKind.Bad, "horror-week")));
        s.Dropped("Вася", 2, 1, 0);
        var drop = s.LastCommandId;
        Assert.Contains("forced-genre", s.Inventory("Вася"));
        s.Roll("Вася");
        var roll = s.LastCommandId;
        var before = s.State;

        s.Act(new UndoCommand(drop, "Дроп по ошибке"));

        Assert.False(s.Last.IsAccepted);
        Assert.Equal(RejectionCodes.UndoDependents, s.Last.Rejection!.Code);
        Assert.Contains(roll, s.Last.Rejection.Related);
        Assert.Equal(before, s.State);
    }

    [Fact]
    public void A_replay_takes_what_was_drawn_from_the_log_even_after_the_decks_change()
    {
        var s = EventSetup.Season(EventSetup.Pack(["coins-minus-3", "horror-week", "forced-genre", "tailwind"], EventSetup.Deck(EventKind.Bad, "coins-minus-3"), EventSetup.Deck(EventKind.Good, "tailwind")));
        OnC8(s);
        s.Dropped("Вася", 2, 1, 0);
        s.RollTitle("Вася", "Alan Wake").Start("Вася").NextRandom(2, 3, 0).Complete("Вася", Difficulty.Extreme);

        // Another version of the decks: other events and weights
        s.WithContent(EventSetup.Pack(
            ["coins-minus-3", "horror-week", "forced-genre", "tailwind"],
            EventScenario.Deck(EventKind.Bad, null, ("horror-week", 5), ("coins-minus-3", 1)),
            EventScenario.Deck(EventKind.Good, null, ("tailwind", 0), ("horror-week", 1))));

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
        Assert.Equal(["coins-minus-3", "tailwind"], s.Log.OfType<EventDrawn>().Select(d => d.ObjectId));
    }

    // ---- After the deadline (EV37) ----

    [Fact]
    public void A_drop_after_the_deadline_is_refused_and_draws_nothing()
    {
        var s = OnC8(DropSeason());
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        ScenarioAssert.Accepted(s);
        s.NextRandom(2, 1, 0);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new DropRun(x.PlayerId("Вася"))), RejectionCodes.SeasonNotActive);
    }
}

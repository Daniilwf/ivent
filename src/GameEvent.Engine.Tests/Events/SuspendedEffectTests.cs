using GameEvent.Engine.Content;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// An event that needs an answer waits for it on the server (D-511, EV13, task 5.7): an item answers before its use
/// (D-407), an event cannot — it is drawn on another player's push, on a cell, on a drop penalty. When an event asks
/// (<c>requestChoice</c>, target <c>chosen</c>, <c>pick: chosen</c>, <c>outcomes.choice</c>) its effect is suspended with
/// <see cref="EffectSuspended"/> (the object, the next action, the references computed so far, the options the server
/// counted) and the player has a pending choice of <see cref="ChoiceKind.Effect"/> (the general mechanism, D-91). The answer
/// is the common <see cref="MakeChoice"/>; <see cref="EffectResumed"/> goes on in that new command with a new chain budget
/// and at the level it stopped at. Several suspended effects ask one by one in the order of D-505.
/// <para>
/// Written by <c>test-author</c> from the SPEC before the implementation. The contract these tests assume (D-563): the
/// events <c>effect-suspended</c> <c>EffectSuspended(PlayerId, ChoiceId, ObjectId, …, NextAction, Depth, Options)</c> and
/// <c>effect-resumed</c> <c>EffectResumed(PlayerId, ChoiceId, OptionId)</c>; an option's id is the text of a list or a
/// category, a player's or an object instance's id in the <c>N</c> format; refusals reuse the codes of the general choice.
/// Defaults of the open questions: Н-10…Н-13 of the task's scenarios, the timeout D-560, the finish D-561, the order D-562,
/// the flag D-564.
/// </para>
/// </summary>
public partial class SuspendedEffectTests
{
    // ---- Content: events that ask, the items that bring the player to them ----

    private static ObjectDefinition Event(string id, string name, EffectSpec effect) =>
        new() { Id = id, Kind = ObjectKind.Event, Name = name, Description = name, Rarity = Rarity.Common, Effect = effect };

    private static readonly TargetSpec s_self = new() { Selector = TargetSelector.Self };

    private static ChangeResourceAction Coins(int amount) => new() { Resource = "coins", Amount = ContentValue.Of(amount) };

    private static ChangeResourceAction Coins(string reference) => new() { Resource = "coins", Amount = ContentValue.TryParse(reference)! };

    private static MoveAction Move(int steps) => new() { Steps = ContentValue.Of(steps) };

    /// <summary>A branch by the player's answer (like «Кубик сахара»): three coins or a step forward.</summary>
    private static readonly ObjectDefinition s_crossroads = Event("crossroads", "Перекрёсток", new EffectSpec
    {
        Target = s_self,
        Outcomes = new OutcomesSpec
        {
            Choice = new OutcomeChoice { Prompt = "Что берёшь?", Options = new ChoiceOptionsSpec { List = ["монетки", "шаг"] } },
            Cases =
            [
                new OutcomeCase { Option = "монетки", Actions = [Coins(3)] },
                new OutcomeCase { Option = "шаг", Actions = [Move(1)] },
            ],
        },
    });

    /// <summary>
    /// An action before the question, a roll before it, the question (from the categories), then the actions that use both:
    /// <c>$roll</c> computed before the question and <c>$choice</c> of the answer.
    /// </summary>
    private static readonly ObjectDefinition s_genreGift = Event("genre-gift", "Подарок жанра", new EffectSpec
    {
        Target = s_self,
        Actions =
        [
            Coins(1),
            new RollAction { Dice = ContentValue.TryParse("1d4")! },
            new RequestChoiceAction { Prompt = "Какой жанр следующим?", Options = new ChoiceOptionsSpec { From = ChoiceSource.Categories } },
            Coins("$roll"),
            new GiveObjectAction { ObjectId = "forced-genre", Params = new ContentParamDictionary([new("tag", "$choice")]) },
        ],
    });

    /// <summary>A bad event on a player the drawer picks: the target <c>chosen</c> of an event is a question (D-511).</summary>
    private static readonly ObjectDefinition s_hex = Event("hex", "Сглаз", new EffectSpec
    {
        Target = new TargetSpec { Selector = TargetSelector.Chosen, ExcludeSelf = true },
        Actions = [Coins(-2)],
    });

    /// <summary>Destroy one of your own objects of your choice: <c>pick: chosen</c> of an event is a question.</summary>
    private static readonly ObjectDefinition s_sacrifice = Event("sacrifice", "Жертва", new EffectSpec
    {
        Target = s_self,
        Actions = [new TakeObjectAction { Mode = TakeMode.Destroy, Pick = Pick.Chosen, Target = ActionTarget.Self }],
    });

    /// <summary>Asks forward or back — the push of the answer lands on the next event cell (the level of the continuation).</summary>
    private static readonly ObjectDefinition s_leap = Event("leap", "Прыжок", new EffectSpec
    {
        Target = s_self,
        Outcomes = new OutcomesSpec
        {
            Choice = new OutcomeChoice { Prompt = "Прыгнешь?", Options = new ChoiceOptionsSpec { List = ["вперёд", "стоять"] } },
            Cases = [new OutcomeCase { Option = "вперёд", Actions = [Move(3)] }, new OutcomeCase { Option = "стоять" }],
        },
    });

    private static ObjectDefinition Asking(string id, params string[] options) => Event(id, id, new EffectSpec
    {
        Target = s_self,
        Outcomes = new OutcomesSpec
        {
            Choice = new OutcomeChoice { Prompt = "Как?", Options = new ChoiceOptionsSpec { List = [.. options] } },
            Cases = [.. options.Select((o, i) => new OutcomeCase { Option = o, Actions = [Coins(i + 1)] })],
        },
    });

    /// <summary>The events that ask, for the random games of <see cref="SuspendedEffectInvariantTests"/>.</summary>
    internal static IReadOnlyList<ObjectDefinition> AskingEvents => [s_crossroads, s_genreGift, s_hex, s_sacrifice, s_leap];

    /// <summary>The push of another player by one cell.</summary>
    internal static ObjectDefinition Shove => s_shove;

    private static readonly ObjectDefinition s_orange = EconomyScenario.Examples()["orange"];

    /// <summary>A push of another player by one cell, not hostile: an item of Петя that moves Вася onto the event cell.</summary>
    private static readonly ObjectDefinition s_shove = s_orange with
    {
        Id = "shove",
        Name = "Толчок",
        Effect = new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.Chosen, ExcludeSelf = true }, Actions = [Move(1)] },
    };

    /// <summary>Two draws in one use: a bad deck first, then the special one (D-505 asks the special first).</summary>
    private static readonly ObjectDefinition s_doubleFortune = s_orange with
    {
        Id = "double-fortune",
        Name = "Двойное предсказание",
        Effect = new EffectSpec { Target = s_self, Actions = [new DrawEventAction { Deck = "bad" }, new DrawEventAction { Deck = "special" }] },
    };

    /// <summary>
    /// <code>start → a → e → b → c → d → f → finish</code> with the event cell <c>e</c> drawing from the deck <c>ask</c> of
    /// <paramref name="asking"/> (one entry: the ticket is always 0), <paramref name="extra"/> objects and decks in the same
    /// pack. Players Вася, Петя, Маша, Коля; Вася stands on <c>a</c> and holds an orange. The categories: Horror (Silent Hill
    /// 3 hours, Persona 9 hours, in this order for <see cref="Scenario.RollTitle"/>) and RPG (Disco Elysium).
    /// </summary>
    private static Scenario Season(
        ObjectDefinition asking,
        Func<Ruleset, Ruleset>? rules = null,
        MapGraph? map = null,
        ObjectDefinition[]? extra = null,
        WheelDefinition[]? decks = null)
    {
        var s = Scenario.New(rules is null ? TestRuleset.Create().WithEvents() : rules(TestRuleset.Create().WithEvents()))
            .WithMap(map ?? MapBuilder.New().EventCell("e", "ask").Path("start", "a", "e", "b", "c", "d", "f", "finish").Build())
            .WithCategory("Horror").WithCategory("RPG")
            .WithGame("Silent Hill", 3, "Horror").WithGame("Persona", 9, "Horror").WithGame("Disco Elysium", 30, "RPG")
            .WithPlayers("Вася", "Петя", "Маша", "Коля");
        ObjectDefinition[] objects =
        [
            asking, s_orange, s_shove, s_doubleFortune,
            EconomyScenario.Examples()["forced-genre"], EconomyScenario.Examples()["lucky-die"], EconomyScenario.Examples()["shield"],
            EconomyScenario.Examples()["shield-effect"], .. extra ?? [],
        ];
        WheelDefinition[] wheels = [EventScenario.Deck(EventKind.Good, "ask", asking.Id), .. decks ?? []];
        s.WithContent(new ContentPack
        {
            Objects = [.. objects.DistinctBy(o => o.Id).OrderBy(o => o.Id, StringComparer.Ordinal)],
            Wheels = [.. wheels.OrderBy(w => w.Id, StringComparer.Ordinal)],
        });
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Позиция для теста", CellId: "a"));
        s.Give("Вася", "orange");
        return s;
    }

    /// <summary>Вася steps onto the event cell with his orange; the draw takes ticket 0.</summary>
    private static Scenario StepOntoTheEventCell(Scenario s)
    {
        s.NextRandom(0).Used("Вася", "orange");
        return s;
    }

    private static Guid PendingChoiceId(Scenario s, string player) => s.Player(player).Choice!.ChoiceId;

    private static Scenario Answer(Scenario s, string player, string option) =>
        s.Act(new MakeChoice(s.PlayerId(player), PendingChoiceId(s, player), option));

    private static string N(Guid id) => id.ToString("N");

    // ---- S5.7-01: the question waits on the server ----

    [Fact]
    public void An_event_that_asks_suspends_and_the_question_waits_on_the_server()
    {
        var s = StepOntoTheEventCell(Season(s_crossroads));
        var coins = s.Player("Вася").Coins;

        ScenarioAssert.Accepted(s);
        Assert.Equal("e", s.Player("Вася").CellId);
        Assert.Equal("crossroads", Assert.Single(s.LastEvents<EventDrawn>()).ObjectId);
        var suspended = Assert.Single(s.LastEvents<EffectSuspended>());
        Assert.Equal((s.PlayerId("Вася"), "crossroads"), (suspended.PlayerId, suspended.ObjectId));
        Assert.Equal(new[] { "монетки", "шаг" }, suspended.Options.Select(o => o.Id));

        // The general pending choice of the effect kind, with the same options and id
        var choice = s.Player("Вася").Choice!;
        Assert.Equal((ChoiceKind.Effect, suspended.ChoiceId), (choice.Kind, choice.ChoiceId));
        Assert.Equal(new[] { "монетки", "шаг" }, choice.Options.Select(o => o.Id));

        // Nothing of the outcomes ran yet, and the event comes after the draw
        Assert.Equal(coins, s.Player("Вася").Coins);
        Assert.True(s.Last.Events.ToList().IndexOf(s.LastEvents<EventDrawn>().Single()) < s.Last.Events.ToList().IndexOf(suspended));

        // SPEC «Общий механизм выбора»: the turn waits for the answer
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.ChoicePending);
    }

    // ---- S5.7-02: the answer resumes in a new command ----

    [Theory]
    [InlineData("монетки", 3, "e")]
    [InlineData("шаг", 0, "b")]
    public void The_answer_by_the_common_choice_resumes_the_effect_in_a_new_command(string option, int coinsGained, string cell)
    {
        var s = StepOntoTheEventCell(Season(s_crossroads));
        var coins = s.Player("Вася").Coins;
        var choiceId = PendingChoiceId(s, "Вася");
        var drawCommand = s.LastCommandId;

        Answer(s, "Вася", option);

        ScenarioAssert.Accepted(s);
        Assert.NotEqual(drawCommand, s.LastCommandId);
        Assert.Equal(new EffectResumed(s.PlayerId("Вася"), choiceId, option), Assert.Single(s.LastEvents<EffectResumed>()));
        Assert.Equal(coins + coinsGained, s.Player("Вася").Coins);
        Assert.Equal(cell, s.Player("Вася").CellId);
        Assert.Null(s.Player("Вася").Choice);

        // A second answer (a second tab, a double click) finds nothing to answer; the turn goes on
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new MakeChoice(x.PlayerId("Вася"), choiceId, option)), RejectionCodes.NoPendingChoice);
        s.RollTitle("Вася", "Silent Hill");
        ScenarioAssert.Accepted(s);
    }

    // ---- S5.7-03: an answer off the options, a stale choice, somebody else's answer ----

    [Fact]
    public void An_answer_off_the_options_a_stale_choice_or_another_players_answer_is_refused()
    {
        var s = StepOntoTheEventCell(Season(s_crossroads));
        var choice = s.Player("Вася").Choice!;

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new MakeChoice(x.PlayerId("Вася"), choice.ChoiceId, "очки")), RejectionCodes.UnknownChoiceOption);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new MakeChoice(x.PlayerId("Вася"), choice.ChoiceId, "")), RejectionCodes.UnknownChoiceOption);
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new MakeChoice(x.PlayerId("Вася"), Guid.Parse("00000000-0000-0000-0000-000000000009"), "монетки")), RejectionCodes.NoPendingChoice);

        // Петя may not answer Вася's question, even with its id
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new MakeChoice(x.PlayerId("Петя"), choice.ChoiceId, "монетки")), RejectionCodes.NoPendingChoice);

        Assert.Equal(choice, s.Player("Вася").Choice);
    }

    // ---- S5.7-04: computed references are kept ----

    [Fact]
    public void Values_computed_before_the_question_are_kept_and_the_answer_fills_the_choice()
    {
        var s = Season(s_genreGift);
        var coins = s.Player("Вася").Coins;

        // The ticket of the draw, then the d4 of the event: 3
        s.NextRandom(0, 3).Used("Вася", "orange");

        // The actions before the question ran: +1 coin and the roll, written as its result
        Assert.Equal(coins + 1, s.Player("Вася").Coins);
        Assert.Equal(3, Assert.Single(s.LastEvents<EffectRolled>()).Total);
        var suspended = Assert.Single(s.LastEvents<EffectSuspended>());

        // The next action is the one after the question (zero-based, D-563); the options are the season's categories
        Assert.Equal(3, suspended.NextAction);
        Assert.Equal(new[] { "Horror", "RPG" }, suspended.Options.Select(o => o.Id));
        Assert.DoesNotContain("forced-genre", s.Inventory("Вася"));

        Answer(s, "Вася", "RPG");

        // $roll is the 3 rolled before the question, not a new roll; $choice is the answer
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<EffectRolled>());
        Assert.Equal(coins + 1 + 3, s.Player("Вася").Coins);
        Assert.Equal("RPG", s.Held("Вася", "forced-genre").Params!["tag"]);
        Assert.Equal(0, s.Random.ScriptedLeft);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    // ---- S5.7-05, S5.7-06: the target chosen is a question, counted by the server ----

    [Fact]
    public void A_chosen_target_of_an_event_is_asked_among_the_players_the_server_counted()
    {
        var s = Season(s_hex);
        s.Act(new SetPlayerInactive(s.PlayerId("Маша"), true));
        ScenarioAssert.Accepted(s);
        var petya = s.Player("Петя").Coins;

        StepOntoTheEventCell(s);

        // Active players other than the drawer; Маша is inactive, so not an option
        var suspended = Assert.Single(s.LastEvents<EffectSuspended>());
        Assert.Equal(new[] { N(s.PlayerId("Петя")), N(s.PlayerId("Коля")) }.Order(), suspended.Options.Select(o => o.Id).Order());
        ScenarioAssert.RejectsWithoutChanges(s, x => Answer(x, "Вася", N(x.PlayerId("Маша"))), RejectionCodes.UnknownChoiceOption);
        ScenarioAssert.RejectsWithoutChanges(s, x => Answer(x, "Вася", N(x.PlayerId("Вася"))), RejectionCodes.UnknownChoiceOption);

        Answer(s, "Вася", N(s.PlayerId("Петя")));

        ScenarioAssert.Accepted(s);
        Assert.Equal(petya - 2, s.Player("Петя").Coins);
    }

    [Fact]
    public void A_target_that_stopped_qualifying_before_the_answer_is_skipped_and_the_answer_is_not_refused()
    {
        // Н-10: the answer is checked against the snapshot of the options, the target again when the effect goes on
        var s = StepOntoTheEventCell(Season(s_hex));
        var kolya = s.Player("Коля").Coins;
        s.Act(new SetPlayerInactive(s.PlayerId("Коля"), true));
        ScenarioAssert.Accepted(s);

        Answer(s, "Вася", N(s.PlayerId("Коля")));

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<EffectResumed>());
        Assert.Equal(kolya, s.Player("Коля").Coins);
        Assert.DoesNotContain(s.LastEvents<CoinsChanged>(), c => c.PlayerId == s.PlayerId("Коля"));
        Assert.Null(s.Player("Вася").Choice);
    }

    [Fact]
    public void The_first_finisher_is_not_among_the_targets_of_an_event()
    {
        // EV16, D-510: Маша finished first and is frozen; nobody hits her
        var s = Season(s_hex);
        s.RollTitle("Маша", "Persona").Start("Маша").NextRandom(4, 4, 4).Complete("Маша");
        Assert.NotNull(s.Player("Маша").Finish);
        var run = s.State.Runs.Values.Single(r => r.PlayerId == s.PlayerId("Маша")).RunId;
        s.Act(new ApproveProof(run, Comment: "видел"));
        ScenarioAssert.Accepted(s);
        var masha = s.Player("Маша").Coins;

        StepOntoTheEventCell(s);

        var suspended = Assert.Single(s.LastEvents<EffectSuspended>());
        Assert.DoesNotContain(N(s.PlayerId("Маша")), suspended.Options.Select(o => o.Id));
        ScenarioAssert.RejectsWithoutChanges(s, x => Answer(x, "Вася", N(x.PlayerId("Маша"))), RejectionCodes.UnknownChoiceOption);
        Assert.Equal(masha, s.Player("Маша").Coins);
    }

    // ---- S5.7-07: pick chosen ----

    [Fact]
    public void A_pick_of_an_own_object_is_asked_among_the_instances_held()
    {
        var s = Season(s_sacrifice);
        var die = s.Give("Вася", "lucky-die");
        var shield = s.Give("Вася", "shield");
        var petyas = s.Give("Петя", "shield");

        StepOntoTheEventCell(s);

        // The orange is spent by its use; the options are what Вася holds at the question
        var suspended = Assert.Single(s.LastEvents<EffectSuspended>());
        Assert.Equal(new[] { N(die), N(shield) }.Order(), suspended.Options.Select(o => o.Id).Order());
        ScenarioAssert.RejectsWithoutChanges(s, x => Answer(x, "Вася", N(petyas)), RejectionCodes.UnknownChoiceOption);

        Answer(s, "Вася", N(die));

        ScenarioAssert.Accepted(s);
        Assert.Equal(die, Assert.Single(s.LastEvents<ObjectRemoved>()).InstanceId);
        Assert.Equal(new[] { "shield" }, s.Inventory("Вася"));
        Assert.Equal(new[] { "shield" }, s.Inventory("Петя"));
    }

    // ---- S5.7-08: the level and the budget of the continuation ----

    [Fact]
    public void The_continuation_goes_on_at_the_level_it_stopped_at()
    {
        // EV31: the orange's stop on e1 draws on level 1, the gamble pushes onto e2 — the leap asks on level 2; its answer
        // pushes onto e3 (a draw of level 3), whose gamble pushes onto e4: that reaction is the fourth level and is cut
        var map = MapBuilder.New()
            .EventCell("e1", "risky").EventCell("e2", "ask").EventCell("e3", "risky").EventCell("e4", "risky").EventCell("e5", "risky")
            .Path("start", "a", "e1", "a1", "b1", "e2", "a2", "b2", "e3", "a3", "b3", "e4", "a4", "b4", "e5", "a5", "finish")
            .Build();
        var s = Season(s_leap, map: map, extra: [EconomyScenario.Examples()["gamble"]], decks: [EventScenario.Deck(EventKind.Risky, null, "gamble")]);

        s.NextRandom(0, 6, 0).Used("Вася", "orange");
        Assert.Equal("e2", s.Player("Вася").CellId);
        Assert.Equal(2, Assert.Single(s.LastEvents<EffectSuspended>()).Depth);

        s.NextRandom(0, 6);
        Answer(s, "Вася", "вперёд");

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<EventDrawn>());
        Assert.Equal(EffectChainLimit.Depth, Assert.Single(s.LastEvents<EffectChainCut>()).Limit);
        Assert.Equal("e4", s.Player("Вася").CellId);
    }

    [Fact]
    public void The_continuation_has_a_new_budget_of_events()
    {
        // Measured on the default limit: the draw and the answer each fit a limit of the larger of them, but not their sum
        static Scenario Played(Func<Ruleset, Ruleset>? rules)
        {
            var s = StepOntoTheEventCell(Season(s_crossroads, rules));
            Answer(s, "Вася", "монетки");
            return s;
        }

        var measured = Played(null);
        var draw = measured.History[^2].Events.Count;
        var answer = measured.History[^1].Events.Count;
        var limit = Math.Max(draw, answer);
        Assert.True(draw + answer > limit);

        var s = Played(r => r with { Effects = r.Effects with { MaxEventsPerCommand = limit } });

        Assert.DoesNotContain(s.History[^2].Events, e => e is EffectChainCut);
        Assert.DoesNotContain(s.Last.Events, e => e is EffectChainCut);
        Assert.Equal(measured.Player("Вася").Coins, s.Player("Вася").Coins);
    }

    // ---- S5.7-09: several suspended effects, one by one ----

    [Fact]
    public void Several_suspended_effects_ask_one_by_one_the_special_first()
    {
        // D-505: special, good, bad — the bad event was drawn first, the special asks first
        var s = Season(
            s_crossroads,
            extra: [Asking("bad-ask", "x", "y"), Asking("special-ask", "p", "q")],
            decks: [EventScenario.Deck(EventKind.Bad, null, "bad-ask"), EventScenario.Deck(EventKind.Special, null, "special-ask")]);
        s.Give("Вася", "double-fortune");

        s.NextRandom(0, 0).Used("Вася", "double-fortune");

        Assert.Equal(2, s.LastEvents<EffectSuspended>().Count());
        Assert.Equal(new[] { "p", "q" }, s.Player("Вася").Choice!.Options.Select(o => o.Id));

        Answer(s, "Вася", "p");

        ScenarioAssert.Accepted(s);
        Assert.Equal("p", Assert.Single(s.LastEvents<EffectResumed>()).OptionId);
        var next = s.Player("Вася").Choice!;
        Assert.Equal(ChoiceKind.Effect, next.Kind);
        Assert.Equal(new[] { "x", "y" }, next.Options.Select(o => o.Id));
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.ChoicePending);

        Answer(s, "Вася", "y");

        ScenarioAssert.Accepted(s);
        Assert.Null(s.Player("Вася").Choice);
    }

    // ---- S5.7-10: suspended on another player's push ----

    [Fact]
    public void A_push_by_another_player_suspends_on_the_pushed_and_the_pusher_does_not_wait()
    {
        var s = Season(s_crossroads);
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        var run = s.State.Runs.Values.Single(r => r.PlayerId == s.PlayerId("Вася"));
        s.Give("Петя", "shove");

        s.NextRandom(0).Used("Петя", "shove", "Вася");

        // Петя's command is accepted whole; the question is Вася's
        ScenarioAssert.Accepted(s);
        Assert.Equal(s.PlayerId("Вася"), Assert.Single(s.LastEvents<EffectSuspended>()).PlayerId);
        Assert.Null(s.Player("Петя").Choice);
        Assert.Equal(ChoiceKind.Effect, s.Player("Вася").Choice!.Kind);
        s.RollTitle("Петя", "Persona");
        ScenarioAssert.Accepted(s);

        // Н-11: Вася's run waits for the answer and is not changed by it (invariant 16)
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Complete("Вася"), RejectionCodes.ChoicePending);
        Assert.Equal(run, s.State.Runs[run.RunId]);

        Answer(s, "Вася", "монетки");
        ScenarioAssert.Accepted(s);
        Assert.Equal(run, s.State.Runs[run.RunId]);
        s.NextRandom(4).Complete("Вася");
        ScenarioAssert.Accepted(s);
    }

    // ---- S5.7-11, S5.7-16: a closed tab, a replay, the same seed ----

    [Fact]
    public void A_pending_effect_choice_survives_a_rebuild_of_the_state_from_the_log()
    {
        var s = StepOntoTheEventCell(Season(s_genreGift));

        var replayed = SeasonEngine.Replay(s.Log.Select(e => EventCodec.Decode(EventCodec.Encode(e))));

        Assert.Equal(s.State, replayed);
        Assert.Equal(s.Player("Вася").Choice, replayed.Players[s.PlayerId("Вася")].Choice);

        // The answer works on the rebuilt state as on the live one
        var answered = SeasonEngine.Execute(replayed, new MakeChoice(s.PlayerId("Вася"), PendingChoiceId(s, "Вася"), "Horror"), s.Context());
        Assert.True(answered.IsAccepted, answered.Rejection?.Detail);
    }

    [Fact]
    public void Same_seed_and_commands_give_the_same_log_with_a_suspension_and_its_answer()
    {
        // EV49, invariant 14: the draw, the roll before the question and the outcome are results in the log
        static Scenario Played()
        {
            var s = Season(s_genreGift, seed: 7);
            s.Used("Вася", "orange");
            Answer(s, "Вася", "Horror");
            return s;
        }

        var first = Played();
        var second = Played();

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
        Assert.Equal(first.State, SeasonEngine.Replay(first.Log.Select(e => EventCodec.Decode(EventCodec.Encode(e)))));
    }

    private static Scenario Season(ObjectDefinition asking, int seed)
    {
        // The same season with an unscripted random source of the seed
        var s = Scenario.New(TestRuleset.Create().WithEvents(), seed)
            .WithMap(MapBuilder.New().EventCell("e", "ask").Path("start", "a", "e", "b", "c", "d", "f", "finish").Build())
            .WithCategory("Horror").WithCategory("RPG").WithGame("Silent Hill", 3, "Horror").WithGame("Disco Elysium", 30, "RPG")
            .WithPlayers("Вася", "Петя");
        s.WithContent(new ContentPack
        {
            Objects = [.. new[] { asking, s_orange, EconomyScenario.Examples()["forced-genre"] }.OrderBy(o => o.Id, StringComparer.Ordinal)],
            Wheels = [EventScenario.Deck(EventKind.Good, "ask", asking.Id)],
        });
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Позиция для теста", CellId: "a"));
        s.Give("Вася", "orange");
        return s;
    }

    // ---- S5.7-12: undo ----

    [Fact]
    public void Undo_of_the_draw_after_the_answer_is_refused_and_names_the_answer()
    {
        var s = Season(s_genreGift);
        var before = s.Player("Вася");
        s.NextRandom(0, 3).Used("Вася", "orange");
        var draw = s.LastCommandId;
        Answer(s, "Вася", "RPG");
        var answer = s.LastCommandId;

        s.Act(new UndoCommand(draw, "Ошибка"));

        Assert.Equal(RejectionCodes.UndoDependents, s.Last.Rejection!.Code);
        Assert.Contains(answer, s.Last.Rejection.Related);

        // In reverse order both go: the season is as before the step
        s.Act(new UndoCommand(answer, "Ошибка"));
        ScenarioAssert.Accepted(s);
        s.Act(new UndoCommand(draw, "Ошибка"));
        ScenarioAssert.Accepted(s);
        var after = s.Player("Вася");
        Assert.Equal((before.CellId, before.Coins, before.Points), (after.CellId, after.Coins, after.Points));
        Assert.Null(after.Choice);
        Assert.Equal(new[] { "orange" }, s.Inventory("Вася"));
    }

    [Fact]
    public void Undo_of_the_answer_brings_the_question_back()
    {
        var s = Season(s_genreGift);
        s.NextRandom(0, 3).Used("Вася", "orange");
        var choice = s.Player("Вася").Choice;
        var coins = s.Player("Вася").Coins;
        Answer(s, "Вася", "RPG");

        s.Act(new UndoCommand(s.LastCommandId, "Не то"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(choice, s.Player("Вася").Choice);
        Assert.Equal(coins, s.Player("Вася").Coins);
        Assert.DoesNotContain("forced-genre", s.Inventory("Вася"));

        // Answered again, the kept $roll is still the 3 of the draw
        Answer(s, "Вася", "Horror");
        Assert.Equal(coins + 3, s.Player("Вася").Coins);
        Assert.Equal("Horror", s.Held("Вася", "forced-genre").Params!["tag"]);
    }

    // ---- S5.7-13: the admin discards ----

    [Fact]
    public void The_admin_discards_a_suspended_effect_and_the_rest_of_it_never_runs()
    {
        // Н-12: closed without the actions left; what ran before the question stays
        var s = Season(s_genreGift);
        var coins = s.Player("Вася").Coins;
        s.NextRandom(0, 3).Used("Вася", "orange");
        var choiceId = PendingChoiceId(s, "Вася");

        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Игрок пропал", DiscardOffer: true));

        ScenarioAssert.Accepted(s);
        Assert.Contains(new ChoiceDiscarded(s.PlayerId("Вася"), choiceId), s.Last.Events);
        Assert.Empty(s.LastEvents<EffectResumed>());
        Assert.Null(s.Player("Вася").Choice);
        Assert.Equal(coins + 1, s.Player("Вася").Coins);
        Assert.DoesNotContain("forced-genre", s.Inventory("Вася"));
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new MakeChoice(x.PlayerId("Вася"), choiceId, "RPG")), RejectionCodes.NoPendingChoice);

        s.RollTitle("Вася", "Silent Hill");
        ScenarioAssert.Accepted(s);
    }

    // ---- S5.7-14: the deadline ----

    [Theory]
    [InlineData("монетки", 3, "e")]
    [InlineData("шаг", 0, "e")]
    public void After_the_deadline_the_answer_is_accepted_and_moves_nothing(string option, int coinsGained, string cell)
    {
        // Н-13, EV37: the question came before the deadline; after it coins still change, the position does not
        var s = Season(s_crossroads);
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow.AddHours(1)));
        StepOntoTheEventCell(s);
        var coins = s.Player("Вася").Coins;
        s.Advance(TimeSpan.FromHours(2));
        s.Act(new ReachDeadline());
        Assert.Equal(SeasonStatus.Closing, s.State.Status);

        Answer(s, "Вася", option);

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<EffectResumed>());
        Assert.Equal(coins + coinsGained, s.Player("Вася").Coins);
        Assert.Equal(cell, s.Player("Вася").CellId);
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Empty(s.LastEvents<Engine.Runs.CompletionRolled>());
        Assert.Null(s.Player("Вася").Choice);
    }

    [Fact]
    public void The_season_does_not_finish_while_an_effect_waits_for_its_answer()
    {
        // D-561, like a branch (D-305): the answer belongs to a step made before the deadline
        var s = StepOntoTheEventCell(Season(s_crossroads));
        s.MoveStatusTo(SeasonStatus.Closing);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ChangeSeasonStatus(SeasonStatus.Finished)), RejectionCodes.EffectChoicePending);

        Answer(s, "Вася", "монетки");
        s.MoveStatusTo(SeasonStatus.Finished);
        Assert.Equal(SeasonStatus.Finished, s.State.Status);
    }

    // ---- Timeouts by the timer (D-560) ----

    [Fact]
    public void Without_a_timeout_an_unanswered_question_waits()
    {
        var s = StepOntoTheEventCell(Season(s_crossroads));
        var choice = s.Player("Вася").Choice;

        s.Advance(TimeSpan.FromDays(30)).FireTimers();

        Assert.Empty(s.Last.Events.OfType<EffectResumed>());
        Assert.Equal(choice, s.Player("Вася").Choice);
    }

    [Fact]
    public void With_a_timeout_the_timer_answers_by_a_random_option_written_in_the_log()
    {
        var s = StepOntoTheEventCell(Season(s_crossroads, r => r with { Events = r.Events! with { AnswerTimeoutHours = 24 } }));
        var choice = s.Player("Вася").Choice!;

        // Not before the timeout
        s.Advance(TimeSpan.FromHours(23)).FireTimers();
        Assert.Empty(s.Last.Events.OfType<EffectResumed>());
        Assert.Equal(choice, s.Player("Вася").Choice);

        // Past it the server picks the second option and writes it: a replay never picks again
        s.Advance(TimeSpan.FromHours(2)).NextRandom(1).FireTimers();

        ScenarioAssert.Accepted(s);
        Assert.Equal(new EffectResumed(s.PlayerId("Вася"), choice.ChoiceId, "шаг"), Assert.Single(s.LastEvents<EffectResumed>()));
        Assert.Equal("b", s.Player("Вася").CellId);
        Assert.Null(s.Player("Вася").Choice);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void A_timeout_is_a_number_of_the_rules_and_must_be_positive()
    {
        var on = TestRuleset.Create().WithEvents();

        Assert.Contains(RulesetValidator.Validate(on with { Events = on.Events! with { AnswerTimeoutHours = 0 } }), e => e.Path == "events.answerTimeoutHours");
        Assert.DoesNotContain(RulesetValidator.Validate(on with { Events = on.Events! with { AnswerTimeoutHours = 24 } }), e => e.Path == "events.answerTimeoutHours");
        Assert.Null(on.Events!.AnswerTimeoutHours);
    }

    // ---- The flag (EV35, D-564) ----

    [Fact]
    public void With_events_switched_off_mid_season_the_answer_is_refused_and_the_question_stays()
    {
        // D-420: a mechanic switched off freezes; the admin's discard still applies
        var s = StepOntoTheEventCell(Season(s_crossroads, r => r with { Events = r.Events! with { AnswerTimeoutHours = 24 } }));
        var choice = s.Player("Вася").Choice;
        s.WithRuleset(r => r with { Features = r.Features with { Events = false } });

        ScenarioAssert.RejectsWithoutChanges(s, x => Answer(x, "Вася", "монетки"), RejectionCodes.FeatureDisabled);

        s.Advance(TimeSpan.FromHours(48)).NextRandom(1).FireTimers();
        Assert.Empty(s.Last.Events.OfType<EffectResumed>());
        Assert.Equal(choice, s.Player("Вася").Choice);

        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Механика выключена", DiscardOffer: true));
        ScenarioAssert.Accepted(s);
        Assert.Null(s.Player("Вася").Choice);
    }

    [Fact]
    public void With_events_off_no_question_of_an_event_is_ever_asked()
    {
        // Invariant 19: an orange onto an event cell with events off draws nothing and asks nothing (D-532: drawEvent is manual)
        var s = Season(s_crossroads);
        s.WithRuleset(r => r with { Features = r.Features with { Events = false } });

        s.Used("Вася", "orange");

        Assert.Empty(s.LastEvents<EffectSuspended>());
        Assert.Null(s.Player("Вася").Choice);
    }

    // ---- S5.7-15: the pack no longer refuses events with questions ----

    [Theory]
    [InlineData("crossroads")]
    [InlineData("genre-gift")]
    [InlineData("hex")]
    [InlineData("sacrifice")]
    [InlineData("leap")]
    public void An_event_with_a_question_is_published_with_events_on(string id)
    {
        // D-511 lifts the refusal of D-402 for events: the question waits on the server
        var asking = new[] { s_crossroads, s_genreGift, s_hex, s_sacrifice, s_leap }.Single(o => o.Id == id);
        var pack = new ContentPack
        {
            Objects = [.. new[] { asking, EconomyScenario.Examples()["forced-genre"] }.OrderBy(o => o.Id, StringComparer.Ordinal)],
            Wheels = [EventScenario.Deck(EventKind.Good, null, id)],
        };

        Assert.Empty(ContentPublishing.Check(pack, events: true));
    }

    [Fact]
    public void An_event_on_a_selector_that_needs_no_answer_is_published()
    {
        // 5.3 lifts the rest of D-536 (D-554): an event on the leader reaches him without a question; with the question of
        // 5.7 no target of an event is refused any more (D-559 (3))
        var leader = s_hex with { Id = "hex-leader", Effect = s_hex.Effect! with { Target = new TargetSpec { Selector = TargetSelector.Leader } } };

        Assert.DoesNotContain("$.objects[0].effect.target", ContentPublishing.Check(new ContentPack { Objects = [leader] }, events: true).Select(e => e.Path));
    }
}

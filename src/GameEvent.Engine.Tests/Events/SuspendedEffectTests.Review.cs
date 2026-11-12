using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// The review of task 5.7 (D-569): what the implementation of D-567 does beyond the first tests — the target checked again
/// at every later question, the level of each continuation, the refusals of the pack, a question behind another choice,
/// the discard of a question only, the timer's moment, the skip after the deadline.
/// </summary>
public partial class SuspendedEffectTests
{
    /// <summary>A bad event on a player the drawer picks, with a second question before its blow: «−2 монетки».</summary>
    private static readonly ObjectDefinition s_hexAsking = Event("hex-asking", "Сглаз с вопросом", new EffectSpec
    {
        Target = new TargetSpec { Selector = TargetSelector.Chosen, ExcludeSelf = true },
        Actions =
        [
            new RequestChoiceAction { Prompt = "Точно?", Options = new ChoiceOptionsSpec { List = ["да", "нет"] } },
            Coins(-2),
        ],
    });

    /// <summary>A way the player picks, steps rolled: the roll comes after the answer (D-567 (2)).</summary>
    private static readonly ObjectDefinition s_way = Event("way", "Развилка судьбы", new EffectSpec
    {
        Target = s_self,
        Actions = [new MoveAction { Steps = ContentValue.TryParse("1d6")!, Direction = MoveDirection.Chosen }],
    });

    /// <summary>A portal or coins: the teleport after the deadline is skipped too (Н-13).</summary>
    private static readonly ObjectDefinition s_portal = Event("portal", "Портал", new EffectSpec
    {
        Target = s_self,
        Outcomes = new OutcomesSpec
        {
            Choice = new OutcomeChoice { Prompt = "Куда?", Options = new ChoiceOptionsSpec { List = ["портал", "монетки"] } },
            Cases =
            [
                new OutcomeCase { Option = "портал", Actions = [new TeleportAction { Cell = "d" }] },
                new OutcomeCase { Option = "монетки", Actions = [Coins(2)] },
            ],
        },
    });

    /// <summary>An item of Петя that makes another player draw from the deck <c>ask</c>.</summary>
    private static readonly ObjectDefinition s_curse = s_orange with
    {
        Id = "curse",
        Name = "Проклятие",
        Effect = new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.Chosen, ExcludeSelf = true }, Actions = [new DrawEventAction { Deck = "ask" }] },
    };

    /// <summary>An item that draws from the deck <c>ask</c> for its user.</summary>
    private static readonly ObjectDefinition s_fortune = s_orange with
    {
        Id = "fortune",
        Name = "Предсказание",
        Effect = new EffectSpec { Target = s_self, Actions = [new DrawEventAction { Deck = "ask" }] },
    };

    /// <summary>Two draws in one use: the bad deck first, then the good deck <c>ask</c>.</summary>
    private static readonly ObjectDefinition s_badThenGood = s_orange with
    {
        Id = "bad-then-good",
        Name = "Сначала плохое",
        Effect = new EffectSpec { Target = s_self, Actions = [new DrawEventAction { Deck = "bad" }, new DrawEventAction { Deck = "ask" }] },
    };

    private static Scenario Finishes(Scenario s, string player, string game, params int[] dice) =>
        s.RollTitle(player, game).Start(player).NextRandom(dice).Complete(player);

    // ---- Н-10 at a later question: the target is counted again (D-567 (3)) ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_chosen_target_that_stopped_qualifying_between_two_questions_is_not_hit(bool finishesFirst)
    {
        // D-510: nobody hits the first finisher; an inactive player is not a target either
        var s = StepOntoTheEventCell(Season(s_hexAsking));
        Answer(s, "Вася", N(s.PlayerId("Петя")));
        var second = Assert.Single(s.LastEvents<EffectSuspended>());
        Assert.Equal((SuspendedStep.Action, (Guid?)s.PlayerId("Петя")), (second.Step, second.TargetId));

        if (finishesFirst)
        {
            Finishes(s, "Петя", "Persona", 4, 4, 4);
            Assert.True(Targets.IsFirst(s.State, s.PlayerId("Петя")));
        }
        else
        {
            s.Act(new SetPlayerInactive(s.PlayerId("Петя"), true));
        }

        ScenarioAssert.Accepted(s);
        var petya = s.Player("Петя").Coins;

        Answer(s, "Вася", "да");

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<EffectResumed>());
        Assert.Equal(petya, s.Player("Петя").Coins);
        Assert.Empty(s.LastEvents<CoinsChanged>());
        Assert.Null(s.Player("Вася").Choice);
    }

    [Fact]
    public void A_chosen_target_still_qualifying_at_the_later_question_is_hit()
    {
        var s = StepOntoTheEventCell(Season(s_hexAsking));
        Answer(s, "Вася", N(s.PlayerId("Петя")));
        var petya = s.Player("Петя").Coins;

        Answer(s, "Вася", "да");

        ScenarioAssert.Accepted(s);
        Assert.Equal(petya - 2, s.Player("Петя").Coins);
    }

    // ---- S5.7-08: each continuation goes on at its own level ----

    [Fact]
    public void Continuations_answered_by_one_timer_go_on_each_at_its_own_level()
    {
        // Вася's question is asked on level 3 (e1 → gamble → e2 → gamble → e3), Петя's on level 1 (one step onto e3). The
        // timer answers both in one command: Петя's step onto p1 draws on level 2 and asks again — not cut at Вася's level.
        var map = MapBuilder.New()
            .EventCell("e1", "risky").EventCell("e2", "risky").EventCell("e3", "ask").EventCell("p1", "again")
            .Path("start", "a", "e1", "x1", "y1", "e2", "x2", "y2", "e3", "p1", "q1", "r1", "s1", "finish")
            .Build();
        var s = Season(
            s_crossroads,
            r => r with { Events = r.Events! with { AnswerTimeoutHours = 24 } },
            map,
            extra: [EconomyScenario.Examples()["gamble"], Asking("again", "x", "y")],
            decks: [EventScenario.Deck(EventKind.Risky, null, "gamble"), EventScenario.Deck(EventKind.Good, "again", "again")]);

        s.NextRandom(0, 6, 0, 6, 0).Used("Вася", "orange");
        Assert.Equal("e3", s.Player("Вася").CellId);
        Assert.Equal(3, Assert.Single(s.LastEvents<EffectSuspended>()).Depth);

        s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Позиция для теста", CellId: "y2"));
        s.Give("Петя", "orange");
        s.NextRandom(0).Used("Петя", "orange");
        Assert.Equal(1, Assert.Single(s.LastEvents<EffectSuspended>()).Depth);

        // The timer picks in the order of the players' ids: «монетки» for Вася, «шаг» for Петя
        var picks = s.State.Players.Keys.Select(id => id == s.PlayerId("Вася") ? 0 : 1).ToArray();
        s.Advance(TimeSpan.FromHours(25)).NextRandom([.. picks.Take(2), 0]).FireTimers();

        ScenarioAssert.Accepted(s);
        Assert.Equal(2, s.LastEvents<EffectResumed>().Count());
        Assert.Empty(s.LastEvents<EffectChainCut>());
        Assert.Equal("p1", s.Player("Петя").CellId);
        var again = Assert.Single(s.LastEvents<EffectSuspended>());
        Assert.Equal((s.PlayerId("Петя"), "again", 2), (again.PlayerId, again.ObjectId, again.Depth));
    }

    // ---- D-568: what the pack still refuses in an event ----

    [Fact]
    public void An_event_may_not_ask_inside_a_case_of_its_outcomes()
    {
        var inCase = Event("in-case", "Вопрос в случае", new EffectSpec
        {
            Target = s_self,
            Outcomes = new OutcomesSpec
            {
                Roll = ContentValue.TryParse("1d6")!,
                Cases =
                [
                    new OutcomeCase
                    {
                        From = 1,
                        To = 6,
                        Actions = [Coins(1), new RequestChoiceAction { Prompt = "Какой?", Options = new ChoiceOptionsSpec { List = ["a", "b"] } }],
                    },
                ],
            },
        });

        var errors = ContentPublishing.Check(new ContentPack { Objects = [inCase] }, events: true);

        var error = Assert.Single(errors, e => e.Path == "$.objects[0].effect.actions[1]");
        Assert.Contains("inside a case", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_asks_for_one_object_at_a_time()
    {
        var several = Event("several", "Несколько на выбор", new EffectSpec
        {
            Target = s_self,
            Actions = [Coins(1), new TakeObjectAction { Mode = TakeMode.Destroy, Pick = Pick.Chosen, Count = ContentValue.Of(2), Target = ActionTarget.Self }],
        });

        var errors = ContentPublishing.Check(new ContentPack { Objects = [several] }, events: true);

        var error = Assert.Single(errors, e => e.Path == "$.objects[0].effect.actions[1].count");
        Assert.Contains("one object at a time", error.Message, StringComparison.Ordinal);
    }

    // ---- D-567 (10): a question waits behind another choice ----

    [Fact]
    public void A_question_drawn_while_choosing_a_branch_is_asked_after_the_branch()
    {
        // Вася's own move stops at the fork f with steps left; Петя's curse makes him draw meanwhile
        var map = MapBuilder.New().Path("start", "a", "f", "b1", "j", "k", "l", "m", "finish").Path("f", "c1", "j").Build();
        var s = Season(s_crossroads, map: map, extra: [s_curse]);
        Finishes(s, "Вася", "Silent Hill", 3);
        var branch = s.Player("Вася").Choice!;
        Assert.Equal(ChoiceKind.Branch, branch.Kind);
        s.Give("Петя", "curse");

        s.NextRandom(0).Used("Петя", "curse", "Вася");

        ScenarioAssert.Accepted(s);
        var suspended = Assert.Single(s.LastEvents<EffectSuspended>());
        Assert.Equal(s.PlayerId("Вася"), suspended.PlayerId);
        Assert.Equal(branch, s.Player("Вася").Choice);

        s.ChooseBranch("Вася", "c1");

        ScenarioAssert.Accepted(s);
        var next = s.Player("Вася").Choice!;
        Assert.Equal((ChoiceKind.Effect, suspended.ChoiceId), (next.Kind, next.ChoiceId));
        var coins = s.Player("Вася").Coins;
        Answer(s, "Вася", "монетки");
        ScenarioAssert.Accepted(s);
        Assert.Equal(coins + 3, s.Player("Вася").Coins);
        Assert.Null(s.Player("Вася").Choice);
    }

    // ---- D-567 (8): the answer of a player who has since become the first finisher only closes the question ----

    [Fact]
    public void The_answer_of_a_player_who_has_since_become_the_first_finisher_only_closes_the_question()
    {
        // Маша finishes first (preliminary), Вася after her and draws a question; Маша's proof is rejected: Вася is first
        var s = Season(s_crossroads, extra: [s_fortune]);
        Finishes(s, "Маша", "Persona", 4, 4, 4);
        var mashasRun = s.State.Runs.Values.Single(r => r.PlayerId == s.PlayerId("Маша")).RunId;
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Позиция для теста", CellId: "f"));
        Finishes(s, "Вася", "Silent Hill", 4);
        Assert.NotNull(s.Player("Вася").Finish);
        Assert.False(Targets.IsFirst(s.State, s.PlayerId("Вася")));
        s.Give("Вася", "fortune");
        s.NextRandom(0).Used("Вася", "fortune");
        Assert.Equal(ChoiceKind.Effect, s.Player("Вася").Choice!.Kind);

        s.Act(new RejectProof(mashasRun, "Не то"));
        ScenarioAssert.Accepted(s);
        Assert.True(Targets.IsFirst(s.State, s.PlayerId("Вася")));
        var coins = s.Player("Вася").Coins;

        Answer(s, "Вася", "монетки");

        ScenarioAssert.Accepted(s);
        Assert.IsType<EffectResumed>(Assert.Single(s.Last.Events));
        Assert.Equal(coins, s.Player("Вася").Coins);
        Assert.Null(s.Player("Вася").Choice);
    }

    // ---- D-567 (9), D-569: the admin's discard ----

    [Fact]
    public void The_admin_discards_a_question_while_the_player_plays_and_the_run_goes_on()
    {
        var s = Season(s_crossroads);
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        var run = s.Player("Вася").ActiveRunId;
        s.Give("Петя", "shove");
        s.NextRandom(0).Used("Петя", "shove", "Вася");
        var choiceId = PendingChoiceId(s, "Вася");

        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Вопрос не нужен", DiscardOffer: true));

        ScenarioAssert.Accepted(s);
        Assert.Contains(new ChoiceDiscarded(s.PlayerId("Вася"), choiceId), s.Last.Events);
        Assert.Equal((TurnPhase.Playing, run), (s.Player("Вася").Phase, s.Player("Вася").ActiveRunId));
        Assert.Null(s.Player("Вася").Choice);

        // Without a question the discard of a playing player is refused as before (C6)
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new AdjustPlayer(x.PlayerId("Вася"), "Ещё раз", DiscardOffer: true)), RejectionCodes.PlayerBusy);
        s.NextRandom(4).Complete("Вася");
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void A_discard_while_rolling_takes_the_question_only_and_the_offer_stays()
    {
        // D-569: the discard takes what is asked now; the rolled game is the player's, a second discard takes it
        var s = Season(s_crossroads);
        s.RollTitle("Вася", "Silent Hill");
        var offer = s.Player("Вася").Offer!;
        s.Give("Петя", "shove");
        s.NextRandom(0).Used("Петя", "shove", "Вася");
        var choiceId = PendingChoiceId(s, "Вася");

        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Снять вопрос", DiscardOffer: true));

        ScenarioAssert.Accepted(s);
        Assert.Contains(new ChoiceDiscarded(s.PlayerId("Вася"), choiceId), s.Last.Events);
        Assert.Empty(s.LastEvents<OfferDiscarded>());
        Assert.Equal((TurnPhase.Rolling, offer), (s.Player("Вася").Phase, s.Player("Вася").Offer));
        Assert.Null(s.Player("Вася").Choice);

        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Снять игру", DiscardOffer: true));
        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<OfferDiscarded>());
        Assert.Null(s.Player("Вася").Offer);
    }

    // ---- D-565: the order good before bad ----

    [Fact]
    public void A_good_question_is_asked_before_a_bad_one_drawn_earlier()
    {
        var s = Season(Asking("good-ask", "p", "q"), extra: [Asking("bad-ask", "x", "y"), s_badThenGood], decks: [EventScenario.Deck(EventKind.Bad, null, "bad-ask")]);
        s.Give("Вася", "bad-then-good");

        s.NextRandom(0, 0).Used("Вася", "bad-then-good");

        Assert.Equal(new[] { "bad-ask", "good-ask" }, s.LastEvents<EffectSuspended>().Select(e => e.ObjectId));
        Assert.Equal(new[] { "p", "q" }, s.Player("Вася").Choice!.Options.Select(o => o.Id));
        Answer(s, "Вася", "q");
        Assert.Equal(new[] { "x", "y" }, s.Player("Вася").Choice!.Options.Select(o => o.Id));
    }

    // ---- D-560: the timer's moment and its bound ----

    [Fact]
    public void The_next_timer_of_a_player_is_the_moment_his_question_times_out()
    {
        // The scheduler fires FireTimers only at Timers.Next (the column NextTimerAt): without it the timeout never comes
        var s = Season(s_crossroads, r => r with { Events = r.Events! with { AnswerTimeoutHours = 24 } });
        var asked = s.Clock.UtcNow;
        StepOntoTheEventCell(s);
        var vasya = s.Player("Вася");

        Assert.Equal(asked.AddHours(24), Timers.Next(vasya, s.State.Rules.Features));
        Assert.Null(Timers.Next(vasya, s.State.Rules.Features with { Events = false }));

        var waits = StepOntoTheEventCell(Season(s_crossroads));
        Assert.Null(Timers.Next(waits.Player("Вася"), waits.State.Rules.Features));
    }

    [Fact]
    public void A_timeout_is_at_most_a_year()
    {
        var on = TestRuleset.Create().WithEvents();

        Assert.Contains(RulesetValidator.Validate(on with { Events = on.Events! with { AnswerTimeoutHours = 365 * 24 + 1 } }), e => e.Path == "events.answerTimeoutHours");
        Assert.DoesNotContain(RulesetValidator.Validate(on with { Events = on.Events! with { AnswerTimeoutHours = 365 * 24 } }), e => e.Path == "events.answerTimeoutHours");
    }

    // ---- D-567 (2): the way is asked before the steps are rolled ----

    [Fact]
    public void A_way_asked_by_an_event_rolls_its_steps_once_after_the_answer()
    {
        var s = Season(s_way);

        s.NextRandom(0).Used("Вася", "orange");

        Assert.Empty(s.LastEvents<EffectRolled>());
        var suspended = Assert.Single(s.LastEvents<EffectSuspended>());
        Assert.Equal(new[] { "forward", "back" }, suspended.Options.Select(o => o.Id));

        s.NextRandom(2);
        Answer(s, "Вася", "forward");

        ScenarioAssert.Accepted(s);
        Assert.Equal(2, Assert.Single(s.LastEvents<EffectRolled>()).Total);
        Assert.Equal("c", s.Player("Вася").CellId);
        Assert.Equal(0, s.Random.ScriptedLeft);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log.Select(e => EventCodec.Decode(EventCodec.Encode(e)))));
    }

    // ---- S5.7-14: after the deadline the move is skipped with a record, without a roll ----

    private static Scenario PastTheDeadline(ObjectDefinition asking)
    {
        var s = Season(asking);
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow.AddHours(1)));
        s.NextRandom(0).Used("Вася", "orange");
        s.Advance(TimeSpan.FromHours(2));
        s.Act(new ReachDeadline());
        Assert.Equal(SeasonStatus.Closing, s.State.Status);
        return s;
    }

    [Fact]
    public void After_the_deadline_a_move_answered_is_skipped_with_a_record()
    {
        var s = PastTheDeadline(s_crossroads);

        Answer(s, "Вася", "шаг");

        ScenarioAssert.Accepted(s);
        Assert.Equal(new EffectMoveSkipped(s.PlayerId("Вася"), "crossroads"), Assert.Single(s.LastEvents<EffectMoveSkipped>()));
        Assert.Empty(s.LastEvents<PlayerMoved>());
    }

    [Fact]
    public void After_the_deadline_an_answer_without_a_move_records_no_skip()
    {
        var s = PastTheDeadline(s_crossroads);

        Answer(s, "Вася", "монетки");

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<EffectMoveSkipped>());
    }

    [Fact]
    public void After_the_deadline_a_teleport_answered_is_skipped_with_a_record()
    {
        var s = PastTheDeadline(s_portal);

        Answer(s, "Вася", "портал");

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<EffectMoveSkipped>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal("e", s.Player("Вася").CellId);
    }

    [Fact]
    public void After_the_deadline_a_way_answered_rolls_no_steps()
    {
        // «без движения и бросков исхода»: the dice of the move are not rolled for a move that cannot happen
        var s = PastTheDeadline(s_way);

        Answer(s, "Вася", "back");

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<EffectRolled>());
        Assert.Single(s.LastEvents<EffectMoveSkipped>());
        Assert.Equal(0, s.Random.ScriptedLeft);
    }
}

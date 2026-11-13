using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Effects;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;
using static GameEvent.Engine.Tests.GlobalEvents.GlobalSetup;

namespace GameEvent.Engine.Tests.GlobalEvents;

/// <summary>
/// Global events (task 5.9, EV18, EV36/EV37 parts, EV38, EV49; SPEC «Общие ивенты запускает админ: длительность, баннер на
/// главной, при необходимости общая цель», «Цели»; D-516, D-517, D-519, D-537, D-700, D-750…D-754): the admin launches an
/// event of the content with a term, the launch keeps its snapshot and term, its effect goes once to everyone but the first
/// finisher (a later finisher only for points and coins) through a settlement of its own; the event ends by its term once,
/// by the admin, or with the deadline, and a goal not reached gives nothing. Written by <c>test-author</c> from the SPEC
/// and the public contract, before the implementation is finished.
/// </summary>
public class GlobalEventTests
{
    // ---- Launch: snapshot, term, effect once ----

    [Fact]
    public void The_launch_keeps_the_snapshot_of_the_event_and_its_term()
    {
        var s = Season();
        var goal = HoursGoal(50, OnAll(Coins(5)));

        var launched = s.Launch("coin-rain", 72, goal);

        Assert.Equal("coin-rain", launched.ObjectId);
        Assert.Equal(CoinRain, launched.Definition);
        Assert.Equal(s.Clock.UtcNow, launched.StartedAt);
        Assert.Equal(s.Clock.UtcNow.AddHours(72), launched.EndsAt);
        Assert.Equal(goal, launched.Goal);
        var state = s.Global(launched.GlobalEventId);
        Assert.Equal(GlobalEventStatus.Active, state.Status);
        Assert.Equal((CoinRain, launched.StartedAt, launched.EndsAt, goal), (state.Definition, state.StartedAt, state.EndsAt, state.Goal));
        Assert.Empty(state.Contributions);
        Assert.Equal(0m, state.Progress);
        Assert.False(state.Reached);
        Assert.Null(state.EndReason);
    }

    [Fact]
    public void A_later_change_of_the_content_does_not_change_a_running_event()
    {
        // Events keep results, not references (invariant 4): the snapshot of the launch stays
        var s = Season();
        var launched = s.Launch("coin-rain", 72);
        var richer = Event("coin-rain", OnAll(Coins(9)));

        s.WithContent(new ContentPack { Objects = [.. BaseObjects().Where(o => o.Id != "coin-rain").Append(richer).OrderBy(o => o.Id, StringComparer.Ordinal)] });

        Assert.Equal(CoinRain, s.Global(launched.GlobalEventId).Definition);
        s.SettleAll();
        Assert.All(Names, p => Assert.Equal(2, s.Player(p).Coins));
    }

    [Fact]
    public void The_launch_only_queues_its_effect_and_the_settlement_gives_it_to_everyone_once()
    {
        var s = Season();

        var launched = s.Launch("coin-rain", 72);

        // D-537: no coins inside the launch itself
        Assert.Empty(s.LastEvents<CoinsChanged>());
        var queued = s.Queued();
        Assert.Equal((ForAllSource.GlobalEventLaunch, launched.GlobalEventId, "coin-rain"), (queued.Source, queued.Reference, queued.ObjectId));
        Assert.Equal(CoinRain.Effect, queued.Effect);
        Assert.Equal(s.State.Players.Keys, queued.Players);

        s.SettleAll();
        Assert.All(Names, p => Assert.Equal(2, s.Player(p).Coins));

        // Once: nothing is pending, a timer and the end give nothing more
        Assert.Null(ForAllSettlements.Next(s.State));
        s.Advance(TimeSpan.FromHours(72)).Act(new FireTimers());
        s.SettleAll();
        Assert.All(Names, p => Assert.Equal(2, s.Player(p).Coins));
        Assert.Single(s.Log.OfType<ForAllQueued>());
    }

    [Fact]
    public void The_first_finisher_gets_nothing_and_a_later_finisher_gets_points_and_coins()
    {
        // SPEC «Цели»: everyone but the first finisher; by the later ones only effects on points and coins work
        var s = Season(mapLength: 4);
        s.FinishWith("Маша", "Dead Space");
        s.FinishWith("Коля", "Outlast");
        var before = s.State.Players;

        s.Launch("bonus-day", 72);
        var queued = s.Queued();
        Assert.DoesNotContain(s.PlayerId("Маша"), queued.Players);
        Assert.Contains(s.PlayerId("Коля"), queued.Players);
        s.SettleAll();

        Assert.Equal((before[s.PlayerId("Маша")].Coins, before[s.PlayerId("Маша")].Points), (s.Player("Маша").Coins, s.Player("Маша").Points));
        Assert.Equal((before[s.PlayerId("Коля")].Coins + 2, before[s.PlayerId("Коля")].Points + 1), (s.Player("Коля").Coins, s.Player("Коля").Points));
        Assert.Equal(2, s.Player("Вася").Coins);
        Assert.Equal(1, s.Player("Вася").Points);
    }

    [Fact]
    public void A_later_finisher_gets_nothing_of_an_effect_beyond_points_and_coins()
    {
        // D-700: a later finisher is a target only if the effect acts by points and coins alone
        var s = Season(mapLength: 4);
        s.FinishWith("Маша", "Dead Space");
        s.FinishWith("Коля", "Outlast");
        var kolya = s.Player("Коля");

        s.Launch("gift-rain", 72);
        var queued = s.Queued();
        Assert.DoesNotContain(s.PlayerId("Коля"), queued.Players);
        Assert.DoesNotContain(s.PlayerId("Маша"), queued.Players);
        s.SettleAll();

        Assert.Equal((kolya.Coins, 0), (s.Player("Коля").Coins, s.Oranges("Коля")));
        Assert.Equal((2, 1), (s.Player("Вася").Coins, s.Oranges("Вася")));
        Assert.Equal((2, 1), (s.Player("Петя").Coins, s.Oranges("Петя")));
    }

    [Fact]
    public void An_effect_with_a_term_of_its_own_is_given_to_everyone_and_lives_by_its_own_timer()
    {
        // D-517: «выдать всем эффект со сроком { hours: N }» — the launch gives it, its own duration does the rest
        var s = Season();

        s.Launch("shield-day", 72);
        s.SettleAll();

        Assert.All(Names, p => Assert.Equal(1, s.Count(p, "day-shield")));
    }

    [Theory]
    [InlineData("self-coins", 3)]
    [InlineData("plain-coins", 1)]
    public void A_target_of_self_or_none_is_each_player_himself(string objectId, int coins)
    {
        // D-752: the effect is played on each as on himself; its target is all, self or not written
        var s = Season();

        s.Launch(objectId, 24);
        s.SettleAll();

        Assert.All(Names, p => Assert.Equal(coins, s.Player(p).Coins));
    }

    [Theory]
    [InlineData("banner")]
    [InlineData("karaoke")]
    public void An_event_without_an_effect_or_a_manual_one_is_a_banner_and_a_goal_only(string objectId)
    {
        // D-752: no manual effects on everyone are created
        var s = Season();

        var launched = s.Launch(objectId, 24, RunsGoal(3));

        Assert.Empty(s.LastEvents<ForAllQueued>());
        Assert.Empty(s.State.ManualEffects);
        Assert.Equal(GlobalEventStatus.Active, s.Global(launched.GlobalEventId).Status);
    }

    [Fact]
    public void The_event_carries_the_kind_of_the_deck_that_holds_it()
    {
        // D-706, D-752: the kind of the first deck by id holding it
        var s = Season(pack: Pack(null, EventScenario.Deck(EventKind.Good, "good", "coin-rain"), EventScenario.Deck(EventKind.Bad, "zbad", "coin-rain")));

        var launched = s.Launch("coin-rain", 24);

        Assert.Equal(EventKind.Good, launched.Kind);
        Assert.Equal(EventKind.Good, s.Queued().Kind);
        Assert.Equal(EventKind.Good, s.Global(launched.GlobalEventId).Kind);
    }

    [Fact]
    public void An_event_in_no_deck_has_no_kind()
    {
        var s = Season();

        var launched = s.Launch("coin-rain", 24);

        Assert.Null(launched.Kind);
        Assert.Null(s.Queued().Kind);
    }

    // ---- Launch refusals ----

    [Fact]
    public void With_the_flag_off_the_launch_is_refused()
    {
        var s = Season(TestRuleset.Create().WithEvents());

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new LaunchGlobalEvent("coin-rain", 72)), RejectionCodes.FeatureDisabled);
    }

    [Fact]
    public void An_unknown_object_is_refused()
    {
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new LaunchGlobalEvent("no-such-event", 72)), RejectionCodes.ObjectUnknown);
    }

    [Fact]
    public void An_object_removed_from_the_content_is_refused()
    {
        // A live object of the season's content (D-750); a removed one is soft-deleted
        var s = Season();
        s.WithContent(new ContentPack { Objects = [.. BaseObjects().Where(o => o.Id != "coin-rain").OrderBy(o => o.Id, StringComparer.Ordinal)] });

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new LaunchGlobalEvent("coin-rain", 72)), RejectionCodes.ObjectUnknown);
    }

    [Theory]
    [InlineData("orange")]
    [InlineData("forced-genre")]
    [InlineData("day-shield")]
    public void An_object_that_is_not_an_event_is_refused(string objectId)
    {
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new LaunchGlobalEvent(objectId, 72)), RejectionCodes.GlobalEventNotEvent);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(8785, false)]
    [InlineData(1, true)]
    [InlineData(8784, true)]
    public void The_term_is_from_1_to_8784_hours(int hours, bool valid)
    {
        var s = Season();

        if (valid)
        {
            Assert.Equal(s.Clock.UtcNow.AddHours(hours), s.Launch("coin-rain", hours).EndsAt);
        }
        else
        {
            ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new LaunchGlobalEvent("coin-rain", hours)), RejectionCodes.ContentInvalid);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(1_000_001, false)]
    [InlineData(1, true)]
    [InlineData(1_000_000, true)]
    public void The_goal_target_is_from_1_to_a_million(int target, bool valid)
    {
        var s = Season();

        foreach (var goal in new[] { HoursGoal(target), RunsGoal(target) })
        {
            if (valid)
            {
                var launched = s.Launch("banner", 24, goal);
                s.Act(new EndGlobalEvent(launched.GlobalEventId));
                ScenarioAssert.Accepted(s);
            }
            else
            {
                ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new LaunchGlobalEvent("banner", 24, goal)), RejectionCodes.ContentInvalid);
            }
        }
    }

    public static TheoryData<string> InvalidEvents() =>
    [
        "self-move", "self-teleport", "gamble", "coin-or-step",
        "on-leader", "on-random", "with-question",
    ];

    /// <summary>
    /// Events valid in a pack (drawn by one player) that a global event may not run (D-519, D-703 (3), D-752). A trigger, a
    /// term, a condition or an intercept on an event the pack refuses already, so only the goal reward is checked for them.
    /// </summary>
    private static ObjectDefinition[] Unfit() =>
    [
        Event("self-move", new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.Self }, Actions = [new MoveAction { Steps = ContentValue.Of(2) }] }),
        Event("self-teleport", new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.Self }, Actions = [new TeleportAction { Cell = "nearestShortcut" }] }),
        Event("on-leader", new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.Leader }, Actions = [Coins(1)] }),
        Event("on-random", new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.RandomActive }, Actions = [Coins(1)] }),
        Event("with-question", new EffectSpec
        {
            Target = All,
            Actions =
            [
                new RequestChoiceAction { Prompt = "Какой жанр?", Options = new ChoiceOptionsSpec { From = ChoiceSource.Categories } },
                new GiveObjectAction { ObjectId = "forced-genre", Params = new ContentParamDictionary([new("tag", "$choice")]) },
            ],
        }),
    ];

    [Theory]
    [MemberData(nameof(InvalidEvents))]
    public void An_event_a_global_event_may_not_run_is_refused_with_the_path_of_its_effect(string objectId)
    {
        // D-519: no move or teleport on everyone from the system; D-752: no trigger, term, condition, intercept, other
        // target, question (but the first-action vote)
        var s = Season(pack: Pack(Unfit()));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new LaunchGlobalEvent(objectId, 24)), RejectionCodes.ContentInvalid);
        Assert.Contains("effect", s.Last.Rejection!.Detail, StringComparison.Ordinal);
    }

    public static TheoryData<string> InvalidRewards() => ["move", "teleport", "trigger", "duration", "unknown-object", "question", "leader"];

    private static EffectSpec Reward(string kind) => kind switch
    {
        "move" => OnAll(Coins(1), new MoveAction { Steps = ContentValue.Of(1) }),
        "teleport" => OnAll(new TeleportAction { Cell = "nearestShortcut" }),
        "trigger" => new EffectSpec { Target = All, Trigger = Trigger.BeforeDice, Actions = [Coins(1)] },
        "duration" => new EffectSpec { Target = All, Duration = new DurationSpec { Runs = 1 }, Actions = [Coins(1)] },
        "unknown-object" => OnAll(Give("no-such-object")),
        "question" => OnAll(new RequestChoiceAction { Prompt = "Что?", Options = new ChoiceOptionsSpec { List = ["a", "b"] } }),
        "leader" => new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.Leader }, Actions = [Coins(1)] },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(InvalidRewards))]
    public void A_goal_reward_a_global_event_may_not_give_is_refused_with_its_path(string kind)
    {
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new LaunchGlobalEvent("coin-rain", 24, RunsGoal(3, Reward(kind)))), RejectionCodes.ContentInvalid);
        Assert.Contains("goal.reward", s.Last.Rejection!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_reward_without_the_move_is_accepted()
    {
        var s = Season();

        s.Launch("coin-rain", 24, RunsGoal(3, OnAll(Coins(1), Give("orange"))));
    }

    [Fact]
    public void At_most_max_active_events_run_and_an_ended_one_frees_its_place()
    {
        var s = Season(Rules(maxActive: 2));
        var first = s.Launch("coin-rain", 72);
        s.Launch("banner", 72);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new LaunchGlobalEvent("bonus-day", 72)), RejectionCodes.GlobalEventTooMany);

        s.Act(new EndGlobalEvent(first.GlobalEventId));
        ScenarioAssert.Accepted(s);
        s.Launch("bonus-day", 72);
    }

    [Fact]
    public void An_event_whose_term_is_up_does_not_count_even_before_its_timer()
    {
        // D-750: «идёт» — not ended and its term by the engine's clock not up
        var s = Season(Rules(maxActive: 1));
        s.Launch("coin-rain", 24);
        s.Advance(TimeSpan.FromHours(23));
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new LaunchGlobalEvent("banner", 24)), RejectionCodes.GlobalEventTooMany);

        s.Advance(TimeSpan.FromHours(1));

        s.Launch("banner", 24);
    }

    [Fact]
    public void After_the_deadline_no_event_is_launched()
    {
        var s = Season();
        s.MoveStatusTo(SeasonStatus.Closing);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new LaunchGlobalEvent("coin-rain", 72)), RejectionCodes.SeasonDeadlinePassed);
    }

    [Fact]
    public void Past_the_deadline_before_the_scheduler_closes_the_season_no_event_is_launched_either()
    {
        var s = Season();
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow.AddHours(1)));
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(1));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new LaunchGlobalEvent("coin-rain", 72)), RejectionCodes.SeasonDeadlinePassed);
    }

    // ---- The end ----

    [Fact]
    public void The_timer_ends_the_event_at_its_term_once()
    {
        var s = Season();
        var launched = s.Launch("coin-rain", 72, HoursGoal(50, OnAll(Coins(5))));
        s.SettleAll();

        // A minute early: nothing
        s.Advance(TimeSpan.FromHours(72) - TimeSpan.FromMinutes(1)).Act(new FireTimers());
        Assert.Empty(s.LastEvents<GlobalEventEnded>());
        Assert.Equal(GlobalEventStatus.Active, s.Global(launched.GlobalEventId).Status);

        s.Advance(TimeSpan.FromMinutes(1)).Act(new FireTimers());
        ScenarioAssert.Accepted(s);
        Assert.Equal(new GlobalEventEnded(launched.GlobalEventId, GlobalEventEndReason.Timer), Assert.Single(s.LastEvents<GlobalEventEnded>()));
        Assert.Equal((GlobalEventStatus.Ended, GlobalEventEndReason.Timer), (s.Global(launched.GlobalEventId).Status, s.Global(launched.GlobalEventId).EndReason));

        // A goal not reached gives nothing
        Assert.Empty(s.LastEvents<ForAllQueued>());
        Assert.Empty(s.LastEvents<GlobalGoalReached>());

        s.Advance(TimeSpan.FromHours(1)).Act(new FireTimers());
        Assert.Empty(s.LastEvents<GlobalEventEnded>());
        Assert.Single(s.Log.OfType<GlobalEventEnded>());
    }

    [Fact]
    public void The_admin_ends_an_event_early_and_its_timer_then_does_nothing()
    {
        var s = Season();
        var launched = s.Launch("coin-rain", 72, HoursGoal(50, OnAll(Coins(5))));
        s.SettleAll();

        s.Advance(TimeSpan.FromHours(1)).Act(new EndGlobalEvent(launched.GlobalEventId));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new GlobalEventEnded(launched.GlobalEventId, GlobalEventEndReason.Admin), Assert.Single(s.LastEvents<GlobalEventEnded>()));
        Assert.Empty(s.LastEvents<ForAllQueued>());
        Assert.Equal(GlobalEventEndReason.Admin, s.Global(launched.GlobalEventId).EndReason);

        s.Advance(TimeSpan.FromHours(72)).Act(new FireTimers());
        Assert.Empty(s.LastEvents<GlobalEventEnded>());
    }

    [Fact]
    public void Ending_an_ended_event_is_refused()
    {
        var s = Season();
        var launched = s.Launch("coin-rain", 72);
        s.SettleAll();
        s.Act(new EndGlobalEvent(launched.GlobalEventId));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new EndGlobalEvent(launched.GlobalEventId)), RejectionCodes.GlobalEventEnded);
    }

    [Fact]
    public void Ending_an_event_ended_by_its_timer_is_refused()
    {
        var s = Season();
        var launched = s.Launch("coin-rain", 24);
        s.SettleAll();
        s.Advance(TimeSpan.FromHours(24)).Act(new FireTimers());

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new EndGlobalEvent(launched.GlobalEventId)), RejectionCodes.GlobalEventEnded);
    }

    [Fact]
    public void Ending_an_unknown_event_is_refused()
    {
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new EndGlobalEvent(Guid.Parse("6e000000-0000-0000-0000-000000000001"))), RejectionCodes.GlobalEventUnknown);
    }

    [Fact]
    public void The_deadline_ends_every_running_event_and_a_goal_not_reached_gives_nothing()
    {
        // D-516, EV37
        var s = Season();
        var first = s.Launch("coin-rain", 72, HoursGoal(50, OnAll(Coins(5))));
        s.SettleAll();
        var second = s.Launch("banner", 72, RunsGoal(5, OnAll(Coins(5))));
        s.Completed("Вася", "Dead Space");
        var coins = s.State.Players.ToDictionary(p => p.Key, p => p.Value.Coins);

        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [new GlobalEventEnded(first.GlobalEventId, GlobalEventEndReason.Deadline), new GlobalEventEnded(second.GlobalEventId, GlobalEventEndReason.Deadline)],
            s.LastEvents<GlobalEventEnded>().OrderBy(e => e.GlobalEventId));
        Assert.Empty(s.LastEvents<GlobalGoalReached>());
        Assert.Empty(s.LastEvents<ForAllQueued>());
        s.SettleAll();
        Assert.Equal(coins, s.State.Players.ToDictionary(p => p.Key, p => p.Value.Coins));
        Assert.All(s.State.GlobalEvents.Values, g => Assert.Equal((GlobalEventStatus.Ended, GlobalEventEndReason.Deadline), (g.Status, g.EndReason!.Value)));

        // Ended: neither the admin nor the timer ends it again
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new EndGlobalEvent(first.GlobalEventId)), RejectionCodes.GlobalEventEnded);
        s.Advance(TimeSpan.FromHours(72)).Act(new FireTimers());
        Assert.Empty(s.LastEvents<GlobalEventEnded>());
    }

    [Fact]
    public void The_scheduler_reaching_the_deadline_ends_them_the_same_way()
    {
        var s = Season();
        var launched = s.Launch("coin-rain", 72);
        s.SettleAll();
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow.AddHours(2)));
        s.Advance(TimeSpan.FromHours(2));

        s.Act(new ReachDeadline());

        ScenarioAssert.Accepted(s);
        Assert.Equal(new GlobalEventEnded(launched.GlobalEventId, GlobalEventEndReason.Deadline), Assert.Single(s.LastEvents<GlobalEventEnded>()));
    }

    [Fact]
    public void An_event_ended_before_the_deadline_is_not_ended_again_by_it()
    {
        var s = Season();
        var launched = s.Launch("coin-rain", 72);
        s.SettleAll();
        s.Act(new EndGlobalEvent(launched.GlobalEventId));

        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));

        Assert.Empty(s.LastEvents<GlobalEventEnded>());
        Assert.Equal(GlobalEventEndReason.Admin, s.Global(launched.GlobalEventId).EndReason);
    }

    // ---- A player added after the launch (Н-16) ----

    [Fact]
    public void A_player_added_after_the_launch_gets_no_launch_effect()
    {
        var s = Season();
        s.Launch("coin-rain", 72);
        s.SettleAll();

        s.WithPlayers("Оля");
        s.SettleAll();

        Assert.Equal(0, s.Player("Оля").Coins);
        Assert.Null(ForAllSettlements.Next(s.State));
    }

    [Fact]
    public void A_player_added_between_the_launch_and_its_settlement_is_not_among_its_targets()
    {
        // D-700: the targets are fixed at the cause
        var s = Season();
        s.Launch("coin-rain", 72);

        s.WithPlayers("Оля");
        s.SettleAll();

        Assert.Equal(0, s.Player("Оля").Coins);
        Assert.All(Names, p => Assert.Equal(2, s.Player(p).Coins));
    }

    // ---- The flag switched off ----

    [Fact]
    public void The_flag_is_not_switched_off_while_an_event_runs()
    {
        // D-751
        var s = Season();
        var launched = s.Launch("banner", 72);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ChangeRuleset(x.Ruleset with { Features = x.Ruleset.Features with { GlobalEvents = false } })), RejectionCodes.GlobalEventsInPlay);

        s.Act(new EndGlobalEvent(launched.GlobalEventId));
        s.Act(new ChangeRuleset(s.Ruleset with { Features = s.Ruleset.Features with { GlobalEvents = false } }));
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void The_flag_is_not_switched_off_while_a_contribution_waits_for_its_proof()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, RunsGoal(5));
        var run = s.Completed("Вася", "Dead Space");
        s.Act(new EndGlobalEvent(launched.GlobalEventId));
        var off = s.Ruleset with { Features = s.Ruleset.Features with { GlobalEvents = false } };

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ChangeRuleset(off)), RejectionCodes.GlobalEventsInPlay);

        s.Act(new ApproveProof(run, null, Comment));
        ScenarioAssert.Accepted(s);
        s.Act(new ChangeRuleset(off));
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Switched_off_the_events_of_the_mechanic_are_refused()
    {
        var s = Season();
        var launched = s.Launch("banner", 24);
        s.Act(new EndGlobalEvent(launched.GlobalEventId));
        s.Act(new ChangeRuleset(s.Ruleset with { Features = s.Ruleset.Features with { GlobalEvents = false } }));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new LaunchGlobalEvent("coin-rain", 24)), RejectionCodes.FeatureDisabled);
    }

    [Fact]
    public void The_flag_needs_events_on()
    {
        // D-751, EV35: the validator path
        var rules = Rules();
        var errors = RulesetValidator.Validate(rules with { Features = rules.Features with { Events = false } });

        Assert.Contains(errors, e => e.Path == "features.globalEvents");
        Assert.Empty(RulesetValidator.Validate(rules));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void Max_active_is_from_1_to_10(int maxActive, bool valid)
    {
        var errors = RulesetValidator.Validate(Rules(maxActive: maxActive));

        Assert.Equal(!valid, errors.Any(e => e.Path == "globalEvents.maxActive"));
    }

    // ---- Undo (EV38, invariant 12) ----

    [Fact]
    public void Undoing_a_launch_before_its_settlement_removes_the_event_and_its_settlement()
    {
        var s = Season();
        s.Launch("coin-rain", 72, RunsGoal(5));
        var launch = s.LastCommandId;

        s.Act(new UndoCommand(launch, "Ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.State.GlobalEvents);
        Assert.Empty(s.State.ForAll);
        Assert.Null(ForAllSettlements.Next(s.State));
        s.Launch("coin-rain", 72);
    }

    [Fact]
    public void Undoing_a_launch_after_its_settlement_waits_for_the_batches()
    {
        // D-701: the batches hold the cause; undone in reverse, then the launch
        var s = Season();
        s.Launch("coin-rain", 72);
        var launch = s.LastCommandId;
        s.SettleAll();
        var batch = s.LastCommandId;

        s.Act(new UndoCommand(launch, "Ошибка админа"));
        Assert.False(s.Last.IsAccepted);
        Assert.Equal(RejectionCodes.UndoDependents, s.Last.Rejection!.Code);

        s.Act(new UndoCommand(batch, "Ошибка админа"));
        ScenarioAssert.Accepted(s);
        Assert.All(Names, p => Assert.Equal(0, s.Player(p).Coins));
        s.Act(new UndoCommand(launch, "Ошибка админа"));
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.State.GlobalEvents);
        Assert.All(Names, p => Assert.Equal(0, s.Player(p).Coins));
    }

    [Fact]
    public void Undoing_the_batch_after_a_player_used_what_it_gave_names_the_use()
    {
        var s = Season();
        s.Launch("gift-rain", 72);
        s.SettleAll();
        var batch = s.LastCommandId;
        s.Used("Вася", "orange");
        var use = s.LastCommandId;

        s.Act(new UndoCommand(batch, "Ошибка админа"));

        Assert.False(s.Last.IsAccepted);
        Assert.Equal(RejectionCodes.UndoDependents, s.Last.Rejection!.Code);
        Assert.Contains(use, s.Last.Rejection.Related);
    }

    [Fact]
    public void Undoing_the_admins_end_lets_the_event_run_again()
    {
        var s = Season();
        var launched = s.Launch("coin-rain", 72);
        s.SettleAll();
        s.Act(new EndGlobalEvent(launched.GlobalEventId));

        s.Act(new UndoCommand(s.LastCommandId, "Ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(GlobalEventStatus.Active, s.Global(launched.GlobalEventId).Status);
        Assert.Null(s.Global(launched.GlobalEventId).EndReason);
    }

    // ---- Replay (EV49) ----

    [Fact]
    public void The_log_replays_to_the_same_state_and_the_launch_effect_is_not_run_again()
    {
        var s = Season(mapLength: 4);
        s.FinishWith("Маша", "Dead Space");
        var launched = s.Launch("gift-rain", 72, HoursGoal(8, OnAll(Coins(5))));
        s.SettleAll();
        s.Completed("Вася", "Silent Hill");
        s.SettleAll();
        s.Act(new FireTimers());

        var replay = SeasonEngine.Replay(s.StoredLog);

        Assert.Equal(s.State, replay);
        Assert.Equal(s.Global(launched.GlobalEventId), replay.GlobalEvents[launched.GlobalEventId]);
        Assert.Single(s.Log.OfType<ForAllQueued>(), q => q.Source == ForAllSource.GlobalEventLaunch);
    }
}

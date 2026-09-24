using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Runs;

/// <summary>
/// «Тех-реролл» (SPEC «Реролл, дроп, тех-реролл»: free, a reason from the list, <c>roll.techRerollWindowHours</c>
/// after the roll, later only through the admin; the admin may turn it into a drop with the penalty; D-04, D-07, D-11,
/// D-56, D-94). <see cref="TechReroll"/> writes <see cref="RunTechRerolled"/>, <see cref="GameExcluded"/>
/// (TechRerolled) and at once a new roll (SPEC diagram: Playing → Rolling) with its own free rerolls; nothing to roll
/// leaves the player Idle. <see cref="ConvertTechRerollToDrop"/> writes <see cref="TechRerollConvertedToDrop"/> and the
/// drop's penalty events by the current points and position; the player's turn is untouched.
/// The window is inclusive: exactly <c>techRerollWindowHours</c> after the roll is still allowed (D-94: «не больше»).
/// Pinned test ruleset: window 48 hours, penalty 2d4 on points and position, a bad event, one free reroll per roll.
/// </summary>
public class TechRerollTests
{
    private static readonly string[] s_horror = ["Silent Hill", "Alan Wake", "Dead Space", "Outlast"];

    private static Scenario Season(Func<Ruleset, Ruleset>? rules = null, params string[] games)
    {
        var s = Scenario.New();
        if (rules is not null)
        {
            s.WithRuleset(rules);
        }

        s.WithCategory("Horror");
        foreach (var game in games.Length == 0 ? s_horror : games)
        {
            s.WithGame(game, 12, "Horror");
        }

        return s.WithPlayers("Вася", "Петя");
    }

    private static Scenario Playing(Scenario s, string player) => s.Roll(player).Start(player);

    /// <summary>The player completes a 12-hour game: four d4 show <paramref name="dice"/>.</summary>
    private static Scenario Walk(Scenario s, string player, params int[] dice) =>
        s.Roll(player).Start(player).NextRandom(dice).Complete(player, Difficulty.Normal);

    private static Guid ActiveRun(Scenario s, string player) => s.Player(player).ActiveRunId!.Value;

    private static Scenario TechReroll(
        Scenario s, string player, TechRerollReason reason = TechRerollReason.DoesNotLaunch, string? comment = null, bool byAdmin = false) =>
        s.Act(new TechReroll(s.PlayerId(player), reason, comment, byAdmin));

    private static Scenario Convert(Scenario s, Guid runId, string comment = "Игра запускалась, это дроп") =>
        s.Act(new ConvertTechRerollToDrop(runId, comment));

    private static TimeSpan Window(Scenario s) => TimeSpan.FromHours(s.Ruleset.Roll.TechRerollWindowHours);

    /// <summary>Вася tech-rerolls his active run for a reason; returns the run id.</summary>
    private static Guid TechRerolled(Scenario s, string player = "Вася")
    {
        var runId = ActiveRun(s, player);
        TechReroll(s, player);
        ScenarioAssert.Accepted(s);
        return runId;
    }

    // ---- Within the window ----

    [Fact]
    public void Tech_reroll_within_the_window_excludes_the_game_and_rolls_another_at_once()
    {
        // Given Вася rolled, started a bit later and plays for a while, still within 48 hours of the roll
        var s = Season().Roll("Вася");
        s.Advance(TimeSpan.FromHours(1));
        s.Start("Вася");
        var vasya = s.PlayerId("Вася");
        var runId = ActiveRun(s, "Вася");
        var game = s.State.Runs[runId].GameId;
        s.Advance(TimeSpan.FromHours(20));

        // When he tech-rerolls: the game does not launch
        TechReroll(s, "Вася", TechRerollReason.DoesNotLaunch, "Вылетает на заставке");

        // Then RunTechRerolled, GameExcluded(TechRerolled), and a new GameRolled of another game, rolled now
        ScenarioAssert.Accepted(s);
        Assert.Equal(3, s.Last.Events.Count);
        Assert.Equal(
            new RunTechRerolled(runId, vasya, TechRerollReason.DoesNotLaunch, "Вылетает на заставке", ByAdmin: false, s.Clock.UtcNow),
            s.Last.Events[0]);
        Assert.Equal(new GameExcluded(vasya, game, ExclusionReason.TechRerolled), s.Last.Events[1]);
        var rolled = Assert.IsType<GameRolled>(s.Last.Events[2]);
        Assert.Equal(vasya, rolled.PlayerId);
        Assert.NotEqual(game, rolled.GameId);
        Assert.DoesNotContain(rolled.Misses, m => m.GameId == game);
        Assert.Equal(s.Clock.UtcNow, rolled.RolledAt);

        // And Вася is Rolling the new game, the run is tech-rerolled, the game is excluded for him
        var player = s.Player("Вася");
        Assert.Equal(TurnPhase.Rolling, player.Phase);
        Assert.Null(player.ActiveRunId);
        Assert.Equal(rolled.GameId, player.Offer!.GameId);
        Assert.Equal(RunStatus.TechRerolled, s.State.Runs[runId].Status);
        Assert.Contains(new GameExclusion(game, ExclusionReason.TechRerolled), player.Exclusions);
    }

    [Theory]
    [InlineData(TechRerollReason.WeakPc)]
    [InlineData(TechRerollReason.PaidUnavailable)]
    [InlineData(TechRerollReason.DoesNotLaunch)]
    [InlineData(TechRerollReason.EmulatorTooSlow)]
    public void Listed_reasons_need_no_comment(TechRerollReason reason)
    {
        var s = Playing(Season(), "Вася");

        TechReroll(s, "Вася", reason, comment: null);

        ScenarioAssert.Accepted(s);
        var logged = Assert.Single(s.LastEvents<RunTechRerolled>());
        Assert.Equal(reason, logged.Reason);
        Assert.Null(logged.Comment);
        Assert.False(logged.ByAdmin);
    }

    [Fact]
    public void Reason_other_with_a_comment_is_accepted_and_the_comment_is_logged()
    {
        var s = Playing(Season(), "Вася");

        TechReroll(s, "Вася", TechRerollReason.Other, "Нет русского языка, а английский не знаю");

        ScenarioAssert.Accepted(s);
        var logged = Assert.Single(s.LastEvents<RunTechRerolled>());
        Assert.Equal((TechRerollReason.Other, "Нет русского языка, а английский не знаю"), (logged.Reason, logged.Comment));
    }

    [Fact]
    public void Tech_reroll_is_free_and_does_not_touch_points_position_or_coins()
    {
        // Given Вася walked to c7 with 7 points and has 5 coins and a reroll coupon
        var s = Walk(Season(), "Вася", 2, 2, 2, 1);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Приз", CoinsDelta: 5, ResourceDeltas: [new ResourceDelta("freeRerolls", 1)]));
        Playing(s, "Вася");
        var before = s.Player("Вася");

        TechReroll(s, "Вася");

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PointsChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Empty(s.LastEvents<CoinsChanged>());
        Assert.Empty(s.LastEvents<ResourceChanged>());
        Assert.Empty(s.LastEvents<ManualEffectCreated>());
        Assert.Empty(s.State.ManualEffects);
        var after = s.Player("Вася");
        Assert.Equal((before.Points, before.CellId, before.Coins, before.Resources), (after.Points, after.CellId, after.Coins, after.Resources));
        Assert.Equal(before.Path, after.Path);
    }

    [Fact]
    public void New_roll_after_a_tech_reroll_has_its_own_free_reroll()
    {
        // Given Вася used his free reroll before starting (D-07: a new roll after a tech reroll is a new roll)
        var s = Season().Roll("Вася");
        s.Act(new Reroll(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);
        s.Start("Вася");

        TechReroll(s, "Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(0, s.Player("Вася").RerollsThisRoll);

        // Then his next reroll is free again, with no coins at all
        Assert.Equal(0, s.Player("Вася").Coins);
        s.Act(new Reroll(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);
        Assert.Equal(RerollPayment.FreeThisRoll, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
    }

    [Fact]
    public void New_roll_after_a_tech_reroll_can_be_a_choice_of_games()
    {
        // With choiceCount 2 the new roll offers a choice (GameChoiceRolled) without the tech-rerolled game
        var s = Season(r => r with { Roll = r.Roll with { ChoiceCount = 2 } }).Roll("Вася");
        var choice = s.Player("Вася").Choice!;
        s.Act(new MakeChoice(s.PlayerId("Вася"), choice.ChoiceId, choice.Options[0].Id));
        ScenarioAssert.Accepted(s);
        var game = s.State.Runs[ActiveRun(s, "Вася")].GameId;

        TechReroll(s, "Вася");

        ScenarioAssert.Accepted(s);
        var rolled = Assert.IsType<GameChoiceRolled>(s.Last.Events[2]);
        Assert.DoesNotContain(game, rolled.Offers.Select(o => o.GameId));
        Assert.NotNull(s.Player("Вася").Choice);
    }

    [Fact]
    public void Nothing_to_roll_after_a_tech_reroll_leaves_the_player_idle()
    {
        // Given the only game, played by Вася
        var s = Playing(Season(null, "Silent Hill"), "Вася");
        var runId = ActiveRun(s, "Вася");

        TechReroll(s, "Вася");

        // Then only the two events; Вася is Idle and his next roll finds nothing
        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(RunTechRerolled), typeof(GameExcluded)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
        Assert.Null(s.Player("Вася").Offer);
        Assert.Equal(0, s.Player("Вася").RerollsThisRoll);
        Assert.Equal(RunStatus.TechRerolled, s.State.Runs[runId].Status);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.NoAvailableGames);
    }

    [Fact]
    public void Tech_rerolled_game_is_available_to_other_players()
    {
        var s = Playing(Season(null, "Silent Hill"), "Вася");
        TechReroll(s, "Вася");

        s.Roll("Петя");

        var rolled = Assert.Single(s.LastEvents<GameRolled>());
        Assert.Equal(s.GameId("Silent Hill"), rolled.GameId);
        Assert.Empty(rolled.Misses);
        Assert.Empty(s.Player("Петя").Exclusions);
    }

    [Fact]
    public void Tech_rerolled_game_never_comes_to_the_player_again()
    {
        // Given two games: Вася tech-rerolls the first and gets the second at once
        var s = Playing(Season(null, "Silent Hill", "Alan Wake"), "Вася");
        var first = s.State.Runs[ActiveRun(s, "Вася")].GameId;
        TechReroll(s, "Вася");
        var second = s.Player("Вася").Offer!.GameId;
        Assert.NotEqual(first, second);

        // When he gives the second up by «Уже проходил», nothing is left for him
        s.Act(new DeclareAlreadyPlayed(s.PlayerId("Вася"), second));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<GameRolled>());
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.NoAvailableGames);
    }

    // ---- The window: from the roll, not the start (D-04, D-56); inclusive ----

    [Fact]
    public void Window_counts_from_the_roll_not_from_the_start()
    {
        // Given Вася rolled, waited 47 hours before starting, and played 2 more hours: 49 hours since the roll
        var s = Season().Roll("Вася");
        s.Advance(TimeSpan.FromHours(47));
        s.Start("Вася");
        s.Advance(TimeSpan.FromHours(2));

        ScenarioAssert.RejectsWithoutChanges(s, x => TechReroll(x, "Вася"), RejectionCodes.TechRerollWindowClosed);
        Assert.Equal(TurnPhase.Playing, s.Player("Вася").Phase);
    }

    [Fact]
    public void Tech_reroll_exactly_at_the_end_of_the_window_is_allowed()
    {
        var s = Season().Roll("Вася").Start("Вася");
        s.Advance(Window(s));

        TechReroll(s, "Вася");

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Tech_reroll_a_tick_after_the_window_is_rejected()
    {
        var s = Season().Roll("Вася").Start("Вася");
        s.Advance(Window(s) + TimeSpan.FromTicks(1));

        ScenarioAssert.RejectsWithoutChanges(s, x => TechReroll(x, "Вася"), RejectionCodes.TechRerollWindowClosed);
    }

    [Fact]
    public void Window_follows_the_ruleset()
    {
        // Given the window is 2 hours
        var s = Season(r => r with { Roll = r.Roll with { TechRerollWindowHours = 2 } }).Roll("Вася").Start("Вася");
        s.Advance(TimeSpan.FromHours(3));

        ScenarioAssert.RejectsWithoutChanges(s, x => TechReroll(x, "Вася"), RejectionCodes.TechRerollWindowClosed);
    }

    [Fact]
    public void Shortening_the_window_after_the_roll_does_not_close_it_for_that_run()
    {
        // D-94 (1): the window concerns the run, so it is fixed at the roll (RunSnapshot.TechRerollWindowHours)
        var s = Season().Roll("Вася").Start("Вася");
        var runId = ActiveRun(s, "Вася");
        Assert.Equal(48, s.State.Runs[runId].Snapshot.TechRerollWindowHours);
        s.WithRuleset(r => r with { Roll = r.Roll with { TechRerollWindowHours = 2 } });
        s.Advance(TimeSpan.FromHours(47));

        TechReroll(s, "Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(48, s.State.Runs[runId].Snapshot.TechRerollWindowHours);
    }

    [Fact]
    public void Lengthening_the_window_after_the_roll_does_not_reopen_it_for_that_run()
    {
        var s = Season().Roll("Вася").Start("Вася");
        s.WithRuleset(r => r with { Roll = r.Roll with { TechRerollWindowHours = 100 } });
        s.Advance(TimeSpan.FromHours(48) + TimeSpan.FromTicks(1));

        ScenarioAssert.RejectsWithoutChanges(s, x => TechReroll(x, "Вася"), RejectionCodes.TechRerollWindowClosed);
    }

    [Fact]
    public void Changed_window_applies_to_the_next_roll()
    {
        // The rule in force at the roll is snapshotted: the new roll after a tech reroll takes the changed window
        var s = Season().Roll("Вася").Start("Вася");
        s.WithRuleset(r => r with { Roll = r.Roll with { TechRerollWindowHours = 2 } });
        TechReroll(s, "Вася");
        ScenarioAssert.Accepted(s);
        s.Start("Вася");
        Assert.Equal(2, s.State.Runs[ActiveRun(s, "Вася")].Snapshot.TechRerollWindowHours);
        s.Advance(TimeSpan.FromHours(3));

        ScenarioAssert.RejectsWithoutChanges(s, x => TechReroll(x, "Вася"), RejectionCodes.TechRerollWindowClosed);
    }

    [Fact]
    public void New_roll_after_a_tech_reroll_opens_a_new_window()
    {
        // Given Вася tech-rerolled 40 hours after the first roll and started the new game
        var s = Season().Roll("Вася").Start("Вася");
        s.Advance(TimeSpan.FromHours(40));
        TechReroll(s, "Вася");
        ScenarioAssert.Accepted(s);
        s.Start("Вася");

        // When 40 more hours pass (80 since the first roll, 40 since the new one)
        s.Advance(TimeSpan.FromHours(40));
        TechReroll(s, "Вася");

        ScenarioAssert.Accepted(s);
    }

    // ---- After the window: only the admin, marked in the log ----

    [Fact]
    public void Admin_tech_rerolls_after_the_window_and_the_log_says_so()
    {
        var s = Season().Roll("Вася").Start("Вася");
        var runId = ActiveRun(s, "Вася");
        s.Advance(TimeSpan.FromHours(100));

        TechReroll(s, "Вася", TechRerollReason.PaidUnavailable, "Игру убрали из магазина", byAdmin: true);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new RunTechRerolled(runId, s.PlayerId("Вася"), TechRerollReason.PaidUnavailable, "Игру убрали из магазина", ByAdmin: true, s.Clock.UtcNow),
            s.Last.Events[0]);
        Assert.Equal(new GameExcluded(s.PlayerId("Вася"), s.State.Runs[runId].GameId, ExclusionReason.TechRerolled), s.Last.Events[1]);
        Assert.Equal(TurnPhase.Rolling, s.Player("Вася").Phase);
    }

    [Fact]
    public void Admin_tech_reroll_within_the_window_is_marked_too()
    {
        var s = Playing(Season(), "Вася");
        var runId = ActiveRun(s, "Вася");

        TechReroll(s, "Вася", TechRerollReason.WeakPc, "Попросил в чате", byAdmin: true);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new RunTechRerolled(runId, s.PlayerId("Вася"), TechRerollReason.WeakPc, "Попросил в чате", ByAdmin: true, s.Clock.UtcNow),
            Assert.Single(s.LastEvents<RunTechRerolled>()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void Admin_tech_reroll_without_a_comment_is_rejected(string? comment)
    {
        // D-94 (2): an admin tech reroll is an admin change (D-89), it needs a comment even for a listed reason,
        // inside the window and after it
        var s = Playing(Season(), "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => TechReroll(x, "Вася", TechRerollReason.DoesNotLaunch, comment, byAdmin: true), RejectionCodes.CommentRequired);

        s.Advance(TimeSpan.FromHours(100));
        ScenarioAssert.RejectsWithoutChanges(
            s, x => TechReroll(x, "Вася", TechRerollReason.DoesNotLaunch, comment, byAdmin: true), RejectionCodes.CommentRequired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Admin_tech_reroll_for_other_without_a_comment_is_rejected_as_an_admin_change(string? comment)
    {
        // D-94 (2): the admin's comment rule is checked before the «Other» rule, so the code is player.commentRequired
        var s = Playing(Season(), "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => TechReroll(x, "Вася", TechRerollReason.Other, comment, byAdmin: true), RejectionCodes.CommentRequired);
    }

    [Fact]
    public void Admin_tech_reroll_for_other_with_a_comment_is_accepted()
    {
        var s = Playing(Season(), "Вася");

        TechReroll(s, "Вася", TechRerollReason.Other, "Сломан сейв", byAdmin: true);

        ScenarioAssert.Accepted(s);
        var logged = Assert.Single(s.LastEvents<RunTechRerolled>());
        Assert.Equal((TechRerollReason.Other, "Сломан сейв", true), (logged.Reason, logged.Comment, logged.ByAdmin));
    }

    [Fact]
    public void Admin_tech_reroll_comment_over_500_characters_is_rejected()
    {
        var s = Playing(Season(), "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => TechReroll(x, "Вася", TechRerollReason.WeakPc, new string('я', 501), byAdmin: true), RejectionCodes.CommentTooLong);
    }

    [Fact]
    public void Admin_tech_reroll_needs_a_running_season()
    {
        // A tech reroll is a turn command: after the deadline the player's turn is closed for the admin too
        var s = Playing(Season(), "Вася");
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));

        ScenarioAssert.RejectsWithoutChanges(
            s, x => TechReroll(x, "Вася", comment: "Попросил в чате", byAdmin: true), RejectionCodes.SeasonNotActive);
    }

    // ---- Reason and comment ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void Reason_other_without_a_comment_is_rejected(string? comment)
    {
        var s = Playing(Season(), "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => TechReroll(x, "Вася", TechRerollReason.Other, comment), RejectionCodes.ReasonCommentRequired);
    }

    [Fact]
    public void Comment_of_500_characters_is_accepted()
    {
        var s = Playing(Season(), "Вася");
        var comment = new string('я', 500);

        TechReroll(s, "Вася", TechRerollReason.Other, comment);

        ScenarioAssert.Accepted(s);
        Assert.Equal(comment, Assert.Single(s.LastEvents<RunTechRerolled>()).Comment);
    }

    [Theory]
    [InlineData(TechRerollReason.Other)]
    [InlineData(TechRerollReason.WeakPc)]
    public void Comment_over_500_characters_is_rejected(TechRerollReason reason)
    {
        var s = Playing(Season(), "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => TechReroll(x, "Вася", reason, new string('я', 501)), RejectionCodes.CommentTooLong);
    }

    // ---- Turn rejections ----

    [Fact]
    public void Tech_reroll_while_idle_is_rejected_with_wrong_phase()
    {
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(s, x => TechReroll(x, "Вася"), RejectionCodes.WrongPhase);
    }

    [Fact]
    public void Tech_reroll_of_an_offered_game_is_rejected_with_wrong_phase()
    {
        var s = Season().Roll("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => TechReroll(x, "Вася"), RejectionCodes.WrongPhase);
    }

    [Fact]
    public void Tech_reroll_by_an_unknown_player_is_rejected()
    {
        var s = Playing(Season(), "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new TechReroll(SequentialIds.Make(0x0BAD0000, 1), TechRerollReason.WeakPc, null)),
            RejectionCodes.PlayerUnknown);
    }

    [Fact]
    public void Tech_reroll_in_a_closing_season_is_rejected()
    {
        var s = Playing(Season(), "Вася");
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));

        ScenarioAssert.RejectsWithoutChanges(s, x => TechReroll(x, "Вася"), RejectionCodes.SeasonNotActive);
    }

    // ---- The admin turns a tech reroll into a drop (RR6) ----

    [Fact]
    public void Admin_converts_to_drop()
    {
        // Given Вася walked to c7 with 7 points, tech-rerolled a game, and now plays another one
        var s = Walk(Season(), "Вася", 2, 2, 2, 1);
        Playing(s, "Вася");
        var vasya = s.PlayerId("Вася");
        var runId = TechRerolled(s);
        var game = s.State.Runs[runId].GameId;
        s.Start("Вася");
        var playingNow = s.Player("Вася");

        // When the admin turns the tech reroll into a drop and the penalty dice show 3 and 2
        s.NextRandom(3, 2);
        Convert(s, runId, "Игра запускалась, это дроп");

        // Then TechRerollConvertedToDrop, PointsChanged(-5), PlayerMoved back 5 from where he stands, the bad event
        ScenarioAssert.Accepted(s);
        Assert.Equal(4, s.Last.Events.Count);
        Assert.Equal(
            new TechRerollConvertedToDrop(runId, vasya, "Игра запускалась, это дроп", [new Die(4, 3), new Die(4, 2)], s.Clock.UtcNow),
            s.Last.Events[0]);
        Assert.Equal(new PointsChanged(vasya, -5, PointsReason.DropPenalty, runId), s.Last.Events[1]);
        Assert.Equal(
            new PlayerMoved(vasya, "c7", "c2", -5, ["c6", "c5", "c4", "c3", "c2"], MoveReason.DropPenalty, runId),
            s.Last.Events[2]);
        var created = Assert.IsType<ManualEffectCreated>(s.Last.Events[3]);
        Assert.Equal(new ManualEffectCreated(created.EffectId, vasya, EventKind.Bad, ManualEffectSource.Drop, runId), created);
        Assert.Empty(s.LastEvents<GameExcluded>());
        Assert.Empty(s.LastEvents<CoinsChanged>());

        // And the run is dropped, the exclusion's reason is a drop; his current turn is untouched
        Assert.Equal(RunStatus.Dropped, s.State.Runs[runId].Status);
        var player = s.Player("Вася");
        Assert.Contains(new GameExclusion(game, ExclusionReason.Dropped), player.Exclusions);
        Assert.DoesNotContain(player.Exclusions, x => x.GameId == game && x.Reason == ExclusionReason.TechRerolled);
        Assert.Equal((2, "c2"), (player.Points, player.CellId));
        Assert.Equal(TurnPhase.Playing, player.Phase);
        Assert.Equal(playingNow.ActiveRunId, player.ActiveRunId);
        Assert.Equal(RunStatus.Playing, s.State.Runs[player.ActiveRunId!.Value].Status);
        Assert.True(s.State.ManualEffects.ContainsKey(created.EffectId));
    }

    [Fact]
    public void Conversion_hits_the_current_position_not_the_one_at_the_tech_reroll()
    {
        // Given Вася tech-rerolled on the start, then walked to c8
        var s = Playing(Season(), "Вася");
        var runId = TechRerolled(s);
        s.Start("Вася").NextRandom(2, 2, 2, 2).Complete("Вася");
        Assert.Equal("c8", s.Player("Вася").CellId);

        s.NextRandom(1, 2);
        Convert(s, runId);

        ScenarioAssert.Accepted(s);
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal(("c8", "c5", -3), (moved.From, moved.To, moved.Steps));
        Assert.Equal(5, s.Player("Вася").Points);
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
    }

    [Fact]
    public void Conversion_on_the_start_writes_no_move_and_may_take_points_below_zero()
    {
        var s = Playing(Season(), "Вася");
        var runId = TechRerolled(s);

        s.NextRandom(4, 4);
        Convert(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(TechRerollConvertedToDrop), typeof(PointsChanged), typeof(ManualEffectCreated)],
            s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(-8, s.Player("Вася").Points);
        Assert.Equal(LinearMap.StartId, s.Player("Вася").CellId);
    }

    [Fact]
    public void Conversion_while_the_player_rolls_keeps_the_offer()
    {
        // Right after the tech reroll Вася holds the new offer; the conversion does not touch it
        var s = Walk(Season(), "Вася", 2, 2, 2, 2);
        Playing(s, "Вася");
        var runId = TechRerolled(s);
        var offer = s.Player("Вася").Offer;

        s.NextRandom(1, 1);
        Convert(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(TurnPhase.Rolling, s.Player("Вася").Phase);
        Assert.Equal(offer, s.Player("Вася").Offer);
    }

    [Fact]
    public void Conversion_follows_the_drop_rules_in_force_now()
    {
        // Given the admin switched the position part and the bad event off after the tech reroll
        var s = Walk(Season(), "Вася", 2, 2, 2, 1);
        Playing(s, "Вася");
        var runId = TechRerolled(s);
        s.WithRuleset(r => r with
        {
            Drop = r.Drop with { PenaltyDice = new PenaltyDice { Count = 1, Sides = 6 }, AffectsPosition = false, MandatoryEvent = MandatoryEvent.None },
        });

        s.NextRandom(6);
        Convert(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(TechRerollConvertedToDrop), typeof(PointsChanged)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal([new Die(6, 6)], Assert.Single(s.LastEvents<TechRerollConvertedToDrop>()).PenaltyDice);
        Assert.Equal((1, "c7"), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Fact]
    public void Conversion_is_allowed_while_the_season_is_closing()
    {
        // D-11: any time until the season is finished
        var s = Playing(Season(), "Вася");
        var runId = TechRerolled(s);
        s.Advance(TimeSpan.FromDays(20));
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        ScenarioAssert.Accepted(s);

        s.NextRandom(1, 1);
        Convert(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(RunStatus.Dropped, s.State.Runs[runId].Status);
    }

    [Theory]
    [InlineData(SeasonStatus.Finished)]
    [InlineData(SeasonStatus.Archived)]
    public void Conversion_after_the_season_is_finished_is_rejected(SeasonStatus status)
    {
        // Results are fixed: the same code as other admin changes of a finished season (season.closed)
        var s = Playing(Season(), "Вася");
        var runId = TechRerolled(s);
        for (var next = SeasonStatus.Closing; next <= status; next++)
        {
            s.Act(new ChangeSeasonStatus(next));
            ScenarioAssert.Accepted(s);
        }

        ScenarioAssert.RejectsWithoutChanges(s, x => Convert(x, runId), RejectionCodes.SeasonClosed);
    }

    [Fact]
    public void Conversion_of_an_unknown_run_is_rejected()
    {
        var s = Playing(Season(), "Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => Convert(x, SequentialIds.Make(0x0BAD0000, 7)), RejectionCodes.RunUnknown);
    }

    [Fact]
    public void Conversion_of_a_run_being_played_is_rejected()
    {
        var s = Playing(Season(), "Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => Convert(x, ActiveRun(x, "Вася")), RejectionCodes.NotTechRerolled);
    }

    [Fact]
    public void Conversion_of_a_completed_run_is_rejected()
    {
        var s = Playing(Season(), "Вася");
        var runId = ActiveRun(s, "Вася");
        s.NextRandom(1, 1, 1, 1).Complete("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => Convert(x, runId), RejectionCodes.NotTechRerolled);
    }

    [Fact]
    public void Conversion_of_a_dropped_run_is_rejected()
    {
        var s = Playing(Season(), "Вася");
        var runId = ActiveRun(s, "Вася");
        s.NextRandom(1, 1).Act(new DropRun(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Convert(x, runId), RejectionCodes.NotTechRerolled);
    }

    [Fact]
    public void Converting_twice_is_rejected_the_second_time()
    {
        var s = Playing(Season(), "Вася");
        var runId = TechRerolled(s);
        s.NextRandom(1, 1);
        Convert(s, runId);
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Convert(x, runId), RejectionCodes.NotTechRerolled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Conversion_needs_a_comment(string comment)
    {
        var s = Playing(Season(), "Вася");
        var runId = TechRerolled(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Convert(x, runId, comment), RejectionCodes.CommentRequired);
    }

    [Fact]
    public void Conversion_comment_of_500_characters_is_accepted_and_501_rejected()
    {
        var s = Playing(Season(), "Вася");
        var runId = TechRerolled(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Convert(x, runId, new string('я', 501)), RejectionCodes.CommentTooLong);

        s.NextRandom(1, 1);
        Convert(s, runId, new string('я', 500));
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Conversion_of_one_player_does_not_touch_another()
    {
        var s = Walk(Season(), "Петя", 2, 2, 2, 2);
        Playing(s, "Петя");
        Playing(s, "Вася");
        var runId = TechRerolled(s);
        var petya = s.Player("Петя");

        s.NextRandom(1, 1);
        Convert(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(petya, s.Player("Петя"));
    }

    // ---- The log ----

    [Fact]
    public void Replaying_the_log_gives_the_same_state_after_tech_rerolls_and_a_conversion()
    {
        var s = Walk(Season(), "Вася", 2, 2, 2, 1);
        Playing(s, "Вася");
        var converted = TechRerolled(s);
        s.Start("Вася");
        s.Advance(TimeSpan.FromHours(60));
        TechReroll(s, "Вася", TechRerollReason.Other, "Сломан сейв", byAdmin: true);
        ScenarioAssert.Accepted(s);
        s.NextRandom(2, 3);
        Convert(s, converted);
        ScenarioAssert.Accepted(s);

        var replayed = SeasonEngine.Replay(s.Log);

        Assert.Equal(s.State, replayed);
        Assert.Equal(RunStatus.Dropped, replayed.Runs[converted].Status);
        Assert.Single(replayed.Runs.Values, r => r.Status == RunStatus.TechRerolled);
        var reasons = replayed.Players[s.PlayerId("Вася")].Exclusions.Select(x => x.Reason).Order().ToList();
        Assert.Equal([ExclusionReason.Dropped, ExclusionReason.TechRerolled], reasons);
    }

    [Fact]
    public void Folding_the_conversion_events_alone_updates_the_run_and_the_exclusion()
    {
        var s = Walk(Season(), "Вася", 2, 2, 2, 1);
        Playing(s, "Вася");
        var runId = TechRerolled(s);
        var before = s.State;

        s.NextRandom(3, 2);
        Convert(s, runId);

        Assert.Equal(s.State, s.Last.Events.Aggregate(before, SeasonEngine.Apply));
    }

    [Fact]
    public void Same_seed_gives_the_same_new_roll_after_a_tech_reroll()
    {
        static Scenario Play()
        {
            var s = Playing(Season(), "Вася");
            return TechReroll(s, "Вася");
        }

        Assert.Equal(Play().Log, Play().Log);
    }
}

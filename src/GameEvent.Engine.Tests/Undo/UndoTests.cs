using FsCheck.Xunit;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Undo;

/// <summary>
/// Undoing a whole command (SPEC «Откат действий», L3, invariant 13, D-20, D-104): what it touched gets its earlier values
/// back; while later commands touched the same players, runs, effects, games, season fields or finish order, the undo is
/// refused and names them.
/// </summary>
public class UndoTests
{
    private static Scenario Season()
    {
        var s = Scenario.New().WithCategory("Horror");
        foreach (var title in new[] { "Silent Hill", "Alan Wake", "Dead Space", "Outlast", "Amnesia", "Soma", "Prey", "Doom" })
        {
            s.WithGame(title, 6, "Horror");
        }

        return s.WithPlayers("Вася", "Петя", "Маша");
    }

    private static UndoCommand Undo(Guid commandId) => new(commandId, "Ошибка админа");

    // The season as it matters to the game: the counters of finishes and points changes only grow, and the tick numbers
    // in players follow them (D-104)
    private static SeasonState Comparable(SeasonState state) =>
        state with
        {
            FinishesSoFar = 0,
            PointsChanges = 0,
            Players = state.Players.SetItems(state.Players.Select(p => KeyValuePair.Create(p.Key, p.Value with { PointsTick = 0 }))),
        };

    [Fact]
    public void Undoing_an_adjustment_gives_the_points_back()
    {
        var s = Season();
        var before = s.State;
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Бонус", PointsDelta: 5, CoinsDelta: 3));
        var adjustment = s.LastCommandId;

        s.Act(Undo(adjustment));

        ScenarioAssert.Accepted(s);
        var undone = Assert.IsType<CommandUndone>(Assert.Single(s.Last.Events));
        Assert.Equal((adjustment, "Ошибка админа"), (undone.CommandId, undone.Comment));
        Assert.Equal(before.Players[s.PlayerId("Вася")], s.Player("Вася"));
        Assert.Equal(Comparable(before), Comparable(s.State));
    }

    [Fact]
    public void Undoing_a_completion_takes_back_dice_points_cells_coins_and_its_effects()
    {
        var s = Season();
        s.Roll("Вася").Start("Вася");
        var before = s.State;
        s.NextRandom(3, 4).Complete("Вася");
        var completion = s.LastCommandId;
        Assert.NotEqual(before.Players[s.PlayerId("Вася")], s.Player("Вася"));

        s.Act(Undo(completion));

        ScenarioAssert.Accepted(s);
        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.Equal(RunStatus.Playing, s.State.Runs[s.Player("Вася").ActiveRunId!.Value].Status);
    }

    [Fact]
    public void Undoing_the_start_of_a_run_removes_the_run_and_brings_the_offer_back()
    {
        var s = Season();
        s.Roll("Вася");
        var before = s.State;
        s.Start("Вася");
        var start = s.LastCommandId;
        var runId = s.Player("Вася").ActiveRunId!.Value;

        s.Act(Undo(start));

        ScenarioAssert.Accepted(s);
        Assert.Equal([runId], Assert.IsType<CommandUndone>(Assert.Single(s.Last.Events)).RemovedRuns);
        Assert.False(s.State.Runs.ContainsKey(runId));
        Assert.Equal(TurnPhase.Rolling, s.Player("Вася").Phase);
        Assert.Equal(Comparable(before), Comparable(s.State));
    }

    [Fact]
    public void Undoing_an_added_player_removes_them()
    {
        var s = Season();
        var before = s.State;
        s.Act(new AddSeasonPlayer(SequentialIds.Make(0x50000000, 9), SequentialIds.Make(0x51000000, 9), "Лёша"));
        var added = s.LastCommandId;

        s.Act(Undo(added));

        ScenarioAssert.Accepted(s);
        Assert.Equal(Comparable(before), Comparable(s.State));
    }

    [Fact]
    public void Undoing_a_ruleset_change_brings_the_rules_back_as_a_new_version()
    {
        var s = Season();
        var rules = s.State.Rules;
        s.Act(new ChangeRuleset(rules with { Roll = rules.Roll with { FreeRerollsPerRoll = rules.Roll.FreeRerollsPerRoll + 1 } }));
        ScenarioAssert.Accepted(s);
        var change = s.LastCommandId;
        var version = s.State.RulesetVersion;

        s.Act(Undo(change));

        ScenarioAssert.Accepted(s);
        Assert.Equal(rules, s.State.Rules);
        Assert.Equal(version + 1, s.State.RulesetVersion);
    }

    [Fact]
    public void Undoing_an_early_close_reopens_the_season()
    {
        var s = Season();
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        var close = s.LastCommandId;

        s.Act(Undo(close));

        ScenarioAssert.Accepted(s);
        Assert.Equal(SeasonStatus.Active, s.State.Status);
    }

    // ---- Dependents ----

    [Fact]
    public void A_later_command_on_the_same_player_blocks_the_undo_and_is_named()
    {
        var s = Season();
        s.Roll("Вася").Start("Вася").NextRandom(3, 4).Complete("Вася");
        var completion = s.LastCommandId;
        s.Roll("Вася");
        var roll = s.LastCommandId;
        var before = s.State;

        s.Act(Undo(completion));

        Assert.False(s.Last.IsAccepted);
        Assert.Equal(RejectionCodes.UndoDependents, s.Last.Rejection!.Code);
        Assert.Equal([roll], s.Last.Rejection.Related);
        Assert.Equal(before, s.State);
    }

    [Fact]
    public void Undoing_the_dependents_in_reverse_order_opens_the_way()
    {
        var s = Season();
        s.Roll("Вася").Start("Вася");
        var before = s.State;
        s.NextRandom(3, 4).Complete("Вася");
        var completion = s.LastCommandId;
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Бонус", PointsDelta: 2));
        var bonus = s.LastCommandId;

        s.Act(Undo(completion));
        Assert.Equal([bonus], s.Last.Rejection!.Related);

        s.Act(Undo(bonus));
        ScenarioAssert.Accepted(s);
        s.Act(Undo(completion));

        ScenarioAssert.Accepted(s);
        Assert.Equal(Comparable(before), Comparable(s.State));
    }

    [Fact]
    public void Another_players_commands_do_not_block()
    {
        var s = Season();
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Бонус", PointsDelta: 5));
        var bonus = s.LastCommandId;
        s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Бонус", PointsDelta: 7));

        s.Act(Undo(bonus));

        ScenarioAssert.Accepted(s);
        Assert.Equal(0, s.Player("Вася").Points);
        Assert.Equal(7, s.Player("Петя").Points);
    }

    [Fact]
    public void A_later_roll_that_saw_the_game_blocks_the_undo_of_its_run()
    {
        // Вася completes Silent Hill, the only game; Петя's roll then meets it as a miss (the game is taken)
        var s = Scenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Alan Wake", 6, "Horror")
            .WithPlayers("Вася", "Петя");
        s.Roll("Вася").Start("Вася").NextRandom(3, 4).Complete("Вася");
        var completion = s.LastCommandId;
        var game = s.State.Runs.Values.Single().GameId;
        s.Act(new RollGame(s.PlayerId("Петя")));
        ScenarioAssert.Accepted(s);
        var rolled = Assert.IsType<GameRolled>(Assert.Single(s.Last.Events));
        var roll = s.LastCommandId;

        s.Act(Undo(completion));

        // The roll depends on the run only when it saw its game
        if (rolled.GameId == game || rolled.Misses.Any(m => m.GameId == game))
        {
            Assert.Equal([roll], s.Last.Rejection!.Related);
        }
        else
        {
            ScenarioAssert.Accepted(s);
        }
    }

    [Fact]
    public void An_undone_dependent_no_longer_blocks()
    {
        var s = Season();
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Бонус", PointsDelta: 5));
        var first = s.LastCommandId;
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Бонус", PointsDelta: 3));
        var second = s.LastCommandId;
        s.Act(Undo(second));
        ScenarioAssert.Accepted(s);

        s.Act(Undo(first));

        ScenarioAssert.Accepted(s);
        Assert.Equal(0, s.Player("Вася").Points);
    }

    // ---- Reads the reviews of C12a found: a later command decided on what the undone one changed ----

    [Fact]
    public void A_change_of_the_season_makes_every_later_command_depend_on_it()
    {
        // The deadline moved, then a roll: the roll was allowed by the moved deadline
        var s = Season();
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromDays(3)));
        var deadline = s.LastCommandId;
        s.Roll("Петя");
        var roll = s.LastCommandId;

        s.Act(Undo(deadline));

        Assert.Equal(RejectionCodes.UndoDependents, s.Last.Rejection!.Code);
        Assert.Equal([roll], s.Last.Rejection.Related);
    }

    [Fact]
    public void Starting_the_season_cannot_be_undone_once_somebody_played()
    {
        var s = Scenario.New().AsDraft().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася");
        s.Act(new ChangeSeasonStatus(SeasonStatus.Active));
        var start = s.LastCommandId;
        s.Roll("Вася");

        s.Act(Undo(start));

        Assert.Equal(RejectionCodes.UndoDependents, s.Last.Rejection!.Code);
    }

    [Fact]
    public void A_deadline_that_has_passed_is_not_brought_back()
    {
        var s = Season();
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromHours(1)));
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromDays(5)));
        var moved = s.LastCommandId;
        s.Advance(TimeSpan.FromHours(2));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Undo(moved)), RejectionCodes.SeasonDeadlineInPast);
    }

    [Fact]
    public void A_season_closed_at_its_deadline_stays_closed()
    {
        var s = Season();
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromHours(1)));
        s.Advance(TimeSpan.FromHours(1));
        s.Act(new ReachDeadline());
        ScenarioAssert.Accepted(s);
        var reached = s.LastCommandId;

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Undo(reached)), RejectionCodes.SeasonDeadlinePassed);
    }

    [Fact]
    public void An_early_close_is_not_undone_after_the_deadline()
    {
        var s = Season();
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromHours(1)));
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        var close = s.LastCommandId;
        s.Advance(TimeSpan.FromHours(2));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Undo(close)), RejectionCodes.SeasonDeadlinePassed);
    }

    [Fact]
    public void Undoing_a_choice_is_refused_once_another_player_took_a_freed_game()
    {
        // Вася picks one of two; the other is free again and Петя's roll can take it
        var s = Scenario.New().WithRuleset(r => r with { Roll = r.Roll with { ChoiceCount = 2 } }).WithCategory("Horror")
            .WithGame("Silent Hill", 6, "Horror").WithGame("Alan Wake", 6, "Horror").WithPlayers("Вася", "Петя");
        s.Roll("Вася");
        var choice = s.Player("Вася").Choice!;
        s.Act(new Engine.Turns.MakeChoice(s.PlayerId("Вася"), choice.ChoiceId, choice.Options[0].Id));
        ScenarioAssert.Accepted(s);
        var made = s.LastCommandId;
        var freed = choice.Options[1].Game!.GameId;
        s.Roll("Петя");
        Assert.Equal(freed, s.Player("Петя").Offer!.GameId);
        var roll = s.LastCommandId;

        s.Act(Undo(made));

        Assert.Equal(RejectionCodes.UndoDependents, s.Last.Rejection!.Code);
        Assert.Equal([roll], s.Last.Rejection.Related);
    }

    [Fact]
    public void A_witness_added_later_blocks_the_undo_of_their_arrival()
    {
        var s = Season();
        var lyosha = SequentialIds.Make(0x50000000, 9);
        s.Act(new AddSeasonPlayer(lyosha, SequentialIds.Make(0x51000000, 9), "Лёша"));
        var added = s.LastCommandId;
        s.Roll("Вася").Start("Вася").NextRandom(3, 4).Complete("Вася");
        var runId = s.State.Runs.Values.Single().RunId;
        s.Act(new Engine.Proofs.SubmitProof(s.PlayerId("Вася"), runId, [], null, lyosha));
        ScenarioAssert.Accepted(s);
        var proof = s.LastCommandId;

        s.Act(Undo(added));

        Assert.Contains(proof, s.Last.Rejection!.Related);
    }

    [Fact]
    public void Undoing_an_approval_of_the_first_is_refused_once_the_first_was_frozen()
    {
        // The first finisher has two runs up to the finish; the second approval freezes him — the first approval cannot
        // go back to «waiting» under a frozen first (Q-3)
        var s = Scenario.New().WithRuleset(r => r with { Map = r.Map with { LinearLength = 4 } }).WithCategory("Horror")
            .WithGame("Silent Hill", 6, "Horror").WithGame("Alan Wake", 6, "Horror").WithPlayers("Вася", "Петя");
        s.Roll("Вася").Start("Вася").NextRandom(1, 1).Complete("Вася");
        var first = s.State.Runs.Values.Single().RunId;
        s.Roll("Вася").Start("Вася").NextRandom(4, 4).Complete("Вася");
        var second = s.State.Runs.Values.Single(r => r.RunId != first).RunId;
        Assert.NotNull(s.Player("Вася").Finish);
        s.Act(new Engine.Proofs.ApproveProof(first, null, "Видел"));
        ScenarioAssert.Accepted(s);
        var approval = s.LastCommandId;
        s.Act(new Engine.Proofs.ApproveProof(second, null, "Видел"));
        ScenarioAssert.Accepted(s);
        Assert.True(s.Player("Вася").Finish!.Frozen);
        var freeze = s.LastCommandId;

        s.Act(Undo(approval));

        Assert.Equal(RejectionCodes.UndoDependents, s.Last.Rejection!.Code);
        Assert.Equal([freeze], s.Last.Rejection.Related);
    }

    [Fact]
    public void A_later_command_that_repeated_the_change_blocks_the_undo()
    {
        // BUGS.md C13-1 (found by the long run): the same proof sent twice — the second changes nothing in the state, yet its
        // event stands in the log. Undoing the first proof wiped the second one with it, and the log replayed without the
        // first no longer gave the season (invariant 13).
        var s = Season();
        s.Roll("Вася").Start("Вася").NextRandom(3, 4).Complete("Вася");
        var runId = s.State.Runs.Values.Single().RunId;
        s.Act(new Engine.Proofs.SubmitProof(s.PlayerId("Вася"), runId, ["https://imgur.com/a/credits"]));
        ScenarioAssert.Accepted(s);
        var first = s.LastCommandId;
        s.Act(new Engine.Proofs.SubmitProof(s.PlayerId("Вася"), runId, ["https://imgur.com/a/credits"]));
        ScenarioAssert.Accepted(s);
        var second = s.LastCommandId;

        s.Act(Undo(first));

        Assert.Equal(RejectionCodes.UndoDependents, s.Last.Rejection?.Code);
        Assert.Equal([second], s.Last.Rejection!.Related);
    }

    [Fact]
    public void Without_the_history_an_undo_is_refused()
    {
        var s = Season();
        var context = s.Context() with { History = null };

        var result = SeasonEngine.Execute(s.State, Undo(Guid.NewGuid()), context);

        Assert.Equal(RejectionCodes.UndoNoHistory, result.Rejection!.Code);
    }

    // ---- Refusals ----

    [Fact]
    public void An_unknown_command_is_refused()
    {
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Undo(Guid.NewGuid())), RejectionCodes.UndoUnknownCommand);
    }

    [Fact]
    public void The_season_creation_is_not_undone()
    {
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Undo(x.History[0].CommandId)), RejectionCodes.UndoNotUndoable);
    }

    [Fact]
    public void An_undo_is_not_undone()
    {
        var s = Season();
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Бонус", PointsDelta: 5));
        s.Act(Undo(s.LastCommandId));
        var undo = s.LastCommandId;

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Undo(undo)), RejectionCodes.UndoNotUndoable);
    }

    [Fact]
    public void A_command_is_undone_once()
    {
        var s = Season();
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Бонус", PointsDelta: 5));
        var bonus = s.LastCommandId;
        s.Act(Undo(bonus));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Undo(bonus)), RejectionCodes.UndoAlreadyUndone);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void An_undo_explains_itself(string comment)
    {
        var s = Season();
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Бонус", PointsDelta: 5));
        var bonus = s.LastCommandId;

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new UndoCommand(bonus, comment)), RejectionCodes.CommentRequired);
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new UndoCommand(bonus, new string('я', Limits.MaxCommentLength + 1))), RejectionCodes.CommentTooLong);
    }

    [Fact]
    public void Nothing_is_undone_after_the_finish()
    {
        var s = Season();
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Бонус", PointsDelta: 5));
        var bonus = s.LastCommandId;
        s.MoveStatusTo(SeasonStatus.Finished);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Undo(bonus)), RejectionCodes.SeasonClosed);
    }

    [Fact]
    public void The_log_with_an_undo_replays_to_the_same_state()
    {
        var s = Season();
        s.Roll("Вася").Start("Вася").NextRandom(3, 4).Complete("Вася");
        s.Act(Undo(s.LastCommandId));

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log.Select(e => EventCodec.Decode(EventCodec.Encode(e)))));
    }

    [Fact]
    public void Game_ids_are_found_however_deep()
    {
        var game = Guid.NewGuid();
        var missed = Guid.NewGuid();
        var rolled = new GameRolled(Guid.NewGuid(), "Horror", [new RollMiss(missed, RollMissReason.CompletedInSeason, Guid.NewGuid())], game, null!, DateTimeOffset.UnixEpoch);

        Assert.Equal(new[] { ("game", missed), ("game", game) }.Order(), GameIds.Of(rolled).Order());
        Assert.Equal([("game", game)], GameIds.Of(new GameRerolled(Guid.NewGuid(), [game], RerollPayment.FreeThisRoll)));

        var witness = Guid.NewGuid();
        var proof = new Engine.Proofs.ProofSubmitted(Guid.NewGuid(), Guid.NewGuid(), [], null, witness, DateTimeOffset.UnixEpoch, []);
        Assert.Equal([("player", witness)], GameIds.Of(proof));
    }

    // Invariant 13: an undo without dependents leaves the season as if the command had never been — the same as the log
    // replayed without it (up to the counters that only grow); an undo with dependents changes nothing and names only
    // commands that are later, not undone, not undos
    [Property(MaxTest = 150)]
    public void An_accepted_undo_equals_the_log_without_the_command(byte[] script) =>
        Season().Explained(s => PlayUndos(s, script));

    private static void PlayUndos(Scenario s, byte[] script)
    {
        string[] names = ["Вася", "Петя", "Маша"];
        foreach (var b in script.Take(60))
        {
            var player = s.PlayerId(names[b % 3]);
            var completed = s.State.Runs.Values.Where(r => r.Status == RunStatus.Completed).ToList();
            var run = completed.Count == 0 ? Guid.NewGuid() : completed[b % completed.Count].RunId;
            ICommand command = (b / 3 % 14) switch
            {
                0 or 7 => new RollGame(player),
                1 or 8 => new StartRun(player),
                2 or 9 => new CompleteRun(player, Difficulty.Normal),
                3 => new AdjustPlayer(player, "Бонус", PointsDelta: (b % 5) - 2, CoinsDelta: b % 2, DiscardOffer: b % 4 == 3),
                4 => new DropRun(player),
                5 => new SetPlayerInactive(player, b % 2 == 0),
                10 => new Reroll(player),
                11 => (b % 4) switch
                {
                    0 => new Engine.Proofs.SubmitProof(player, run, ["https://imgur.com/a/credits"]),
                    1 => new Engine.Proofs.ApproveProof(run, null, "Видел"),
                    2 => new Engine.Proofs.RejectProof(run, "Нет пруфа"),
                    _ => new CorrectRunHours(run, 3 + (b % 9), "Часы по пруфу"),
                },
                12 => (b % 3) switch
                {
                    0 => new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromDays(1 + (b % 5))),
                    1 => new ChangeSeasonStatus(SeasonStatus.Closing),
                    _ => new AdjustPlayer(player, "Переход", CellId: "c3"),
                },
                _ => Undo(s.History[b % s.History.Count].CommandId),
            };
            var before = s.State;
            var history = s.History.ToList();
            s.Act(command);
            if (command is not UndoCommand undo)
            {
                continue;
            }

            if (!s.Last.IsAccepted)
            {
                Assert.Equal(before, s.State);
                if (s.Last.Rejection!.Code == RejectionCodes.UndoDependents)
                {
                    var targetIndex = history.FindIndex(c => c.CommandId == undo.TargetCommandId);
                    var undone = Undoing.UndoneCommands(history);
                    Assert.All(s.Last.Rejection.Related, id =>
                    {
                        var at = history.FindIndex(c => c.CommandId == id);
                        Assert.True(at > targetIndex && !undone.Contains(id) && !history[at].Events.Any(e => e is CommandUndone));
                    });
                }

                continue;
            }

            CheckRules(s.State);
            var skipped = new HashSet<Guid>(Undoing.UndoneCommands(s.History));
            var expected = SeasonEngine.Replay(s.History
                .Where(c => !skipped.Contains(c.CommandId) && !c.Events.Any(e => e is CommandUndone))
                .SelectMany(c => c.Events));
            Assert.Equal(Comparable(expected) with { RulesetVersion = 0 }, Comparable(s.State) with { RulesetVersion = 0 });
        }

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    // What the rules forbid, checked on the state an undo leaves (reviews of C12a: a later command may have decided on
    // what the undone one changed)
    private static void CheckRules(SeasonState state)
    {
        // A game is held by one player at a time: offered, among the options of a choice, or played (D-06, G9)
        var held = state.Players.Values
            .SelectMany(p => new[] { p.Offer?.GameId }.Concat(p.Choice?.Options.Select(o => o.Game?.GameId) ?? []))
            .Concat(state.Runs.Values.Where(r => r.Status == RunStatus.Playing).Select(r => (Guid?)r.GameId))
            .OfType<Guid>()
            .ToList();
        Assert.Equal(held.Count, held.Distinct().Count());

        // Nothing is played in a draft
        Assert.True(state.Status != SeasonStatus.Draft || state.Runs.Count == 0, "Runs in a draft.");

        // The phase follows the active run
        foreach (var p in state.Players.Values)
        {
            Assert.Equal(p.Phase == TurnPhase.Playing, p.ActiveRunId is { } active && state.Runs[active].Status == RunStatus.Playing);
        }

        // A frozen first has every run up to the finish approved (Q-3)
        foreach (var p in state.Players.Values.Where(p => p.Finish?.Frozen == true && state.Rules.Finish.RequireApprovalForFirst))
        {
            Assert.All(
                state.Runs.Values.Where(r => r.PlayerId == p.PlayerId && r.Status == RunStatus.Completed && !r.AfterFinish),
                r => Assert.Equal(Engine.Proofs.ProofStatus.Approved, r.Proof?.Status));
        }
    }
}

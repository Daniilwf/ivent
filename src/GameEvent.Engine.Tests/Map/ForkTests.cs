using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Map;

/// <summary>
/// Forks (SPEC «Движение», «Карта»; D-304, D-305): the player's own move stops at a fork with steps left and waits for
/// the branch, kept on the server; several forks in one throw are several choices in a row; forced moves take the
/// default branch.
/// </summary>
public class ForkTests
{
    /// <summary>
    /// <code>
    /// start → a → f → b1 → j → k → l → m → finish
    ///             f → c1 ↗                (b1 is the default branch, b1 → j the primary entry of j)
    /// </code>
    /// </summary>
    private static MapGraph ForkMap() =>
        MapBuilder.New().Path("start", "a", "f", "b1", "j", "k", "l", "m", "finish").Path("f", "c1", "j").Build();

    /// <summary>Вася plays «Silent Hill», 9 hours: three d4 at the normal difficulty; the other games are 3 hours, one d4.</summary>
    private static Scenario Playing(MapGraph? map = null) =>
        Pool(map).RollTitle("Вася", "Silent Hill").Start("Вася");

    private static Scenario Pool(MapGraph? map = null) =>
        Scenario.New().WithMap(map ?? ForkMap())
            .WithCategory("Horror")
            .WithGame("Silent Hill", 9, "Horror").WithGame("Fatal Frame", 3, "Horror").WithGame("Siren", 3, "Horror").WithGame("Kuon", 3, "Horror")
            .WithPlayers("Вася", "Петя");

    private static Guid RunOf(Scenario s, string player) =>
        s.State.Runs.Values.Where(r => r.PlayerId == s.PlayerId(player)).OrderBy(r => r.StartedAt).Last().RunId;

    [Fact]
    public void Own_move_reaching_a_fork_with_steps_left_waits_for_the_branch()
    {
        var s = Playing();
        var vasya = s.PlayerId("Вася");

        // 2 + 2 + 2 = 6 steps: a, f — and 4 left at the fork
        s.NextRandom(2, 2, 2).Complete("Вася");

        ScenarioAssert.Accepted(s);
        var run = RunOf(s, "Вася");
        Assert.Equal(
            new PlayerMoved(vasya, "start", "f", 6, ["a", "f"], MoveReason.CompletionRoll, run, Paused: true),
            Assert.Single(s.LastEvents<PlayerMoved>()));
        var requested = Assert.Single(s.LastEvents<BranchChoiceRequested>());
        Assert.Equal(new BranchChoiceRequested(vasya, requested.ChoiceId, "f", ["b1", "c1"], 4, MoveReason.CompletionRoll, run), requested);

        // The points are the whole throw; the choice waits on the server
        Assert.Equal(6, s.Player("Вася").Points);
        Assert.Equal("f", s.Player("Вася").CellId);
        var choice = s.Player("Вася").Choice!;
        Assert.Equal(ChoiceKind.Branch, choice.Kind);
        Assert.Equal(["b1", "c1"], choice.Options.Select(o => o.Id));
        Assert.All(choice.Options, o => Assert.Null(o.Game));
        Assert.Equal(new PendingMove(4, MoveReason.CompletionRoll, run), choice.Move);
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
    }

    [Fact]
    public void Chosen_branch_takes_the_steps_left()
    {
        var s = Playing();
        s.NextRandom(2, 2, 2).Complete("Вася");
        var choiceId = s.Player("Вася").Choice!.ChoiceId;
        var run = RunOf(s, "Вася");

        s.ChooseBranch("Вася", "c1");

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [
                new ChoiceMade(s.PlayerId("Вася"), choiceId, "c1"),
                new PlayerMoved(s.PlayerId("Вася"), "f", "l", 4, ["c1", "j", "k", "l"], MoveReason.CompletionRoll, run),
            ],
            s.Last.Events);
        Assert.Null(s.Player("Вася").Choice);
        Assert.Equal("l", s.Player("Вася").CellId);
        Assert.Equal(6, s.Player("Вася").Points);

        // One segment walked edge by edge through the fork; the run moved all six cells
        Assert.Equal(["start", "a", "f", "c1", "j", "k", "l"], Assert.Single(s.Player("Вася").Path.Segments).Cells);
        Assert.Equal(6, s.State.Runs[run].Moved);
    }

    [Fact]
    public void Default_branch_is_chosen_like_any_other()
    {
        var s = Playing();
        s.NextRandom(2, 2, 2).Complete("Вася");

        s.ChooseBranch("Вася", "b1");

        Assert.Equal(["b1", "j", "k", "l"], Assert.Single(s.LastEvents<PlayerMoved>()).Path);
    }

    [Fact]
    public void Several_forks_in_one_throw_are_several_choices_in_a_row()
    {
        // start → f1 → (x1 | y1) → f2 → (x2 | y2) → m → finish
        var map = MapBuilder.New().Path("start", "f1", "x1", "f2", "x2", "m", "n", "finish").Path("f1", "y1", "f2").Path("f2", "y2", "m").Build();
        var s = Playing(map);

        s.NextRandom(2, 2, 1).Complete("Вася");
        Assert.Equal("f1", s.Player("Вася").CellId);
        Assert.Equal(4, s.Player("Вася").Choice!.Move!.Steps);

        s.ChooseBranch("Вася", "y1");
        ScenarioAssert.Accepted(s);
        Assert.Equal(new PlayerMoved(s.PlayerId("Вася"), "f1", "f2", 4, ["y1", "f2"], MoveReason.CompletionRoll, RunOf(s, "Вася"), Paused: true), Assert.Single(s.LastEvents<PlayerMoved>()));
        var second = Assert.Single(s.LastEvents<BranchChoiceRequested>());
        Assert.Equal(("f2", 2), (second.CellId, second.Steps));

        s.ChooseBranch("Вася", "y2");
        ScenarioAssert.Accepted(s);
        Assert.Equal("m", s.Player("Вася").CellId);
        Assert.Null(s.Player("Вася").Choice);
        Assert.Equal(["start", "f1", "y1", "f2", "y2", "m"], Assert.Single(s.Player("Вася").Path.Segments).Cells);
    }

    [Fact]
    public void Move_ending_on_the_fork_stops_there_and_the_next_throw_asks_first()
    {
        var s = Pool();
        s.RollTitle("Вася", "Fatal Frame").Start("Вася").NextRandom(2).Complete("Вася");
        Assert.Equal("f", s.Player("Вася").CellId);
        Assert.Null(s.Player("Вася").Choice);
        Assert.False(Assert.Single(s.LastEvents<PlayerMoved>()).Paused);
        Assert.Empty(s.LastEvents<BranchChoiceRequested>());

        // Standing on the fork, the next throw moves nothing before the choice
        s.RollTitle("Вася", "Siren").Start("Вася").NextRandom(3).Complete("Вася");
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal(("f", 3), (Assert.Single(s.LastEvents<BranchChoiceRequested>()).CellId, s.Player("Вася").Choice!.Move!.Steps));

        s.ChooseBranch("Вася", "c1");
        Assert.Equal(["c1", "j", "k"], Assert.Single(s.LastEvents<PlayerMoved>()).Path);
    }

    [Fact]
    public void Pending_branch_blocks_the_next_roll_but_not_the_others()
    {
        var s = Playing();
        s.NextRandom(2, 2, 2).Complete("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.ChoicePending);

        s.RollTitle("Петя", "Fatal Frame");
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Unknown_branch_or_a_stale_choice_is_refused()
    {
        var s = Playing();
        s.NextRandom(2, 2, 2).Complete("Вася");
        var choice = s.Player("Вася").Choice!;

        ScenarioAssert.RejectsWithoutChanges(s, x => x.ChooseBranch("Вася", "k"), RejectionCodes.UnknownChoiceOption);
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new MakeChoice(x.PlayerId("Вася"), Guid.Parse("00000000-0000-0000-0000-000000000009"), "b1")), RejectionCodes.NoPendingChoice);
        Assert.Equal(choice, s.Player("Вася").Choice);
    }

    [Fact]
    public void Branch_is_chosen_after_the_deadline_and_while_closing()
    {
        // D-305: the throw came before the deadline, its steps are part of it
        var s = Playing();
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow.AddHours(1)));
        s.NextRandom(2, 2, 2).Complete("Вася");
        s.Advance(TimeSpan.FromHours(2));
        s.Act(new ReachDeadline());
        Assert.Equal(SeasonStatus.Closing, s.State.Status);

        s.ChooseBranch("Вася", "c1");

        ScenarioAssert.Accepted(s);
        Assert.Equal("l", s.Player("Вася").CellId);
    }

    [Fact]
    public void Season_does_not_finish_while_a_branch_waits()
    {
        // D-305: the steps at the fork belong to a throw made before the deadline and may still reach the finish
        var s = Playing();
        s.NextRandom(2, 2, 2).Complete("Вася");
        s.Act(new ApproveProof(RunOf(s, "Вася"), Comment: "видел"));
        s.MoveStatusTo(SeasonStatus.Closing);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ChangeSeasonStatus(SeasonStatus.Finished)), RejectionCodes.BranchChoicePending);

        // Chosen (or discarded by the admin), the season finishes; no branch is chosen after that
        s.ChooseBranch("Вася", "c1");
        s.MoveStatusTo(SeasonStatus.Finished);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new MakeChoice(x.PlayerId("Вася"), Guid.Empty, "b1")), RejectionCodes.SeasonNotActive);
    }

    [Fact]
    public void Undo_that_would_bring_back_a_branch_the_map_no_longer_has_is_refused()
    {
        // The choice was made, then a map changed the fork's exits: the old choice must not come back
        var s = Playing();
        s.NextRandom(2, 2, 2).Complete("Вася");
        s.ChooseBranch("Вася", "c1");
        var chosen = s.LastCommandId;
        var rerouted = MapBuilder.New().Path("start", "a", "f", "b1", "j", "k", "l", "m", "finish").Path("f", "c1", "j").Path("f", "d1", "j").Build();
        s.Act(new PublishMap(rerouted, "Третья ветка"));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new Engine.Undo.UndoCommand(chosen, "Назад")), RejectionCodes.UndoCellNotOnMap);
    }

    [Fact]
    public void Branch_leading_to_the_finish_finishes_and_burns_the_rest()
    {
        var map = MapBuilder.New().Path("start", "f", "b", "c", "finish").Path("f", "finish").Build();
        var s = Playing(map);
        s.NextRandom(2, 2, 2).Complete("Вася");

        s.ChooseBranch("Вася", "finish");

        ScenarioAssert.Accepted(s);
        Assert.Equal("finish", s.Player("Вася").CellId);
        var finished = Assert.Single(s.LastEvents<Engine.Finish.PlayerFinished>());
        Assert.Equal(4, finished.Surplus);
        Assert.True(s.State.Runs[RunOf(s, "Вася")].ReachedFinish);
    }

    [Fact]
    public void Forced_move_forward_takes_the_default_branch_without_asking()
    {
        // SPEC «Чужой толчок через развилку идёт по ветке по умолчанию»: here the admin adds hours to a run
        var s = Pool();
        s.RollTitle("Вася", "Fatal Frame").Start("Вася").NextRandom(2).Complete("Вася");
        Assert.Equal("f", s.Player("Вася").CellId);

        // 9 hours instead of 3: two more d4 showing 1 + 2
        s.NextRandom(1, 2).Act(new CorrectRunHours(RunOf(s, "Вася"), 9, "часы"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(["b1", "j", "k"], Assert.Single(s.LastEvents<PlayerMoved>()).Path);
        Assert.Null(s.Player("Вася").Choice);
    }

    [Fact]
    public void Reject_or_correction_waits_while_the_player_chooses_a_branch()
    {
        // D-305: the steps left lead from where the player stands
        var s = Playing();
        s.NextRandom(2, 2, 2).Complete("Вася");
        var run = RunOf(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new RejectProof(run, "нет пруфа")), RejectionCodes.BranchChoicePending);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.NextRandom(1).Act(new CorrectRunHours(run, 12, "часы")), RejectionCodes.BranchChoicePending);

        // Even a correction that would move nothing: the steps waiting at the fork come from the run's dice
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new CorrectRunHours(run, 3, "часы")), RejectionCodes.BranchChoicePending);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ChangeRunDifficulty(run, Difficulty.Easy, "лёгкая")), RejectionCodes.BranchChoicePending);

        // Approving it is fine
        s.Act(new ApproveProof(run, Comment: "видел"));
        ScenarioAssert.Accepted(s);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new AdjustPlayer(x.PlayerId("Вася"), "Перенос", CellId: "k")), RejectionCodes.BranchChoicePending);
    }

    [Fact]
    public void Admin_discards_a_branch_choice_and_the_steps_left_burn()
    {
        var s = Playing();
        s.NextRandom(2, 2, 2).Complete("Вася");
        var choice = s.Player("Вася").Choice!;

        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Игрок пропал", DiscardOffer: true));

        ScenarioAssert.Accepted(s);
        Assert.Contains(new ChoiceDiscarded(s.PlayerId("Вася"), choice.ChoiceId), s.Last.Events);
        Assert.Null(s.Player("Вася").Choice);
        Assert.Equal(("f", 6), (s.Player("Вася").CellId, s.Player("Вася").Points));

        // Then the reject goes through and takes back what the run moved
        s.Act(new RejectProof(RunOf(s, "Вася"), "нет пруфа"));
        ScenarioAssert.Accepted(s);
        Assert.Equal("start", s.Player("Вася").CellId);
    }

    [Fact]
    public void Discard_with_a_move_in_the_same_adjustment_is_allowed()
    {
        var s = Playing();
        s.NextRandom(2, 2, 2).Complete("Вася");

        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Сам решу", CellId: "k", DiscardOffer: true));

        ScenarioAssert.Accepted(s);
        Assert.Equal("k", s.Player("Вася").CellId);
    }

    [Fact]
    public void Undo_of_the_completion_takes_the_pending_branch_back_too()
    {
        var s = Playing();
        s.NextRandom(2, 2, 2).Complete("Вася");
        var completion = s.LastCommandId;

        s.Act(new Engine.Undo.UndoCommand(completion, "Ошибка"));

        ScenarioAssert.Accepted(s);
        Assert.Null(s.Player("Вася").Choice);
        Assert.Equal("start", s.Player("Вася").CellId);
        Assert.Equal(TurnPhase.Playing, s.Player("Вася").Phase);
    }

    [Fact]
    public void Undo_of_the_branch_choice_brings_the_choice_back()
    {
        var s = Playing();
        s.NextRandom(2, 2, 2).Complete("Вася");
        var choice = s.Player("Вася").Choice;
        s.ChooseBranch("Вася", "c1");

        s.Act(new Engine.Undo.UndoCommand(s.LastCommandId, "Не туда"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(choice, s.Player("Вася").Choice);
        Assert.Equal("f", s.Player("Вася").CellId);
    }

    [Fact]
    public void Replaying_the_log_gives_the_same_pending_branch()
    {
        var s = Playing();
        s.NextRandom(2, 2, 2).Complete("Вася");

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log.Select(e => EventCodec.Decode(EventCodec.Encode(e)))));
    }
}

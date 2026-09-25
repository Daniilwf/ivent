using System.Globalization;
using System.Text;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Finish;
using GameEvent.Engine.Tests.Lifecycle;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Snapshots;

/// <summary>
/// C13: the whole event log of eight key scenarios, as the log stores it (type, format version, JSON), is kept next to
/// this file as <c>*.verified.txt</c> (Verify). Any change of what a command writes — an event more or less, a field, an
/// order, a number — shows up as a diff to accept deliberately; events in the log are never rewritten (CLAUDE.md), so an
/// unexpected diff is a bug or needs a new event version. Each stored event must read back to itself.
/// To accept a change: run the tests, look at the <c>*.received.txt</c>, rename it to <c>*.verified.txt</c>.
/// </summary>
public class LogSnapshotTests
{
    static LogSnapshotTests()
    {
        // No diff tool pops up from a test run; the received file is the diff to read
        DiffEngine.DiffRunner.Disabled = true;
    }

    private static Task VerifyLog(Scenario s) => Verifier.Verify(Render(s));

    /// <summary>One block per command: its name, then each event in the stored form.</summary>
    private static string Render(Scenario s)
    {
        // A record prints its dates and numbers in the current culture: the snapshot must read the same on every machine
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return RenderInvariant(s);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    private static string RenderInvariant(Scenario s)
    {
        var text = new StringBuilder();
        for (var i = 0; i < s.History.Count; i++)
        {
            var command = s.Commands[i];
            text.Append("# ").Append(command switch
            {
                null => "(crafted)",
                CreateSeason or ChangeRuleset => command.GetType().Name,
                _ => command.ToString(),
            }).Append('\n');
            foreach (var e in s.History[i].Events)
            {
                var stored = EventCodec.Encode(e);
                Assert.Equal(e, EventCodec.Decode(stored));
                text.Append(stored.Type).Append(" v").Append(stored.Version).Append(' ').Append(stored.Data).Append('\n');
            }
        }

        return text.ToString();
    }

    private static Scenario Season(int mapLength = 60)
    {
        var s = Scenario.New().WithMapLength(mapLength).WithCategory("Horror");
        foreach (var title in new[] { "Silent Hill", "Alan Wake", "Dead Space", "Outlast", "Amnesia", "Soma" })
        {
            s.WithGame(title, 6, "Horror");
        }

        return s;
    }

    private static Guid Completed(Scenario s, string player, int[] dice, Difficulty difficulty = Difficulty.Normal)
    {
        s.Roll(player).Start(player);
        var runId = s.Player(player).ActiveRunId!.Value;
        s.Advance(TimeSpan.FromHours(6)).NextRandom(dice).Complete(player, difficulty);
        s.Advance(TimeSpan.FromHours(1));
        return runId;
    }

    [Fact]
    public Task Full_cycle_with_a_completion()
    {
        // Roll, start, complete with a review, proof, approval
        var s = Season().WithPlayers("Вася", "Петя");
        s.Roll("Вася").Start("Вася").Advance(TimeSpan.FromHours(6)).NextRandom(3, 4)
            .Complete("Вася", Difficulty.Normal, review: new RunReview(9, "Отличная игра"));
        var runId = s.State.Runs.Values.Single().RunId;
        s.Act(new SubmitProof(s.PlayerId("Вася"), runId, ["https://imgur.com/a/credits"], "Титры"));
        s.Act(new ApproveProof(runId));
        Assert.All(s.History, c => Assert.NotEmpty(c.Events));

        return VerifyLog(s);
    }

    [Fact]
    public Task Drop_with_a_penalty()
    {
        // The penalty dice take points and cells back, and a bad event waits for the player
        var s = Season().WithPlayers("Вася");
        Completed(s, "Вася", [4, 4]);
        s.Roll("Вася").Start("Вася").Advance(TimeSpan.FromHours(2)).NextRandom(3, 2).Act(new DropRun(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);

        return VerifyLog(s);
    }

    [Fact]
    public Task Reroll_paid_by_a_bad_event()
    {
        var s = Season()
            .WithRuleset(r => r with { Roll = r.Roll with { FreeRerollsPerRoll = 0, RerollCost = new RerollCost { Kind = RerollCostKind.BadEvent } } })
            .WithPlayers("Вася");
        s.Roll("Вася").Act(new Reroll(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);

        return VerifyLog(s);
    }

    [Fact]
    public Task Proof_reject()
    {
        // The reject takes back the run's points, cells and coins
        var s = Season().WithPlayers("Вася");
        var runId = Completed(s, "Вася", [3, 2]);
        s.Act(new SubmitProof(s.PlayerId("Вася"), runId, ["https://imgur.com/a/other"]));
        s.Act(new RejectProof(runId, "На скрине другая игра"));
        ScenarioAssert.Accepted(s);

        return VerifyLog(s);
    }

    [Fact]
    public Task Finish_freeze_and_a_second_finisher_with_the_bonus()
    {
        // Вася finishes first and is frozen by the approval; Петя finishes second and gets the bonus of the second place
        var s = FinishSetup.New(players: 2);
        FinishSetup.FrozenFirst(s, "Вася");
        FinishSetup.FinishRun(s, "Петя");
        Assert.Equal(2, s.Player("Петя").Finish!.Order);

        return VerifyLog(s);
    }

    [Fact]
    public Task Deadline_and_the_season_finish_with_its_result()
    {
        // A run completed before the deadline, the scheduler closes the season, the proof is checked, the result is written
        var s = LifecycleSetup.New();
        var runId = Completed(s, "Вася", [2, 3]);
        s.Advance(LifecycleSetup.UntilDeadline);
        s.Act(new ReachDeadline());
        ScenarioAssert.Accepted(s);
        s.Act(new ApproveProof(runId, null, "Видел на стриме"));
        s.MoveStatusTo(SeasonStatus.Finished);
        Assert.NotNull(s.State.Result);

        return VerifyLog(s);
    }

    [Fact]
    public Task Undo_of_a_whole_command()
    {
        // A completion undone: the dice, points, cells and coins go back in one compensating event
        var s = Season().WithPlayers("Вася");
        Completed(s, "Вася", [3, 3]);
        var completion = s.History.Last(c => c.Events.Any(e => e is RunCompleted)).CommandId;
        s.Act(new UndoCommand(completion, "Ошибка: не та игра"));
        ScenarioAssert.Accepted(s);

        return VerifyLog(s);
    }

    [Fact]
    public Task Manual_effects_resolved_by_the_player_and_by_the_admin()
    {
        // An extreme completion grants a good event, a drop a bad one; the player applies the first, the admin marks the
        // second as not applicable
        var s = Season().WithPlayers("Вася");
        Completed(s, "Вася", [4, 5], Difficulty.Extreme);
        s.Roll("Вася").Start("Вася").NextRandom(1, 1).Act(new DropRun(s.PlayerId("Вася")));
        var good = s.State.ManualEffects.Values.Single(e => e.DrawEvent == EventKind.Good);
        var bad = s.State.ManualEffects.Values.Single(e => e.DrawEvent == EventKind.Bad);
        s.Act(new ResolveManualEffect(good.EffectId, ManualEffectOutcome.Applied, null, s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);
        s.Act(new ResolveManualEffect(bad.EffectId, ManualEffectOutcome.NotApplicable, "Ивент уже разыгран в чате", null));
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.State.ManualEffects);

        return VerifyLog(s);
    }
}

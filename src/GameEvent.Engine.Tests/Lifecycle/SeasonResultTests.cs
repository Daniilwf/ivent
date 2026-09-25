using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Ranking;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Lifecycle.LifecycleSetup;

namespace GameEvent.Engine.Tests.Lifecycle;

/// <summary>
/// Finishing the season and its result (SE2, SE4, P9; SPEC «Итоги только после проверки всех пруфов», «SeasonResult»;
/// D-101). Closing → Finished only with an empty proof queue — every completed run approved or rejected, a completed run
/// without a proof counts as unchecked (<c>season.proofsPending</c>); the same command writes
/// <see cref="SeasonResultRecorded"/> — the leaderboard at that moment (D-100) — and <see cref="SeasonState.Result"/>
/// keeps it. After Finished nothing changes; Archived is the next step and changes nothing either.
/// </summary>
public class SeasonResultTests
{
    private static Scenario Finish(Scenario s)
    {
        s.Act(new ChangeSeasonStatus(SeasonStatus.Finished));
        ScenarioAssert.Accepted(s);
        return s;
    }

    [Fact]
    public void Finish_is_refused_while_a_submitted_proof_waits()
    {
        var s = New();
        var runId = CompleteRun(s, "Вася", [2, 2]);
        s.Act(new SubmitProof(s.PlayerId("Вася"), runId, ["https://imgur.com/a/credits"]));
        Close(s);

        Refused(s, new ChangeSeasonStatus(SeasonStatus.Finished), RejectionCodes.SeasonProofsPending);
    }

    [Fact]
    public void Finish_is_refused_while_a_completed_run_has_no_proof_checked()
    {
        // No proof submitted at all: the run is still in the admin's queue
        var s = New();
        CompleteRun(s, "Вася", [2, 2]);
        Close(s);

        Refused(s, new ChangeSeasonStatus(SeasonStatus.Finished), RejectionCodes.SeasonProofsPending);
    }

    [Fact]
    public void Finish_is_refused_while_one_of_several_runs_waits()
    {
        var s = New();
        var vasyaRun = CompleteRun(s, "Вася", [2, 2]);
        CompleteRun(s, "Петя", [1, 1]);
        Close(s);
        Approve(s, vasyaRun);
        ScenarioAssert.Accepted(s);

        Refused(s, new ChangeSeasonStatus(SeasonStatus.Finished), RejectionCodes.SeasonProofsPending);
    }

    [Fact]
    public void Finish_records_the_leaderboard_as_the_result()
    {
        // Вася 4 points (approved), Петя's run rejected, Маша 3 from the admin
        var s = New();
        var vasyaRun = CompleteRun(s, "Вася", [2, 2]);
        var petyaRun = CompleteRun(s, "Петя", [1, 1]);
        s.Act(new AdjustPlayer(s.PlayerId("Маша"), "Бонус", PointsDelta: 3));
        Close(s);
        Approve(s, vasyaRun);
        Reject(s, petyaRun);
        ScenarioAssert.Accepted(s);
        var leaderboard = Leaderboard.Build(s.State);

        s.Act(new ChangeSeasonStatus(SeasonStatus.Finished));

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [new SeasonStatusChanged(SeasonStatus.Closing, SeasonStatus.Finished), new SeasonResultRecorded(leaderboard)],
            s.Last.Events);
        Assert.Equal(SeasonStatus.Finished, s.State.Status);
        Assert.Equal(leaderboard, s.State.Result);
        Assert.Equal([s.PlayerId("Вася"), s.PlayerId("Маша"), s.PlayerId("Петя")], s.State.Result!.Value.Select(r => r.PlayerId));
    }

    [Fact]
    public void Season_without_runs_finishes_with_everybody_sharing_the_place()
    {
        var s = New();
        Close(s);

        Finish(s);

        Assert.Equal([1, 1, 1], s.State.Result!.Value.Select(r => r.Place));
    }

    [Fact]
    public void Result_takes_changes_made_while_closing()
    {
        // The reject in Closing takes Вася's points: the result is the table after it («на момент дедлайна» — D-101)
        var s = New();
        var vasyaRun = CompleteRun(s, "Вася", [2, 2]);
        var petyaRun = CompleteRun(s, "Петя", [1, 1]);
        Close(s);
        Reject(s, vasyaRun);
        Approve(s, petyaRun);

        Finish(s);

        Assert.Equal(s.PlayerId("Петя"), s.State.Result!.Value[0].PlayerId);
        Assert.Equal(0, s.State.Result!.Value.Single(r => r.PlayerId == s.PlayerId("Вася")).Points);
    }

    [Fact]
    public void No_result_before_the_finish()
    {
        var s = New();
        Assert.Null(s.State.Result);

        Close(s);

        Assert.Null(s.State.Result);
    }

    [Fact]
    public void Result_survives_the_replay_and_the_json_round_trip()
    {
        var s = New();
        var runId = CompleteRun(s, "Вася", [2, 2]);
        Close(s);
        Approve(s, runId);
        Finish(s);

        var roundTripped = s.Log.Select(e => EventCodec.Decode(EventCodec.Encode(e))).ToList();

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
        Assert.Equal(s.State.Result, SeasonEngine.Replay(roundTripped).Result);
    }

    // ---- After the finish nothing changes ----

    public static TheoryData<string> LateCommands =>
        ["submitProof", "approve", "reject", "correctHours", "changeDifficulty", "convertToDrop", "adjustPlayer", "deadline", "reachDeadline", "roll"];

    [Theory]
    [MemberData(nameof(LateCommands))]
    public void Nothing_changes_after_the_finish(string kind)
    {
        // Every run is checked before the finish: Вася's approved (his proof was already reviewed anyway), a tech reroll
        // left to convert; the commands below would change the season if it were still open
        var s = New();
        var approved = CompleteRun(s, "Вася", [2, 2]);
        s.Roll("Петя").Start("Петя");
        var techRerolled = s.Player("Петя").ActiveRunId!.Value;
        s.Act(new TechReroll(s.PlayerId("Петя"), TechRerollReason.DoesNotLaunch, null));
        ScenarioAssert.Accepted(s);
        Close(s);
        Approve(s, approved);
        Finish(s);
        var vasya = s.PlayerId("Вася");

        ICommand command = kind switch
        {
            "submitProof" => new SubmitProof(vasya, approved, ["https://imgur.com/a/credits"]),
            "approve" => new ApproveProof(approved, null, "Ещё раз"),
            "reject" => new RejectProof(approved, "Передумали"),
            "correctHours" => new CorrectRunHours(approved, 12, "Часы по HLTB"),
            "changeDifficulty" => new ChangeRunDifficulty(approved, Difficulty.Hard, "По пруфу — сложная"),
            "convertToDrop" => new ConvertTechRerollToDrop(techRerolled, "это был дроп"),
            "adjustPlayer" => new AdjustPlayer(vasya, "Бонус", PointsDelta: 5),
            "deadline" => new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromDays(7)),
            "reachDeadline" => new ReachDeadline(),
            _ => new RollGame(vasya),
        };
        var before = s.State;
        var logLength = s.Log.Count;

        s.NextRandom(3, 3).Act(command);

        Assert.False(s.Last.IsAccepted, $"{kind} was accepted after the finish.");
        Assert.Empty(s.Last.Events);
        Assert.Equal(before, s.State);
        Assert.Equal(logLength, s.Log.Count);
    }

    [Fact]
    public void Archive_follows_the_finish_and_keeps_the_result()
    {
        var s = New();
        CompleteRun(s, "Вася", [2, 2]);
        var runId = s.State.Runs.Keys.Single();
        Close(s);
        Approve(s, runId);
        Finish(s);
        var result = s.State.Result;

        s.Act(new ChangeSeasonStatus(SeasonStatus.Archived));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new SeasonStatusChanged(SeasonStatus.Finished, SeasonStatus.Archived)], s.Last.Events);
        Assert.Equal(result, s.State.Result);
    }

    [Fact]
    public void Finish_straight_from_active_is_refused()
    {
        var s = New();

        Refused(s, new ChangeSeasonStatus(SeasonStatus.Finished), RejectionCodes.SeasonInvalidTransition);
    }
}

using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Proofs.ProofSetup;

namespace GameEvent.Engine.Tests.Proofs;

/// <summary>
/// Uploaded screenshots in a proof (D-116, SPEC «Пруф: скрин титров…»): up to five different files, enough on their own,
/// kept in the proof as the event recorded them; whose they are is checked by the site before the command.
/// </summary>
public class ProofFilesTests
{
    private static readonly Guid s_shot1 = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid s_shot2 = Guid.Parse("50000000-0000-0000-0000-000000000002");

    [Fact]
    public void A_screenshot_alone_is_enough()
    {
        var (s, runId) = Completed([3, 1]);

        s.Act(new SubmitProof(s.PlayerId("Вася"), runId, [], Files: [s_shot1]));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new ProofSubmitted(runId, s.PlayerId("Вася"), [], null, null, s.Clock.UtcNow, [s_shot1]), Assert.Single(s.Last.Events));
        Assert.Equal(new ProofState(ProofStatus.Pending, [], null, null, s.Clock.UtcNow, null, [s_shot1]), s.State.Runs[runId].Proof);
    }

    [Fact]
    public void Screenshots_go_with_links_a_note_and_a_witness_in_their_order()
    {
        var (s, runId) = Completed([3, 1]);
        var masha = s.PlayerId("Маша");

        s.Act(new SubmitProof(s.PlayerId("Вася"), runId, ["https://youtu.be/ending"], "Титры", masha, [s_shot2, s_shot1]));

        ScenarioAssert.Accepted(s);
        Assert.Equal([s_shot2, s_shot1], s.State.Runs[runId].Proof!.Files);
    }

    [Fact]
    public void Five_screenshots_are_accepted()
    {
        var (s, runId) = Completed([3, 1]);
        EquatableArray<Guid> five = [.. Enumerable.Range(1, Limits.MaxProofFiles).Select(i => SequentialIds.Make(0x50000000, i))];

        s.Act(new SubmitProof(s.PlayerId("Вася"), runId, [], Files: five));

        ScenarioAssert.Accepted(s);
        Assert.Equal(five, s.State.Runs[runId].Proof!.Files);
    }

    [Fact]
    public void More_than_five_screenshots_are_refused()
    {
        var (s, runId) = Completed([3, 1]);
        EquatableArray<Guid> six = [.. Enumerable.Range(1, Limits.MaxProofFiles + 1).Select(i => SequentialIds.Make(0x50000000, i))];

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new SubmitProof(s.PlayerId("Вася"), runId, [], Files: six)), RejectionCodes.ProofInvalidFile);
    }

    [Fact]
    public void The_same_screenshot_twice_is_refused()
    {
        var (s, runId) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitProof(s.PlayerId("Вася"), runId, [], Files: [s_shot1, s_shot1])), RejectionCodes.ProofInvalidFile);
    }

    [Fact]
    public void An_empty_file_id_is_refused()
    {
        var (s, runId) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitProof(s.PlayerId("Вася"), runId, [], Files: [Guid.Empty])), RejectionCodes.ProofInvalidFile);
    }

    [Fact]
    public void A_new_proof_replaces_the_screenshots_too()
    {
        var (s, runId) = Completed([3, 1]);
        s.Act(new SubmitProof(s.PlayerId("Вася"), runId, [], Files: [s_shot1]));

        Submit(s, "Вася", runId, ["https://youtu.be/ending"]);

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.State.Runs[runId].Proof!.Files);
    }

    [Fact]
    public void An_approval_keeps_the_screenshots()
    {
        var (s, runId) = Completed([3, 1]);
        s.Act(new SubmitProof(s.PlayerId("Вася"), runId, [], Files: [s_shot1]));

        Approve(s, runId);

        Assert.Equal((ProofStatus.Approved, [s_shot1]), (s.State.Runs[runId].Proof!.Status, s.State.Runs[runId].Proof!.Files));
    }

    [Fact]
    public void Replaying_the_log_gives_the_same_screenshots()
    {
        var (s, runId) = Completed([3, 1]);
        s.Act(new SubmitProof(s.PlayerId("Вася"), runId, [], Files: [s_shot1, s_shot2]));

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }
}

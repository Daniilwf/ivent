using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Proofs.ProofSetup;

namespace GameEvent.Engine.Tests.Proofs;

/// <summary>
/// The player sends the proof of a completed run (W4; SPEC «Пруф: скрин титров, финальной катсцены или победного экрана,
/// либо прохождение видел другой участник. Кубы кидаются сразу, одобрение админа приходит позже и ход не блокирует»;
/// «В статусе „закрытие“ … пруфы принимаются»; D-98). <see cref="SubmitProof"/> writes one <see cref="ProofSubmitted"/>
/// and the run's proof is pending. Links: 1–5, http/https only, up to 500 characters each; a witness is another player
/// of the season; at least a link or a witness; the note is up to 500 characters. While unchecked, a new proof replaces
/// the old one; a checked proof does not change. Proofs are not turn commands: they do not depend on the phase.
/// </summary>
public class SubmitProofTests
{
    // ---- Accepted ----

    [Fact]
    public void Player_submits_a_proof_of_their_completed_run()
    {
        // Given Вася completed a run: 4 points, c4, 6 coins
        var (s, runId) = Completed([3, 1]);
        var vasya = s.PlayerId("Вася");
        var petya = s.PlayerId("Петя");
        var before = s.Player("Вася");

        // When he sends a link, a note and Петя as the witness
        Submit(s, "Вася", runId, [Link], Note, petya);

        // Then exactly one event, and the run's proof waits for the admin
        ScenarioAssert.Accepted(s);
        Assert.Equal([new ProofSubmitted(runId, vasya, [Link], Note, petya, s.Clock.UtcNow, [])], s.Last.Events);
        var run = s.State.Runs[runId];
        Assert.Equal(new ProofState(ProofStatus.Pending, [Link], Note, petya, s.Clock.UtcNow, null), run.Proof);
        Assert.Equal(RunStatus.Completed, run.Status);

        // And nothing else changes: the dice were thrown at completion, the proof does not block (W4)
        Assert.Equal(before, s.Player("Вася"));
    }

    [Fact]
    public void Completed_run_has_no_proof_until_one_is_sent()
    {
        var (s, runId) = Completed([3, 1]);

        Assert.Null(s.State.Runs[runId].Proof);
        Assert.Equal(FixedClock.SeasonStart, s.State.Runs[runId].CompletedAt);
    }

    [Fact]
    public void Link_alone_is_enough()
    {
        var (s, runId) = Completed([3, 1]);

        Submit(s, "Вася", runId, [Link]);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new ProofState(ProofStatus.Pending, [Link], null, null, s.Clock.UtcNow, null), s.State.Runs[runId].Proof);
    }

    [Fact]
    public void Witness_alone_is_enough()
    {
        // SPEC: «либо прохождение видел другой участник»
        var (s, runId) = Completed([3, 1]);
        var masha = s.PlayerId("Маша");

        Submit(s, "Вася", runId, [], witness: masha);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new ProofState(ProofStatus.Pending, [], null, masha, s.Clock.UtcNow, null), s.State.Runs[runId].Proof);
    }

    [Fact]
    public void Http_link_is_accepted_as_well_as_https()
    {
        var (s, runId) = Completed([3, 1]);

        Submit(s, "Вася", runId, ["http://example.com/credits.png"]);

        ScenarioAssert.Accepted(s);
        Assert.Equal(["http://example.com/credits.png"], s.State.Runs[runId].Proof!.Links);
    }

    [Fact]
    public void Five_links_are_accepted_in_their_order()
    {
        var (s, runId) = Completed([3, 1]);
        EquatableArray<string> links = [.. Enumerable.Range(1, Limits.MaxProofLinks).Select(i => $"https://imgur.com/a/{i}")];

        Submit(s, "Вася", runId, links);

        ScenarioAssert.Accepted(s);
        Assert.Equal(links, Assert.Single(s.LastEvents<ProofSubmitted>()).Links);
        Assert.Equal(links, s.State.Runs[runId].Proof!.Links);
    }

    [Fact]
    public void Link_of_the_maximum_length_is_accepted()
    {
        var (s, runId) = Completed([3, 1]);
        const string Prefix = "https://imgur.com/a/";
        var link = Prefix + new string('x', Limits.MaxProofLinkLength - Prefix.Length);

        Submit(s, "Вася", runId, [link]);

        ScenarioAssert.Accepted(s);
        Assert.Equal(Limits.MaxProofLinkLength, s.State.Runs[runId].Proof!.Links[0].Length);
    }

    [Fact]
    public void Note_of_the_maximum_length_is_accepted()
    {
        var (s, runId) = Completed([3, 1]);
        var note = new string('я', Limits.MaxCommentLength);

        Submit(s, "Вася", runId, [Link], note);

        ScenarioAssert.Accepted(s);
        Assert.Equal(note, s.State.Runs[runId].Proof!.Note);
    }

    // ---- Stored trimmed (D-98 (4)) ----

    [Fact]
    public void Links_with_surrounding_spaces_are_stored_trimmed()
    {
        var (s, runId) = Completed([3, 1]);

        Submit(s, "Вася", runId, ["  " + Link + " ", "\thttps://youtu.be/ending\n"]);

        // The event stores the result, so the log and the state agree
        ScenarioAssert.Accepted(s);
        Assert.Equal([Link, "https://youtu.be/ending"], Assert.Single(s.LastEvents<ProofSubmitted>()).Links);
        Assert.Equal([Link, "https://youtu.be/ending"], s.State.Runs[runId].Proof!.Links);
    }

    [Fact]
    public void Note_is_stored_trimmed()
    {
        var (s, runId) = Completed([3, 1]);

        Submit(s, "Вася", runId, [Link], "  " + Note + "\n");

        ScenarioAssert.Accepted(s);
        Assert.Equal(Note, Assert.Single(s.LastEvents<ProofSubmitted>()).Note);
        Assert.Equal(Note, s.State.Runs[runId].Proof!.Note);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void Blank_note_is_stored_as_no_note(string note)
    {
        var (s, runId) = Completed([3, 1]);

        Submit(s, "Вася", runId, [Link], note);

        ScenarioAssert.Accepted(s);
        Assert.Null(Assert.Single(s.LastEvents<ProofSubmitted>()).Note);
        Assert.Equal(new ProofState(ProofStatus.Pending, [Link], null, null, s.Clock.UtcNow, null), s.State.Runs[runId].Proof);
    }

    [Fact]
    public void Blank_note_with_a_witness_only_is_still_a_proof_without_a_note()
    {
        // A blank note is no note: the witness alone makes the proof
        var (s, runId) = Completed([3, 1]);
        var masha = s.PlayerId("Маша");

        Submit(s, "Вася", runId, [], "   ", masha);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new ProofState(ProofStatus.Pending, [], null, masha, s.Clock.UtcNow, null), s.State.Runs[runId].Proof);
    }

    [Fact]
    public void New_proof_replaces_the_pending_one()
    {
        // D-98: while the proof is unchecked, a new one replaces the old
        var (s, runId) = Completed([3, 1]);
        Submit(s, "Вася", runId, [Link], Note);
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(2));

        Submit(s, "Вася", runId, ["https://youtu.be/ending"], witness: s.PlayerId("Петя"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new ProofState(ProofStatus.Pending, ["https://youtu.be/ending"], null, s.PlayerId("Петя"), s.Clock.UtcNow, null),
            s.State.Runs[runId].Proof);
        Assert.Equal(2, s.Log.OfType<ProofSubmitted>().Count());
    }

    [Fact]
    public void Proof_is_sent_while_another_game_is_played()
    {
        // Proofs are not turn commands: Вася plays his next game and sends the proof of the previous one
        var (s, runId) = Completed([3, 1]);
        s.Roll("Вася").Start("Вася");
        var active = s.Player("Вася").ActiveRunId;

        Submit(s, "Вася", runId, [Link]);

        ScenarioAssert.Accepted(s);
        Assert.Equal(TurnPhase.Playing, s.Player("Вася").Phase);
        Assert.Equal(active, s.Player("Вася").ActiveRunId);
    }

    [Fact]
    public void Proof_is_sent_while_the_player_has_an_offer()
    {
        var (s, runId) = Completed([3, 1]);
        s.Roll("Вася");

        Submit(s, "Вася", runId, [Link]);

        ScenarioAssert.Accepted(s);
        Assert.Equal(TurnPhase.Rolling, s.Player("Вася").Phase);
    }

    [Fact]
    public void Proof_of_an_earlier_run_is_accepted()
    {
        var (s, first) = Completed([3, 1]);
        CompleteRun(s, "Вася", [2, 2]);

        Submit(s, "Вася", first, [Link]);

        ScenarioAssert.Accepted(s);
        Assert.Equal(ProofStatus.Pending, s.State.Runs[first].Proof!.Status);
    }

    [Fact]
    public void Proof_is_accepted_while_the_season_is_closing()
    {
        // SPEC «Сезон»: after the deadline rolls are forbidden, proofs are accepted
        var (s, runId) = Completed([3, 1]);
        MoveSeasonTo(s, SeasonStatus.Closing);

        Submit(s, "Вася", runId, [Link]);

        ScenarioAssert.Accepted(s);
        Assert.Equal(ProofStatus.Pending, s.State.Runs[runId].Proof!.Status);
    }

    // ---- Links ----

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/credits.png")]
    [InlineData("file:///C:/credits.png")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("/proofs/credits.png")]
    [InlineData("imgur.com/a/credits")]
    [InlineData("https://")]
    [InlineData("")]
    [InlineData("   ")]
    public void Link_that_is_not_http_or_https_is_rejected(string link)
    {
        var (s, runId) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася", runId, [link]), RejectionCodes.ProofInvalidLink);
    }

    [Fact]
    public void One_bad_link_among_good_ones_rejects_the_whole_proof()
    {
        var (s, runId) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => Submit(x, "Вася", runId, [Link, "javascript:alert(1)"], witness: x.PlayerId("Петя")), RejectionCodes.ProofInvalidLink);
    }

    [Fact]
    public void Link_over_the_limit_is_rejected()
    {
        var (s, runId) = Completed([3, 1]);
        const string Prefix = "https://imgur.com/a/";
        var link = Prefix + new string('x', Limits.MaxProofLinkLength - Prefix.Length + 1);

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася", runId, [link]), RejectionCodes.ProofInvalidLink);
    }

    [Fact]
    public void More_than_five_links_are_rejected()
    {
        var (s, runId) = Completed([3, 1]);
        EquatableArray<string> links = [.. Enumerable.Range(1, Limits.MaxProofLinks + 1).Select(i => $"https://imgur.com/a/{i}")];

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася", runId, links), RejectionCodes.ProofInvalidLink);
    }

    [Fact]
    public void Proof_without_links_and_witness_is_rejected_as_empty()
    {
        var (s, runId) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася", runId, []), RejectionCodes.ProofEmpty);
    }

    [Fact]
    public void Note_alone_is_not_a_proof()
    {
        var (s, runId) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася", runId, [], "Поверьте на слово"), RejectionCodes.ProofEmpty);
    }

    // ---- Witness and note ----

    [Fact]
    public void Player_cannot_be_their_own_witness()
    {
        var (s, runId) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => Submit(x, "Вася", runId, [Link], witness: x.PlayerId("Вася")), RejectionCodes.ProofWitnessInvalid);
    }

    [Fact]
    public void Witness_outside_the_season_is_rejected()
    {
        var (s, runId) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => Submit(x, "Вася", runId, [Link], witness: SequentialIds.Make(0x10000000, 0x77)), RejectionCodes.ProofWitnessInvalid);
    }

    [Fact]
    public void Note_over_the_limit_is_rejected()
    {
        var (s, runId) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => Submit(x, "Вася", runId, [Link], new string('я', Limits.MaxCommentLength + 1)), RejectionCodes.CommentTooLong);
    }

    // ---- Whose run and which ----

    [Fact]
    public void Proof_of_another_players_run_is_rejected()
    {
        var (s, runId) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Петя", runId, [Link]), RejectionCodes.NotYourRun);
    }

    [Fact]
    public void Proof_of_a_run_being_played_is_rejected()
    {
        var (s, _) = Completed([3, 1]);
        s.Roll("Вася").Start("Вася");
        var playing = s.Player("Вася").ActiveRunId!.Value;

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася", playing, [Link]), RejectionCodes.RunNotCompleted);
    }

    [Fact]
    public void Proof_of_a_dropped_run_is_rejected()
    {
        var (s, _) = Completed([3, 1]);
        var dropped = DroppedRun(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася", dropped, [Link]), RejectionCodes.RunNotCompleted);
    }

    [Fact]
    public void Proof_of_an_unknown_run_is_rejected()
    {
        var (s, _) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => Submit(x, "Вася", SequentialIds.Make(0x0BAD0000, 1), [Link]), RejectionCodes.RunUnknown);
    }

    [Fact]
    public void Approved_proof_does_not_change()
    {
        var (s, runId) = Completed([3, 1]);
        Submit(s, "Вася", runId, [Link]);
        Approve(s, runId);
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася", runId, ["https://youtu.be/ending"]), RejectionCodes.ProofAlreadyReviewed);
    }

    [Fact]
    public void Proof_of_a_rejected_run_is_rejected_as_already_reviewed()
    {
        // D-98: a checked proof does not change; a rejected run comes back only by an undo (C12)
        var (s, runId) = Completed([3, 1]);
        Reject(s, runId);
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася", runId, [Link]), RejectionCodes.ProofAlreadyReviewed);
    }

    [Theory]
    [InlineData(SeasonStatus.Finished)]
    [InlineData(SeasonStatus.Archived)]
    public void Proof_after_the_season_is_finished_is_rejected(SeasonStatus status)
    {
        var (s, runId) = Completed([3, 1]);
        s.MoveStatusToForcingFinish(status); // the run stays unchecked on purpose

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася", runId, [Link]), RejectionCodes.SeasonClosed);
    }

    // ---- The log ----

    [Fact]
    public void Replaying_the_log_gives_the_same_proof()
    {
        var (s, runId) = Completed([3, 1]);
        Submit(s, "Вася", runId, [Link], Note);
        s.Advance(TimeSpan.FromMinutes(10));
        Submit(s, "Вася", runId, [Link, "https://youtu.be/ending"], witness: s.PlayerId("Маша"));
        ScenarioAssert.Accepted(s);

        var replayed = SeasonEngine.Replay(s.Log);

        Assert.Equal(s.State, replayed);
        Assert.Equal(s.State.Runs[runId].Proof, replayed.Runs[runId].Proof);
    }

    [Fact]
    public void Folding_the_submitted_event_alone_sets_the_proof()
    {
        var (s, runId) = Completed([3, 1]);
        var before = s.State;

        Submit(s, "Вася", runId, [Link], Note);

        ScenarioAssert.Accepted(s);
        Assert.Equal(s.State, s.Last.Events.Aggregate(before, SeasonEngine.Apply));
    }
}

using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// IRL proofs in the season's life: in «закрытие» they are still submitted and reviewed (SPEC «Сезон»: after the deadline
/// throws are refused, proofs are accepted; EV37, EV48); the frozen first finisher may settle his IRL effect, it changes
/// nothing (EV42, invariant 8, Н-9); undo goes command by command and a submission does not hold the player's turns (EV38,
/// D-104); a replay of the log gives the same state, the files and the link come from the events (EV49).
/// </summary>
public partial class IrlProofTests
{
    // ---- After the deadline (S5.6-11) ----

    [Fact]
    public void In_closing_a_proof_is_submitted_and_reviewed()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        s.MoveStatusTo(SeasonStatus.Closing);

        Submit(s, "Вася", effect, Files(1), Video);
        Review(s, effect, approved: false, "Видео закрыто");
        Submit(s, "Вася", effect, Files(2));
        Review(s, effect, approved: true);

        Assert.False(s.State.ManualEffects.ContainsKey(effect));
    }

    // ---- Events switched off and the definition changed (D-685, D-681) ----

    [Fact]
    public void Events_are_not_switched_off_past_a_proof_waiting_for_the_review()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        Submit(s, "Вася", effect, Files(1));
        var off = s.Ruleset with { Features = s.Ruleset.Features with { Events = false } };

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new Engine.Rulesets.ChangeRuleset(off)), RejectionCodes.IrlProofsInPlay);
        Assert.Equal([effect], s.Last.Rejection!.Related);

        Review(s, effect, approved: false, "Видео закрыто");
        s.Act(new Engine.Rulesets.ChangeRuleset(off));
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Nobody_resolves_past_a_waiting_proof_even_when_the_effect_is_no_longer_irl()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        Submit(s, "Вася", effect, Files(1));

        // The pack republished with the karaoke's proof dropped: the waiting proof still goes through the review
        var pack = s.State.Catalog;
        var karaoke = pack.Find("karaoke")! with { Proof = null };
        s.Act(new Engine.Content.PublishContent(new Engine.Content.ContentPack
        {
            Objects = [.. pack.Objects.Values.Where(e => !e.Deleted).Select(e => e.Definition.Id == "karaoke" ? karaoke : e.Definition).OrderBy(o => o.Id, StringComparer.Ordinal)],
            Wheels = [.. pack.Wheels.Values.OrderBy(w => w.Id, StringComparer.Ordinal)],
        }, "Без пруфа"));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new Engine.Effects.ResolveManualEffect(effect, Engine.Effects.ManualEffectOutcome.Applied, null, x.PlayerId("Вася"))), RejectionCodes.IrlProofPending);
        Review(s, effect, approved: true);
    }

    // ---- The frozen first finisher (S5.6-15) ----

    [Fact]
    public void The_frozen_first_finisher_settles_his_irl_effect_and_nothing_changes()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        s.At("Вася", "c12");
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        var run = s.Player("Вася").ActiveRunId!.Value;
        s.NextRandom(1).Complete("Вася");
        s.Approve(run);
        Assert.True(s.Player("Вася").Finish?.Frozen, "Вася is not the frozen first finisher.");
        var vasya = Standing(s, "Вася");
        var petya = Standing(s, "Петя");

        Submit(s, "Вася", effect, Files(1), Video);
        Review(s, effect, approved: true);

        Assert.False(s.State.ManualEffects.ContainsKey(effect));
        Assert.Equal(vasya, Standing(s, "Вася"));
        Assert.Equal(petya, Standing(s, "Петя"));
    }

    // ---- Undo (S5.6-16, EV38) ----

    private static void AssertUndoneToTheLogWithout(Scenario s)
    {
        ScenarioAssert.Accepted(s);
        var replayed = SeasonEngine.Replay(s.EffectiveLog);
        Assert.Equal(replayed.ManualEffects, s.State.ManualEffects);
        Assert.Equal(replayed.Players, s.State.Players);
    }

    [Fact]
    public void Undoing_a_submission_waits_for_its_review_then_both_go_back()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        Submit(s, "Вася", effect, Files(1), Video);
        var submitted = s.LastCommandId;
        Review(s, effect, approved: true);
        var reviewed = s.LastCommandId;

        var before = s.State;
        var log = s.Log.Count;
        s.Act(new UndoCommand(submitted, "Не тот пруф"));

        ScenarioAssert.Rejected(s, before, log, RejectionCodes.UndoDependents);
        Assert.Contains(reviewed, s.Last.Rejection!.Related);

        s.Act(new UndoCommand(reviewed, "Одобрил по ошибке"));
        AssertUndoneToTheLogWithout(s);
        Assert.Equal(IrlProofStatus.Pending, ProofOf(s, effect)!.Status);

        s.Act(new UndoCommand(submitted, "Не тот пруф"));
        AssertUndoneToTheLogWithout(s);
        Assert.Null(ProofOf(s, effect));
    }

    [Fact]
    public void Undoing_a_reject_puts_the_proof_back_for_review()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        Submit(s, "Вася", effect, Files(1));
        Review(s, effect, approved: false, "Видео закрыто");

        s.Act(new UndoCommand(s.LastCommandId, "Видео открыто"));

        AssertUndoneToTheLogWithout(s);
        Assert.Equal(new IrlProofState(IrlProofStatus.Pending, Files(1), null, ProofOf(s, effect)!.SubmittedAt, null), ProofOf(s, effect));
    }

    [Fact]
    public void A_submission_does_not_hold_an_earlier_roll_of_the_player()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        s.Roll("Вася");
        var roll = s.LastCommandId;
        Submit(s, "Вася", effect, Files(1));

        s.Act(new UndoCommand(roll, "Ролл по ошибке"));

        AssertUndoneToTheLogWithout(s);
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
        Assert.Equal(IrlProofStatus.Pending, ProofOf(s, effect)!.Status);
    }

    [Fact]
    public void Undoing_the_drop_that_drew_the_karaoke_waits_for_the_submission()
    {
        var s = Season();
        s.PlayingFor("Вася");
        s.Dropped("Вася", 1, 1, 0);
        var drop = s.LastCommandId;
        var effect = s.State.ManualEffects.Values.Single(e => e.ObjectId == "karaoke").EffectId;
        Submit(s, "Вася", effect, Files(1));
        var submitted = s.LastCommandId;

        var before = s.State;
        var log = s.Log.Count;
        s.Act(new UndoCommand(drop, "Дроп по ошибке"));

        ScenarioAssert.Rejected(s, before, log, RejectionCodes.UndoDependents);
        Assert.Contains(submitted, s.Last.Rejection!.Related);
    }

    // ---- Replay (S5.6-17, EV49) ----

    [Fact]
    public void A_replay_of_the_log_gives_the_same_proofs_at_every_step()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        var other = Karaoke(s, "Петя");

        Submit(s, "Вася", effect, [Shot(1), Shot(2)], "  " + Video);
        Submit(s, "Петя", other, [], Video);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));

        Review(s, effect, approved: false, "Видео закрыто");
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));

        Submit(s, "Вася", effect, Files(3));
        Review(s, effect, approved: true);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }
}

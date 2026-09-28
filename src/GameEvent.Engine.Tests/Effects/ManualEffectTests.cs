using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Lifecycle;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Effects;

/// <summary>
/// Resolving manual effects (SPEC «Текстовые эффекты без автоматики», E1, D-31, D-102): the owner or the admin marks a
/// pending effect «применено» or «не применимо»; «не применимо» and every admin resolution carry a comment.
/// </summary>
public class ManualEffectTests
{
    // Вася drops his game: the mandatory bad event waits as a manual effect
    private static (Scenario S, Guid EffectId) Pending(string player = "Вася")
    {
        var s = LifecycleSetup.New(r => r with { Drop = r.Drop with { MandatoryEvent = Engine.Rulesets.MandatoryEvent.Bad } });
        s.Roll(player).Start(player);
        s.Act(new DropRun(s.PlayerId(player)));
        ScenarioAssert.Accepted(s);
        var effect = Assert.Single(s.State.ManualEffects.Values, e => e.PlayerId == s.PlayerId(player));
        return (s, effect.EffectId);
    }

    [Fact]
    public void Owner_applies_the_effect_without_a_comment()
    {
        var (s, effectId) = Pending();
        var vasya = s.PlayerId("Вася");
        var runId = s.State.ManualEffects[effectId].RunId;
        var before = s.State;

        s.Act(new ResolveManualEffect(effectId, ManualEffectOutcome.Applied, null, vasya));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new ManualEffectResolved(effectId, vasya, runId, ManualEffectOutcome.Applied, "")], s.Last.Events);
        Assert.False(s.State.ManualEffects.ContainsKey(effectId));

        // Nothing else changes: the effect is a reminder, not an automatic change (D-102)
        Assert.Equal(before.Players[vasya], s.Player("Вася"));
    }

    [Fact]
    public void Owner_marks_the_effect_not_applicable_with_a_comment()
    {
        var (s, effectId) = Pending();
        var vasya = s.PlayerId("Вася");

        s.Act(new ResolveManualEffect(effectId, ManualEffectOutcome.NotApplicable, "  Колода пуста  ", vasya));

        ScenarioAssert.Accepted(s);
        var resolved = Assert.IsType<ManualEffectResolved>(Assert.Single(s.Last.Events));
        Assert.Equal((ManualEffectOutcome.NotApplicable, "Колода пуста"), (resolved.Outcome, resolved.Comment));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Not_applicable_needs_a_comment(string? comment)
    {
        var (s, effectId) = Pending();

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new ResolveManualEffect(effectId, ManualEffectOutcome.NotApplicable, comment, x.PlayerId("Вася"))),
            RejectionCodes.CommentRequired);
    }

    [Fact]
    public void Admin_resolves_any_effect_with_a_comment()
    {
        var (s, effectId) = Pending();

        s.Act(new ResolveManualEffect(effectId, ManualEffectOutcome.Applied, "Разыграли на стриме", PlayerId: null));

        ScenarioAssert.Accepted(s);
        var resolved = Assert.IsType<ManualEffectResolved>(Assert.Single(s.Last.Events));
        Assert.Equal((s.PlayerId("Вася"), "Разыграли на стриме"), (resolved.PlayerId, resolved.Comment));
    }

    [Theory]
    [InlineData(ManualEffectOutcome.Applied)]
    [InlineData(ManualEffectOutcome.NotApplicable)]
    public void Admin_always_writes_a_comment(ManualEffectOutcome outcome)
    {
        var (s, effectId) = Pending();

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ResolveManualEffect(effectId, outcome, " ", PlayerId: null)), RejectionCodes.CommentRequired);
    }

    [Fact]
    public void Player_cannot_resolve_someone_elses_effect()
    {
        var (s, effectId) = Pending();

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new ResolveManualEffect(effectId, ManualEffectOutcome.Applied, null, x.PlayerId("Петя"))),
            RejectionCodes.EffectNotYours);
    }

    [Fact]
    public void Resolved_effect_cannot_be_resolved_again()
    {
        var (s, effectId) = Pending();
        s.Act(new ResolveManualEffect(effectId, ManualEffectOutcome.Applied, null, s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new ResolveManualEffect(effectId, ManualEffectOutcome.NotApplicable, "Передумал", PlayerId: null)),
            RejectionCodes.EffectNotPending);
    }

    [Fact]
    public void Unknown_effect_is_not_pending()
    {
        var (s, _) = Pending();

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new ResolveManualEffect(Guid.NewGuid(), ManualEffectOutcome.Applied, "Ок", PlayerId: null)),
            RejectionCodes.EffectNotPending);
    }

    [Fact]
    public void Unknown_outcome_is_refused()
    {
        var (s, effectId) = Pending();

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ResolveManualEffect(effectId, (ManualEffectOutcome)7, "Ок", PlayerId: null)), RejectionCodes.EffectUnknownOutcome);
    }

    [Fact]
    public void Comment_over_the_limit_is_refused()
    {
        var (s, effectId) = Pending();

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new ResolveManualEffect(effectId, ManualEffectOutcome.NotApplicable, new string('я', Limits.MaxCommentLength + 1), PlayerId: null)),
            RejectionCodes.CommentTooLong);
    }

    [Fact]
    public void Comment_at_the_limit_is_accepted()
    {
        var (s, effectId) = Pending();

        s.Act(new ResolveManualEffect(effectId, ManualEffectOutcome.NotApplicable, new string('я', Limits.MaxCommentLength), PlayerId: null));

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Effects_are_resolved_while_closing()
    {
        var (s, effectId) = Pending();
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        ScenarioAssert.Accepted(s);

        s.Act(new ResolveManualEffect(effectId, ManualEffectOutcome.Applied, null, s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Nothing_is_resolved_after_the_finish()
    {
        var (s, effectId) = Pending();
        s.MoveStatusTo(SeasonStatus.Finished);

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new ResolveManualEffect(effectId, ManualEffectOutcome.Applied, null, x.PlayerId("Вася"))),
            RejectionCodes.SeasonClosed);
    }

    [Fact]
    public void Resolving_one_effect_leaves_the_others_pending()
    {
        var (s, effectId) = Pending();
        s.Roll("Петя").Start("Петя");
        s.Act(new DropRun(s.PlayerId("Петя")));
        ScenarioAssert.Accepted(s);
        Assert.Equal(2, s.State.ManualEffects.Count);

        s.Act(new ResolveManualEffect(effectId, ManualEffectOutcome.Applied, null, s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Equal(s.PlayerId("Петя"), Assert.Single(s.State.ManualEffects.Values).PlayerId);
    }
}

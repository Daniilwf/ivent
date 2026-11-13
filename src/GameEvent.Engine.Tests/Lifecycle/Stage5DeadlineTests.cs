using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Events;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Lifecycle;

/// <summary>
/// The deadline and the results with the mechanics of stage 5 (EV48, SPEC «Проверка на дыры» «Реджект после объявления
/// итогов»; D-546 (1)): the results wait for every IRL proof submitted and not yet reviewed — refused with
/// <c>season.proofsPending</c>, as unchecked runs (D-101), naming the effect. An IRL effect nobody submitted a proof for is a
/// pending manual effect and holds the results by D-603 (<c>season.effectsPending</c>) until the admin resolves it.
/// </summary>
public class Stage5DeadlineTests
{
    private static Scenario KaraokeSeason() =>
        EventSetup.Season(EventSetup.Pack(["karaoke", "sack-bad"], EventSetup.Deck(EventKind.Bad, "karaoke")));

    private static Guid Karaoke(Scenario s, string player)
    {
        s.Give(player, "sack-bad");
        s.Used(player, "sack-bad");
        return s.State.ManualEffects.Values.Single(e => e.PlayerId == s.PlayerId(player) && e.ObjectId == "karaoke").EffectId;
    }

    private static void Accept(Scenario s, ICommand command)
    {
        s.Act(command);
        ScenarioAssert.Accepted(s);
    }

    private static void FinishRefused(Scenario s, string code, Guid related)
    {
        var before = s.State;
        var log = s.Log.Count;

        s.Act(new ChangeSeasonStatus(SeasonStatus.Finished));

        ScenarioAssert.Rejected(s, before, log, code);
        Assert.Contains(related, s.Last.Rejection!.Related);
    }

    [Fact]
    public void Results_wait_for_irl_proofs()
    {
        var s = KaraokeSeason();
        var effect = Karaoke(s, "Петя");
        s.MoveStatusTo(SeasonStatus.Closing);
        Accept(s, new SubmitIrlProof(s.PlayerId("Петя"), effect, [new Guid(0x0F11E001, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)], "https://video.example/abc"));

        FinishRefused(s, RejectionCodes.SeasonProofsPending, effect);

        Accept(s, new ReviewIrlProof(effect, Approved: true));
        Accept(s, new ChangeSeasonStatus(SeasonStatus.Finished));
        Assert.Equal(SeasonStatus.Finished, s.State.Status);
    }

    [Fact]
    public void Results_wait_for_an_irl_effect_without_a_proof_until_the_admin_resolves_it()
    {
        // Н-8 «неподанный не держит» gives way to D-603: a pending manual effect could not be resolved after the finish
        var s = KaraokeSeason();
        var effect = Karaoke(s, "Петя");
        s.MoveStatusTo(SeasonStatus.Closing);

        FinishRefused(s, RejectionCodes.SeasonEffectsPending, effect);

        Accept(s, new ResolveManualEffect(effect, ManualEffectOutcome.NotApplicable, "Пруфа не будет", PlayerId: null));
        Accept(s, new ChangeSeasonStatus(SeasonStatus.Finished));
        Assert.Equal(SeasonStatus.Finished, s.State.Status);
    }

    [Fact]
    public void Results_wait_for_a_rejected_irl_proof_like_any_pending_effect()
    {
        var s = KaraokeSeason();
        var effect = Karaoke(s, "Петя");
        var petya = s.PlayerId("Петя");
        s.MoveStatusTo(SeasonStatus.Closing);
        Accept(s, new SubmitIrlProof(petya, effect, [], "https://video.example/abc"));
        Accept(s, new ReviewIrlProof(effect, Approved: false, "Видео закрыто"));

        FinishRefused(s, RejectionCodes.SeasonEffectsPending, effect);

        Accept(s, new SubmitIrlProof(petya, effect, [], "https://video.example/open"));
        FinishRefused(s, RejectionCodes.SeasonProofsPending, effect);

        Accept(s, new ReviewIrlProof(effect, Approved: true));
        Accept(s, new ChangeSeasonStatus(SeasonStatus.Finished));
        Assert.Equal(SeasonStatus.Finished, s.State.Status);
    }
}

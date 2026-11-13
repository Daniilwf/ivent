using GameEvent.Engine.Content;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// Manual events (SPEC «Ивенты»: «текст плюс, где возможно, автоматика»; «Текстовые эффекты без автоматики попадают в
/// список „ждёт ручного разрешения“»; EV1, EV14): an event with <c>manual: true</c> is drawn like any other and leaves a
/// manual effect of that object (D-410) for the player or the admin. CONTENT.md «Примеры ивентов» 3 «Караоке» is the
/// acceptance test: drawn as the bad event of a drop, its text waits, the turn does not.
/// </summary>
public class ManualEventTests
{
    private static Scenario KaraokeSeason(Func<Ruleset, Ruleset>? rules = null) =>
        EventSetup.Season(EventSetup.Pack(["karaoke", "sack-bad"], EventSetup.Deck(EventKind.Bad, "karaoke")), rules: rules);

    [Fact]
    public void The_karaoke_of_the_content_guide_publishes_with_events_on()
    {
        var karaoke = EconomyScenario.Examples()["karaoke"];
        Assert.True(karaoke.Manual);

        Assert.Empty(ContentPublishing.Check(new ContentPack { Objects = [karaoke], Wheels = [EventSetup.Deck(EventKind.Bad, "karaoke")] }, events: true));
    }

    [Fact]
    public void Karaoke_drawn_by_a_drop_leaves_a_manual_effect_of_the_event_and_the_turn_goes_on()
    {
        var s = KaraokeSeason();
        s.PlayingFor("Вася");

        s.Dropped("Вася", 1, 1, 0);

        var vasya = s.PlayerId("Вася");
        Assert.Equal(new EventDrawn(vasya, "bad", "karaoke", EventKind.Bad, EventSource.DropPenalty), Assert.Single(s.LastEvents<EventDrawn>()));
        var created = Assert.Single(s.LastEvents<ManualEffectCreated>());
        // The pending effect remembers the drop it came from, so a buy-out never takes it (panel D-508, D-557)
        Assert.Equal((vasya, (EventKind?)null, ManualEffectSource.Drop, "karaoke"), (created.PlayerId, created.DrawEvent, created.Source, created.ObjectId));
        Assert.True(s.IndexOf(e => e is EventDrawn) < s.IndexOf(e => e is ManualEffectCreated));

        var pending = s.State.ManualEffects[created.EffectId];
        Assert.Equal("karaoke", pending.ObjectId);

        // SPEC «IRL-ивенты … ход не блокируется»: Вася rolls on while the karaoke waits
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
        s.Roll("Вася");
        Assert.True(s.State.ManualEffects.ContainsKey(created.EffectId));
    }

    [Fact]
    public void A_manual_event_from_an_item_waits_like_one_from_a_drop()
    {
        var s = KaraokeSeason();
        s.Give("Вася", "sack-bad");

        s.Used("Вася", "sack-bad");

        Assert.Equal(EventSource.Item, Assert.Single(s.LastEvents<EventDrawn>()).Source);
        Assert.Single(s.LastEvents<ManualEffectCreated>(), m => m.ObjectId == "karaoke" && m.Source == ManualEffectSource.Event);
    }

    [Fact]
    public void The_manual_effect_of_an_event_is_written_in_version_two()
    {
        var s = KaraokeSeason();
        s.Give("Вася", "sack-bad");
        s.Used("Вася", "sack-bad");

        var created = Assert.Single(s.LastEvents<ManualEffectCreated>());

        Assert.Equal(("manual-effect-created", 2), (EventCatalog.Describe(created.GetType()).Name, EventCatalog.Describe(created.GetType()).Version));
    }

    [Fact]
    public void The_admin_resolves_the_karaoke_with_a_comment()
    {
        var s = KaraokeSeason();
        s.Give("Вася", "sack-bad");
        s.Used("Вася", "sack-bad");
        var effectId = Assert.Single(s.State.ManualEffects).Key;

        s.Act(new ResolveManualEffect(effectId, ManualEffectOutcome.Applied, "Спел «Батарейку», видео в галерее", PlayerId: null));

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<ManualEffectResolved>(), r => r.EffectId == effectId && r.Outcome == ManualEffectOutcome.Applied);
        Assert.Empty(s.State.ManualEffects);
    }

    [Fact]
    public void Another_player_cannot_resolve_the_karaoke()
    {
        var s = KaraokeSeason();
        s.Give("Вася", "sack-bad");
        s.Used("Вася", "sack-bad");
        var effectId = Assert.Single(s.State.ManualEffects).Key;

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ResolveManualEffect(effectId, ManualEffectOutcome.Applied, null, x.PlayerId("Петя"))), RejectionCodes.EffectNotYours);
    }

    [Fact]
    public void A_manual_event_is_still_resolved_after_the_deadline()
    {
        // SPEC «Сезон»: in «закрытие» throws are refused, the rest is settled
        var s = KaraokeSeason();
        s.Give("Вася", "sack-bad");
        s.Used("Вася", "sack-bad");
        var effectId = Assert.Single(s.State.ManualEffects).Key;
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        ScenarioAssert.Accepted(s);

        s.Act(new ResolveManualEffect(effectId, ManualEffectOutcome.NotApplicable, "Не успел до конца сезона", PlayerId: null));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.State.ManualEffects);
    }

    [Fact]
    public void A_replay_of_a_manual_event_gives_the_same_pending_effect()
    {
        var s = KaraokeSeason();
        s.PlayingFor("Вася");
        s.Dropped("Вася", 1, 1, 0);

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }
}

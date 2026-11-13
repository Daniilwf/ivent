using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// IRL events (SPEC «Ивенты»: «IRL-ивенты (караоке, готовка, грим): пруф фото или видео, админ подтверждает, ход не
/// блокируется»; EV12, D-509): a manual effect of an object with <c>proof: media</c> (CONTENT.md «Примеры ивентов» 3
/// «Караоке») waits for the player's proof — own screenshots and / or one https link — and the admin's review. Approval
/// resolves the manual effect «применено» and gives nothing; a reject with a comment lets the player submit again, the log of
/// the first submission stays. The owner cannot resolve an IRL effect himself; an ordinary manual event without a proof
/// still can. Test ruleset: <c>events.irlProof.maxFiles = 5</c>.
/// </summary>
public partial class IrlProofTests
{
    private const string Video = "https://video.example/abc";

    /// <summary>An ordinary manual event without a proof: a text the owner may mark himself.</summary>
    private static readonly ObjectDefinition s_toast = ContentJson.Parse<ObjectDefinition>("""
        {
          "id": "toast", "kind": "event", "name": "Тост",
          "description": "Скажи тост в общем чате.",
          "rarity": "common", "price": null, "hostile": false, "manual": true
        }
        """);

    /// <summary>
    /// The karaoke in the bad deck (a drop or the «Чёрный мешок» draws it), the toast in the good deck (the «Белый мешок»).
    /// </summary>
    private static Scenario Season(Func<Ruleset, Ruleset>? rules = null) =>
        EventSetup.Season(
            new ContentPack
            {
                Objects = [.. new[] { EventSetup.Object("karaoke"), EventSetup.Object("sack-bad"), EventSetup.Object("sack-good"), s_toast }
                    .OrderBy(o => o.Id, StringComparer.Ordinal)],
                Wheels = [EventSetup.Deck(EventKind.Bad, "karaoke"), EventSetup.Deck(EventKind.Good, "toast")],
            },
            rules: rules);

    private static Func<Ruleset, Ruleset> MaxFiles(int maxFiles) =>
        r => r with { Events = r.Events! with { IrlProof = new IrlProofRules { MaxFiles = maxFiles } } };

    /// <summary>A screenshot the site has stored for the player (ownership is the site's check, D-116, task 5.18).</summary>
    private static Guid Shot(int n) => new(0x0F11E000 + n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static EquatableArray<Guid> Files(int count) => [.. Enumerable.Range(1, count).Select(Shot)];

    /// <summary>The player draws the karaoke with the «Чёрный мешок»; returns the IRL effect.</summary>
    private static Guid Karaoke(Scenario s, string player)
    {
        s.Give(player, "sack-bad");
        s.Used(player, "sack-bad");
        return s.State.ManualEffects.Values.Single(e => e.PlayerId == s.PlayerId(player) && e.ObjectId == "karaoke").EffectId;
    }

    /// <summary>The player draws the toast with the «Белый мешок»; returns the ordinary manual effect.</summary>
    private static Guid Toast(Scenario s, string player)
    {
        s.Give(player, "sack-good");
        s.Used(player, "sack-good");
        return s.State.ManualEffects.Values.Single(e => e.PlayerId == s.PlayerId(player) && e.ObjectId == "toast").EffectId;
    }

    private static Scenario Submit(Scenario s, string player, Guid effect, EquatableArray<Guid> files, string? link = null)
    {
        s.Act(new SubmitIrlProof(s.PlayerId(player), effect, files, link));
        ScenarioAssert.Accepted(s);
        return s;
    }

    private static Scenario Review(Scenario s, Guid effect, bool approved, string? comment = null)
    {
        s.Act(new ReviewIrlProof(effect, approved, comment));
        ScenarioAssert.Accepted(s);
        return s;
    }

    private static IrlProofState? ProofOf(Scenario s, Guid effect) => s.State.ManualEffects[effect].IrlProof;

    private static (int Points, int Coins, string CellId) Standing(Scenario s, string player) =>
        (s.Player(player).Points, s.Player(player).Coins, s.Player(player).CellId);

    // ---- Which effect is IRL ----

    [Fact]
    public void The_karaoke_is_an_irl_effect_and_an_ordinary_manual_event_is_not()
    {
        var s = Season();
        var karaoke = Karaoke(s, "Вася");
        var toast = Toast(s, "Вася");

        Assert.True(IrlProofs.IsIrl(s.State, s.State.ManualEffects[karaoke]));
        Assert.False(IrlProofs.IsIrl(s.State, s.State.ManualEffects[toast]));
        Assert.Null(ProofOf(s, karaoke));
    }

    // ---- Submission (S5.6-01) ----

    [Fact]
    public void The_player_submits_screenshots_and_a_link_and_the_proof_waits_for_the_admin()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        var vasya = s.PlayerId("Вася");
        var standing = Standing(s, "Вася");

        s.Act(new SubmitIrlProof(vasya, effect, [Shot(1), Shot(2)], Video));

        ScenarioAssert.Accepted(s);
        var submitted = Assert.Single(s.Last.Events);
        Assert.Equal(new IrlProofSubmitted(effect, vasya, [Shot(1), Shot(2)], Video, s.Clock.UtcNow), submitted);
        Assert.Equal(new IrlProofState(IrlProofStatus.Pending, [Shot(1), Shot(2)], Video, s.Clock.UtcNow, null), ProofOf(s, effect));
        Assert.Equal(standing, Standing(s, "Вася"));
    }

    [Fact]
    public void The_turn_goes_on_while_the_proof_waits()
    {
        // SPEC «IRL-ивенты … ход не блокируется»
        var s = Season();
        var effect = Karaoke(s, "Вася");
        Submit(s, "Вася", effect, Files(1));

        s.Roll("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(TurnPhase.Rolling, s.Player("Вася").Phase);
        Assert.Equal(IrlProofStatus.Pending, ProofOf(s, effect)!.Status);
    }

    [Fact]
    public void Screenshots_alone_are_a_proof()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");

        Submit(s, "Вася", effect, Files(Limits.MaxProofFiles));

        Assert.Equal((IrlProofStatus.Pending, Files(Limits.MaxProofFiles), (string?)null), (ProofOf(s, effect)!.Status, ProofOf(s, effect)!.Files, ProofOf(s, effect)!.Link));
    }

    [Fact]
    public void A_link_alone_is_a_proof_and_is_kept_trimmed()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");

        Submit(s, "Вася", effect, [], "  " + Video + "  ");

        Assert.Equal((IrlProofStatus.Pending, 0, (string?)Video), (ProofOf(s, effect)!.Status, ProofOf(s, effect)!.Files.Count, ProofOf(s, effect)!.Link));
    }

    [Fact]
    public void A_link_of_the_longest_length_is_accepted()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        var link = "https://video.example/" + new string('a', Limits.MaxProofLinkLength - "https://video.example/".Length);
        Assert.Equal(Limits.MaxProofLinkLength, link.Length);

        Submit(s, "Вася", effect, [], link);

        Assert.Equal(link, ProofOf(s, effect)!.Link);
    }

    [Fact]
    public void A_season_without_screenshots_takes_a_link_and_refuses_a_file()
    {
        // events.irlProof.maxFiles = 0: only a link
        var s = Season(MaxFiles(0));
        var effect = Karaoke(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitIrlProof(x.PlayerId("Вася"), effect, Files(1))), RejectionCodes.IrlProofInvalidFile);

        Submit(s, "Вася", effect, [], Video);
    }

    // ---- What a submission refuses (S5.6-06, S5.6-07, S5.6-09, S5.6-10) ----

    [Fact]
    public void More_screenshots_than_the_ruleset_allows_are_refused()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitIrlProof(x.PlayerId("Вася"), effect, Files(6), Video)), RejectionCodes.IrlProofInvalidFile);
    }

    [Fact]
    public void The_ruleset_limit_on_screenshots_is_the_one_in_force()
    {
        var s = Season(MaxFiles(2));
        var effect = Karaoke(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitIrlProof(x.PlayerId("Вася"), effect, Files(3))), RejectionCodes.IrlProofInvalidFile);

        Submit(s, "Вася", effect, Files(2));
    }

    public static TheoryData<string, EquatableArray<Guid>> InvalidFiles() => new()
    {
        { "the same screenshot twice", [Shot(1), Shot(1)] },
        { "an empty file id", [Guid.Empty] },
    };

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Invalid_screenshots_are_refused(string what, EquatableArray<Guid> files)
    {
        _ = what;
        var s = Season();
        var effect = Karaoke(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitIrlProof(x.PlayerId("Вася"), effect, files)), RejectionCodes.IrlProofInvalidFile);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_proof_without_a_screenshot_or_a_link_is_refused(string? link)
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitIrlProof(x.PlayerId("Вася"), effect, [], link)), RejectionCodes.IrlProofEmpty);
    }

    public static TheoryData<string> InvalidLinks() =>
    [
        "http://video.example/abc",
        "https://user:pw@video.example/abc",
        "javascript:alert(1)",
        "https:///abc",
        "video.example/abc",
        "ftp://video.example/abc",
        "https://video.example/" + new string('a', Limits.MaxProofLinkLength - "https://video.example/".Length + 1),

        // Characters the parser drops or a browser rewrites: the shown address would differ from the checked one (D-680)
        "https://video.example/\u202Egpj.exe",
        "https:\\\\video.example\\abc",
        "https://video.example/a b",
        "\0https://video.example/abc",
        "https://video.example/\u200Babc",
    ];

    [Theory]
    [MemberData(nameof(InvalidLinks))]
    public void A_link_that_is_not_a_plain_https_address_is_refused(string link)
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitIrlProof(x.PlayerId("Вася"), effect, Files(1), link)), RejectionCodes.IrlProofInvalidLink);
    }

    [Fact]
    public void A_proof_to_another_players_effect_is_refused()
    {
        var s = Season();
        var petyaKaraoke = Karaoke(s, "Петя");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitIrlProof(x.PlayerId("Вася"), petyaKaraoke, Files(1))), RejectionCodes.EffectNotYours);
    }

    [Fact]
    public void A_proof_to_an_ordinary_manual_effect_is_refused()
    {
        var s = Season();
        var toast = Toast(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitIrlProof(x.PlayerId("Вася"), toast, Files(1))), RejectionCodes.IrlProofNotIrl);
    }

    [Fact]
    public void A_proof_to_an_unknown_effect_is_refused()
    {
        var s = Season();
        Karaoke(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitIrlProof(x.PlayerId("Вася"), new Guid(0x0DEAD, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), Files(1))), RejectionCodes.EffectNotPending);
    }

    [Fact]
    public void A_proof_from_an_unknown_player_is_refused()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitIrlProof(new Guid(0x0BAD, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), effect, Files(1))), RejectionCodes.PlayerUnknown);
    }

    [Fact]
    public void A_second_submission_waits_until_the_first_is_reviewed()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        Submit(s, "Вася", effect, Files(1));

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitIrlProof(x.PlayerId("Вася"), effect, Files(2), Video)), RejectionCodes.IrlProofPending);
    }

    [Fact]
    public void A_proof_to_an_approved_effect_is_refused()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        Submit(s, "Вася", effect, Files(1));
        Review(s, effect, approved: true);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitIrlProof(x.PlayerId("Вася"), effect, Files(2))), RejectionCodes.EffectNotPending);
    }

    // ---- Who resolves an IRL effect (S5.6-05) ----

    [Fact]
    public void The_owner_cannot_resolve_his_irl_effect_himself()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ResolveManualEffect(effect, ManualEffectOutcome.Applied, "Спел", x.PlayerId("Вася"))), RejectionCodes.EffectAdminOnly);
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ResolveManualEffect(effect, ManualEffectOutcome.NotApplicable, "Не буду петь", x.PlayerId("Вася"))), RejectionCodes.EffectAdminOnly);
    }

    [Fact]
    public void The_owner_still_resolves_an_ordinary_manual_event_himself()
    {
        // Control case of S5.6-05
        var s = Season();
        var toast = Toast(s, "Вася");

        s.Act(new ResolveManualEffect(toast, ManualEffectOutcome.Applied, null, s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.False(s.State.ManualEffects.ContainsKey(toast));
    }

    [Fact]
    public void The_admin_cannot_resolve_an_irl_effect_past_a_proof_waiting_for_review()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        Submit(s, "Вася", effect, Files(1));

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ResolveManualEffect(effect, ManualEffectOutcome.NotApplicable, "Закрываю", PlayerId: null)), RejectionCodes.IrlProofPending);
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ResolveManualEffect(effect, ManualEffectOutcome.Applied, "Закрываю", PlayerId: null)), RejectionCodes.IrlProofPending);
    }

    [Fact]
    public void The_admin_resolves_an_irl_effect_nobody_submitted_a_proof_for()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");

        s.Act(new ResolveManualEffect(effect, ManualEffectOutcome.NotApplicable, "Не будет караоке", PlayerId: null));

        ScenarioAssert.Accepted(s);
        Assert.False(s.State.ManualEffects.ContainsKey(effect));
    }

    // ---- Review (S5.6-02, S5.6-03, S5.6-04) ----

    [Fact]
    public void Approval_resolves_the_effect_as_applied_and_gives_nothing()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        var vasya = s.PlayerId("Вася");
        Submit(s, "Вася", effect, [Shot(1), Shot(2)], Video);
        s.Advance(TimeSpan.FromHours(2));
        var vasyaBefore = Standing(s, "Вася");
        var petyaBefore = Standing(s, "Петя");
        var runId = s.State.ManualEffects[effect].RunId;

        s.Act(new ReviewIrlProof(effect, Approved: true, "Отлично спел"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new IGameEvent[]
            {
                new IrlProofReviewed(effect, vasya, true, "Отлично спел", s.Clock.UtcNow),
                // D-684: the admin's comment stays in the review, which only the player and the admin see
                new ManualEffectResolved(effect, vasya, runId, ManualEffectOutcome.Applied, ""),
            },
            s.Last.Events);
        Assert.False(s.State.ManualEffects.ContainsKey(effect));
        Assert.Equal(vasyaBefore, Standing(s, "Вася"));
        Assert.Equal(petyaBefore, Standing(s, "Петя"));
    }

    [Fact]
    public void Approval_without_a_comment_resolves_with_an_empty_one()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        Submit(s, "Вася", effect, Files(1));

        Review(s, effect, approved: true);

        Assert.Null(Assert.Single(s.LastEvents<IrlProofReviewed>()).Comment);
        Assert.Equal(string.Empty, Assert.Single(s.LastEvents<ManualEffectResolved>()).Comment);
    }

    [Fact]
    public void A_reject_with_a_comment_keeps_the_effect_waiting_without_a_penalty()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        var vasya = s.PlayerId("Вася");
        Submit(s, "Вася", effect, [Shot(1)], Video);
        var submittedAt = s.Clock.UtcNow;
        s.Advance(TimeSpan.FromHours(1));
        var standing = Standing(s, "Вася");

        s.Act(new ReviewIrlProof(effect, Approved: false, "Видео закрыто"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new IrlProofReviewed(effect, vasya, false, "Видео закрыто", s.Clock.UtcNow), Assert.Single(s.Last.Events));
        Assert.Equal(new IrlProofState(IrlProofStatus.Rejected, [Shot(1)], Video, submittedAt, "Видео закрыто"), ProofOf(s, effect));
        Assert.Equal(standing, Standing(s, "Вася"));
    }

    [Fact]
    public void After_a_reject_the_player_submits_again_and_the_first_submission_stays_in_the_log()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        Submit(s, "Вася", effect, [Shot(1)], Video);
        var first = Assert.Single(s.LastEvents<IrlProofSubmitted>());
        Review(s, effect, approved: false, "Видео закрыто");
        s.Advance(TimeSpan.FromHours(1));

        Submit(s, "Вася", effect, [Shot(2), Shot(3)], "https://video.example/open");

        var proof = ProofOf(s, effect)!;
        Assert.Equal(
            (IrlProofStatus.Pending, Files(3).Skip(1).ToEquatableArray(), (string?)"https://video.example/open", s.Clock.UtcNow),
            (proof.Status, proof.Files, proof.Link, proof.SubmittedAt));
        Assert.Contains(first, s.Log);
        Assert.Equal(2, s.Log.OfType<IrlProofSubmitted>().Count());

        Review(s, effect, approved: true);
        Assert.False(s.State.ManualEffects.ContainsKey(effect));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_reject_without_a_comment_is_refused(string? comment)
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        Submit(s, "Вася", effect, Files(1));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ReviewIrlProof(effect, Approved: false, comment)), RejectionCodes.CommentRequired);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_review_comment_longer_than_the_limit_is_refused(bool approved)
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        Submit(s, "Вася", effect, Files(1));

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ReviewIrlProof(effect, approved, new string('я', Limits.MaxCommentLength + 1))), RejectionCodes.CommentTooLong);
    }

    [Fact]
    public void A_review_comment_of_the_longest_length_is_accepted()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        Submit(s, "Вася", effect, Files(1));

        Review(s, effect, approved: false, new string('я', Limits.MaxCommentLength));
    }

    [Fact]
    public void A_review_without_a_submission_is_refused()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ReviewIrlProof(effect, Approved: true)), RejectionCodes.IrlProofNotSubmitted);
    }

    [Fact]
    public void A_second_review_of_a_rejected_proof_is_refused()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        Submit(s, "Вася", effect, Files(1));
        Review(s, effect, approved: false, "Видео закрыто");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ReviewIrlProof(effect, Approved: true)), RejectionCodes.IrlProofNotSubmitted);
    }

    [Fact]
    public void A_review_of_an_unknown_or_resolved_effect_is_refused()
    {
        var s = Season();
        var effect = Karaoke(s, "Вася");
        Submit(s, "Вася", effect, Files(1));
        Review(s, effect, approved: true);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ReviewIrlProof(effect, Approved: true)), RejectionCodes.EffectNotPending);
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ReviewIrlProof(new Guid(0x0DEAD, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), Approved: true)), RejectionCodes.EffectNotPending);
    }

    // ---- The flag (S5.6-14, EV35, invariant 19) ----

    [Fact]
    public void With_events_off_submission_and_review_are_refused()
    {
        var s = Scenario.New().WithMapLength(10).WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithPlayers("Вася");
        var effect = new Guid(0x0DEAD, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitIrlProof(x.PlayerId("Вася"), effect, Files(1), Video)), RejectionCodes.FeatureDisabled);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ReviewIrlProof(effect, Approved: true)), RejectionCodes.FeatureDisabled);
    }

    // ---- A finished season (D-101) ----

    [Fact]
    public void A_finished_season_takes_no_submission_and_no_review()
    {
        // A season finished before D-603 may still hold a pending IRL effect and a proof waiting for review
        var s = Season();
        var waiting = Karaoke(s, "Вася");
        var submitted = Karaoke(s, "Петя");
        Submit(s, "Петя", submitted, Files(1));
        s.MoveStatusToForcingFinish(SeasonStatus.Finished);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitIrlProof(x.PlayerId("Вася"), waiting, Files(1))), RejectionCodes.SeasonClosed);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ReviewIrlProof(submitted, Approved: true)), RejectionCodes.SeasonClosed);
    }
}

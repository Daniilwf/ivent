using GameEvent.Engine.Achievements;
using GameEvent.Engine.Content;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;
using static GameEvent.Engine.Tests.Proofs.ProofSetup;

namespace GameEvent.Engine.Tests.Achievements;

/// <summary>D-927: only approval of a run longer than the configured limit earns this seasonal achievement.</summary>
public sealed class LongRunAchievementTests
{
    private const string Player = "Вася";
    private const string AchievementId = "long-run-test";
    private const int AwardCoins = 5;
    private const decimal HoursAtThreshold = 30m;
    private const decimal HoursAboveThreshold = 30.01m;
    private const string Definition = """
        {
          "id": "long-run-test", "kind": "achievement", "scope": "season",
          "name": "Длинная игра", "description": "Пройди игру длиннее 30 часов.",
          "effect": {
            "trigger": "proofApproved", "condition": { "stat": "runHours", "gt": 30 },
            "actions": [{ "type": "changeResource", "resource": "coins", "amount": 5 }]
          }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Approval_awards_only_when_the_saved_hours_strictly_exceed_thirty(bool aboveThreshold)
    {
        var hours = aboveThreshold ? HoursAboveThreshold : HoursAtThreshold;
        var s = Season(hours);
        var run = CompleteAndSubmit(s);
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        ScenarioAssert.Accepted(s);
        var before = s.Player(Player).Coins;

        ApproveAndAssert(s, run, before, aboveThreshold);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hours_corrected_before_approval_decide_the_award_and_later_edits_do_not(bool qualifyingAtApproval)
    {
        var initial = qualifyingAtApproval ? HoursAtThreshold : HoursAboveThreshold;
        var corrected = qualifyingAtApproval ? HoursAboveThreshold : HoursAtThreshold;
        var s = Season(initial);
        var run = CompleteAndSubmit(s);

        CorrectHours(s, run, corrected);
        AssertNoAward(s);
        var before = s.Player(Player).Coins;

        ApproveAndAssert(s, run, before, qualifyingAtApproval);
        var badges = s.Player(Player).Achievements;

        CorrectHours(s, run, initial);

        Assert.Empty(s.LastEvents<AchievementEarned>());
        Assert.DoesNotContain(s.LastEvents<CoinsChanged>(), change => change.Reason == CoinsReason.Achievement);
        Assert.Equal(badges, s.Player(Player).Achievements);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Fact]
    public void Undoing_approval_removes_the_badge_and_coins_and_reapproval_only_awards_once()
    {
        var s = Season(HoursAboveThreshold);
        var run = CompleteAndSubmit(s);
        var before = s.Player(Player).Coins;
        var submittedProof = s.State.Runs[run].Proof;
        ApproveAndAssert(s, run, before, earned: true);
        var approval = s.LastCommandId;

        s.Act(new UndoCommand(approval, "Undo proof approval"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(submittedProof, s.State.Runs[run].Proof);
        Assert.Empty(s.Player(Player).Achievements);
        Assert.Equal(before, s.Player(Player).Coins);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));

        ApproveAndAssert(s, run, before, earned: true);
        var awardedBadge = Assert.Single(s.Player(Player).Achievements);
        var nextRun = CompleteAndSubmit(s);
        before = s.Player(Player).Coins;
        Approve(s, nextRun);

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<ProofApproved>());
        Assert.Empty(s.LastEvents<AchievementEarned>());
        Assert.Equal(awardedBadge, Assert.Single(s.Player(Player).Achievements));
        Assert.Equal(before, s.Player(Player).Coins);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Approval_awards_the_partner_only_when_the_completed_run_was_split(bool partnerLeaves)
    {
        const string Partner = "Петя";
        const string Game = "Silent Hill";
        var rules = TestRuleset.Create().WithCoop();
        var s = Scenario.New(rules with { Features = rules.Features with { Achievements = true } })
            .WithCategory("Horror")
            .WithCoopGame(Game, HoursAboveThreshold, "Horror")
            .WithPlayers(Player, Partner)
            .WithContent(new ContentPack { Objects = [ContentJson.Parse<ObjectDefinition>(Definition)] });
        s.CoopRun(Player, Partner, Game);
        var run = s.Player(Player).ActiveRunId!.Value;
        if (partnerLeaves)
        {
            s.Act(new LeaveCoop(s.PlayerId(Partner)));
            ScenarioAssert.Accepted(s);
        }

        s.NextRandom(Enumerable.Repeat(1, 10).ToArray()).Complete(Player);
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<AchievementEarned>());
        Submit(s, Player, run, [Link]);
        ScenarioAssert.Accepted(s);
        var partnerCoins = s.Player(Partner).Coins;

        Approve(s, run);

        ScenarioAssert.Accepted(s);
        Assert.Equal(AchievementId, Assert.Single(s.Player(Player).Achievements).ObjectId);
        if (partnerLeaves)
        {
            Assert.Empty(s.Player(Partner).Achievements);
            Assert.Equal(partnerCoins, s.Player(Partner).Coins);
        }
        else
        {
            Assert.Equal(AchievementId, Assert.Single(s.Player(Partner).Achievements).ObjectId);
            Assert.Equal(partnerCoins + AwardCoins, s.Player(Partner).Coins);
        }

        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Fact]
    public void A_proof_approval_achievement_requires_a_strict_hours_condition()
    {
        var definition = ContentJson.Parse<ObjectDefinition>(Definition);
        var effect = definition.Effect!;
        var inclusive = definition with { Effect = effect with { Condition = effect.Condition! with { Gte = 30, Gt = null } } };

        Assert.Contains(ContentPublishing.Check(new ContentPack { Objects = [inclusive] }, achievements: true),
            error => error.Path == "$.objects[0].effect.condition");
    }

    [Fact]
    public void A_regular_effect_cannot_fire_on_proof_approval()
    {
        var effect = ContentJson.Parse<ObjectDefinition>(Definition) with { Kind = ObjectKind.Effect, Scope = null };

        Assert.Contains(ContentPublishing.Check(new ContentPack { Objects = [effect] }, achievements: true),
            error => error.Path == "$.objects[0].effect.trigger");
    }

    private static Scenario Season(decimal hours) => Scenario.New()
        .WithRuleset(rules => rules with { Features = rules.Features with { Achievements = true } })
        .WithCategory("Horror")
        .WithGame("Silent Hill", hours, "Horror")
        .WithGame("Alan Wake", hours, "Horror")
        .WithPlayers(Player)
        .WithContent(new ContentPack { Objects = [ContentJson.Parse<ObjectDefinition>(Definition)] });

    private static Guid CompleteAndSubmit(Scenario s)
    {
        var badges = s.Player(Player).Achievements;
        var run = CompleteRun(s, Player, Enumerable.Repeat(1, 10).ToArray());
        Assert.Empty(s.LastEvents<AchievementEarned>());
        Assert.Equal(badges, s.Player(Player).Achievements);
        Submit(s, Player, run, [Link]);
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<AchievementEarned>());
        Assert.Equal(badges, s.Player(Player).Achievements);
        return run;
    }

    private static void CorrectHours(Scenario s, Guid run, decimal hours)
    {
        s.Act(new CorrectRunHours(run, hours, "Сверено время прохождения"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(hours, s.State.Runs[run].Hours);
    }

    private static void ApproveAndAssert(Scenario s, Guid run, int coinsBefore, bool earned)
    {
        Approve(s, run);
        ScenarioAssert.Accepted(s);
        Assert.Equal(run, Assert.Single(s.LastEvents<ProofApproved>()).RunId);
        if (!earned)
        {
            AssertNoAward(s);
            Assert.Equal(coinsBefore, s.Player(Player).Coins);
            return;
        }

        var award = Assert.Single(s.LastEvents<AchievementEarned>());
        var coins = Assert.Single(s.LastEvents<CoinsChanged>(), change => change.Reason == CoinsReason.Achievement);
        Assert.Equal(s.PlayerId(Player), award.PlayerId);
        Assert.Equal(s.PlayerId(Player), coins.PlayerId);
        Assert.Equal(AchievementId, award.Achievement.ObjectId);
        Assert.Equal(s.Clock.UtcNow, award.Achievement.EarnedAt);
        Assert.Equal(AwardCoins, coins.Delta);
        Assert.Equal(award.Achievement, Assert.Single(s.Player(Player).Achievements));
        Assert.Equal(coinsBefore + AwardCoins, s.Player(Player).Coins);
    }

    private static void AssertNoAward(Scenario s)
    {
        Assert.Empty(s.LastEvents<AchievementEarned>());
        Assert.DoesNotContain(s.LastEvents<CoinsChanged>(), change => change.Reason == CoinsReason.Achievement);
        Assert.Empty(s.Player(Player).Achievements);
    }
}

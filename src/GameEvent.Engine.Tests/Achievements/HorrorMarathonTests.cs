using FsCheck.Xunit;
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

namespace GameEvent.Engine.Tests.Achievements;

/// <summary>The «Хоррор-марафон» example in CONTENT.md, with proof review, shared completions and undo.</summary>
public sealed class HorrorMarathonTests
{
    private const string AchievementId = "horror-marathon";
    private const string Roller = "Roller";
    private const string Partner = "Partner";
    private const string HorrorCoop = "Coop Horror";
    private const string FirstHorror = "First Horror";
    private const string SecondHorror = "Second Horror";
    private const string ThirdHorror = "Third Horror";
    private const string FourthHorror = "Fourth Horror";
    private const string DroppedHorror = "Dropped Horror";
    private const string OtherGame = "Other Game";
    private const string CompletedNonHorror = "Completed Non-Horror";
    private const int AchievementCoins = 5;

    [Fact]
    public void The_third_consecutive_horror_completion_awards_the_badge_and_coins_once()
    {
        var s = Season();
        s.CompleteTitle(Roller, FirstHorror, 1, 1);
        NoAward(s, Roller);
        s.CompleteTitle(Roller, SecondHorror, 1, 1);
        NoAward(s, Roller);

        s.CompleteTitle(Roller, ThirdHorror, 1, 1);

        AssertAward(s, Roller);
        Assert.Equal((3 * CoinsPerRun) + AchievementCoins, s.Player(Roller).Coins);
        s.CompleteTitle(Roller, FourthHorror, 1, 1);
        Assert.Empty(s.LastEvents<AchievementEarned>());
        Assert.Single(s.Player(Roller).Achievements);
        Assert.Equal((4 * CoinsPerRun) + AchievementCoins, s.Player(Roller).Coins);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Theory]
    [InlineData(DroppedHorror)]
    [InlineData(OtherGame)]
    public void A_mixed_completion_breaks_a_streak_and_a_drop_neither_adds_nor_breaks_it(string dropped)
    {
        var s = Season();
        s.CompleteTitle(Roller, FirstHorror, 1, 1);
        s.CompleteTitle(Roller, CompletedNonHorror, 1, 1);
        s.CompleteTitle(Roller, SecondHorror, 1, 1);
        var run = s.PlayTitle(Roller, dropped);
        s.Act(new DropRun(s.PlayerId(Roller)));
        ScenarioAssert.Accepted(s);
        Assert.Equal(RunStatus.Dropped, s.State.Runs[run].Status);
        NoAward(s, Roller);

        s.CompleteTitle(Roller, ThirdHorror, 1, 1);
        NoAward(s, Roller);
        s.CompleteTitle(Roller, FourthHorror, 1, 1);
        AssertAward(s, Roller);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_split_coop_completion_counts_toward_each_participants_own_streak(bool partnerLeaves)
    {
        var s = Season();
        s.CompleteTitle(Partner, SecondHorror, 1, 1);
        s.CompleteTitle(Partner, ThirdHorror, 1, 1);
        s.CompleteTitle(Roller, FirstHorror, 1, 1);
        s.CoopRun(Roller, Partner, HorrorCoop);
        if (partnerLeaves)
        {
            s.Act(new LeaveCoop(s.PlayerId(Partner)));
            ScenarioAssert.Accepted(s);
        }

        s.CompleteWith(Roller, 1, 1);

        Assert.Empty(s.Player(Roller).Achievements);
        if (partnerLeaves)
        {
            NoAward(s, Partner);
        }
        else
        {
            var split = Assert.Single(s.LastEvents<CoopRewardSplit>());
            Assert.Equal(s.PlayerId(Partner), split.PartnerId);
            AssertAward(s, Partner);
        }
    }

    [Fact]
    public void A_rejected_completion_drops_out_of_the_streak_and_a_granted_award_stays()
    {
        var s = Season();
        var rejectedBeforeAward = s.CompleteTitle(Roller, FirstHorror, 1, 1);
        s.CompleteTitle(Roller, SecondHorror, 1, 1);
        s.Act(new RejectProof(rejectedBeforeAward, Comment));
        ScenarioAssert.Accepted(s);
        Assert.Equal(RunStatus.Rejected, s.State.Runs[rejectedBeforeAward].Status);

        s.CompleteTitle(Roller, ThirdHorror, 1, 1);
        NoAward(s, Roller);
        var awardedRun = s.CompleteTitle(Roller, FourthHorror, 1, 1);
        AssertAward(s, Roller);
        var awardedBadge = Assert.Single(s.Player(Roller).Achievements);

        s.Act(new RejectProof(awardedRun, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Equal(RunStatus.Rejected, s.State.Runs[awardedRun].Status);
        Assert.Equal(awardedBadge, Assert.Single(s.Player(Roller).Achievements));
        Assert.Equal(AchievementCoins, Assert.Single(s.EffectiveLog.OfType<CoinsChanged>(), change => change.Reason == CoinsReason.Achievement).Delta);
        Assert.DoesNotContain(s.Last.Events.OfType<CoinsChanged>(), change => change.Reason == CoinsReason.Achievement);

        s.CompleteTitle(Roller, DroppedHorror, 1, 1);
        Assert.Empty(s.LastEvents<AchievementEarned>());
        Assert.Equal(awardedBadge, Assert.Single(s.Player(Roller).Achievements));
        Assert.Equal(AchievementCoins, Assert.Single(s.EffectiveLog.OfType<CoinsChanged>(), change => change.Reason == CoinsReason.Achievement).Delta);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Fact]
    public void Undoing_the_awarding_completion_removes_the_badge_and_coins_and_allows_a_fresh_award()
    {
        var s = Season();
        s.CompleteTitle(Roller, FirstHorror, 1, 1);
        s.CompleteTitle(Roller, SecondHorror, 1, 1);
        var run = s.PlayTitle(Roller, ThirdHorror);
        var coinsBefore = s.Player(Roller).Coins;
        s.CompleteWith(Roller, 1, 1);
        AssertAward(s, Roller);
        var completion = s.LastCommandId;

        s.Act(new UndoCommand(completion, "Undo completion"));

        ScenarioAssert.Accepted(s);
        NoAward(s, Roller);
        Assert.Equal((run, coinsBefore), (s.Player(Roller).ActiveRunId, s.Player(Roller).Coins));
        Assert.Empty(s.EffectiveLog.OfType<AchievementEarned>());
        s.CompleteWith(Roller, 1, 1);
        AssertAward(s, Roller);
        Assert.Single(s.EffectiveLog.OfType<AchievementEarned>());
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Fact]
    public void A_third_horror_completion_does_not_grant_the_award_while_achievements_are_disabled()
    {
        var s = Season();
        s.CompleteTitle(Roller, FirstHorror, 1, 1);
        s.CompleteTitle(Roller, SecondHorror, 1, 1);
        s.WithRuleset(rules => rules with { Features = rules.Features with { Achievements = false } });

        s.CompleteTitle(Roller, ThirdHorror, 1, 1);

        NoAward(s, Roller);
        Assert.Equal(3 * CoinsPerRun, s.Player(Roller).Coins);
    }

    [Fact]
    public void The_completion_that_reaches_first_place_does_not_grant_an_automatic_award()
    {
        var s = Season(mapLength: 7);
        s.CompleteTitle(Roller, FirstHorror, 1, 1);
        s.CompleteTitle(Roller, SecondHorror, 1, 1);

        s.CompleteTitle(Roller, ThirdHorror, 2, 2);

        Assert.Equal(1, s.Player(Roller).Finish?.Order);
        NoAward(s, Roller);
    }

    [Property(MaxTest = 48)]
    public void The_first_qualifying_streak_awards_exactly_one_seasonal_badge_in_a_sequence(byte[] script)
    {
        var games = (script ?? []).Take(8).Select((value, index) => (Title: $"Game {index}", Horror: (value & 1) == 1)).ToArray();
        var rules = TestRuleset.Create();
        var s = Scenario.New(rules with { Features = rules.Features with { Achievements = true } })
            .WithMapLength(60).WithCategory("Games");
        foreach (var game in games)
        {
            s.WithGame(game.Title, GameHours, game.Horror ? ["Games", "Horror"] : ["Games"]);
        }

        var streak = 0;
        var awarded = false;
        PublishFromLog(s.WithPlayers(Roller));
        foreach (var game in games)
        {
            streak = game.Horror ? streak + 1 : 0;
            var grantsNow = streak == 3 && !awarded;
            s.CompleteTitle(Roller, game.Title, 1, 1);
            Assert.Equal(grantsNow ? 1 : 0, s.LastEvents<AchievementEarned>().Count());
            awarded |= grantsNow;
            Assert.Equal(awarded ? 1 : 0, s.Player(Roller).Achievements.Count);
        }

        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    private static Scenario Season(int mapLength = 60)
    {
        var rules = TestRuleset.Create().WithCoop();
        var s = Scenario.New(rules with { Features = rules.Features with { Achievements = true } })
            .WithMapLength(mapLength)
            .WithCategory("Games")
            .WithCoopGame(HorrorCoop, GameHours, "Games", "Horror")
            .WithGame(FirstHorror, GameHours, "Games", "Horror")
            .WithGame(SecondHorror, GameHours, "Games", "Horror")
            .WithGame(ThirdHorror, GameHours, "Games", "Horror")
            .WithGame(FourthHorror, GameHours, "Games", "Horror")
            .WithGame(DroppedHorror, GameHours, "Games", "Horror")
            .WithGame(OtherGame, GameHours, "Games")
            .WithGame(CompletedNonHorror, GameHours, "Games")
            .WithPlayers(Roller, Partner);
        return PublishFromLog(s);
    }

    private static Scenario PublishFromLog(Scenario s)
    {
        // The stored definition isolates completion from publication, which AchievementPublishingTests covers through the real command.
        return s.AppendCraftedEvents(new ContentPublished(1, EconomyScenario.ExamplePack(AchievementId), "Season content"));
    }

    private static void NoAward(Scenario s, string player)
    {
        Assert.Empty(s.LastEvents<AchievementEarned>());
        Assert.DoesNotContain(s.LastEvents<CoinsChanged>(), change => change.Reason == CoinsReason.Achievement);
        Assert.Empty(s.Player(player).Achievements);
    }

    private static void AssertAward(Scenario s, string player)
    {
        var award = Assert.Single(s.LastEvents<AchievementEarned>());
        Assert.Equal(s.PlayerId(player), award.PlayerId);
        Assert.Equal(AchievementId, award.Achievement.ObjectId);
        Assert.Equal(award.Achievement, Assert.Single(s.Player(player).Achievements));
        var coins = Assert.Single(s.LastEvents<CoinsChanged>(), change => change.Reason == CoinsReason.Achievement);
        Assert.Equal((award.PlayerId, AchievementCoins), (coins.PlayerId, coins.Delta));
    }
}

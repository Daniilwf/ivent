using FsCheck.Xunit;
using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Finish;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Achievements;

/// <summary>The exact seasonal «Непробиваемый» example in CONTENT.md, and invariants 13, 19 and 20.</summary>
public sealed class SeasonalAchievementTests
{
    private const string AchievementId = "unbreakable";
    private const string AttackItem = "bird-thief";
    private const string ShieldItem = "shield";
    private const string Attacker = "Вася";
    private const string Recipient = "Петя";
    private const string OtherRecipient = "Маша";
    private const int AwardThreshold = 5;
    private const int DocumentedReward = 5;
    private const int StartingCoins = 20;

    [Theory]
    [InlineData(DocumentedReward)]
    [InlineData(9)]
    public void The_fifth_landed_attack_awards_the_badge_and_the_configured_coins_once(int reward)
    {
        var s = Season(reward);
        Attack(s, Recipient, AwardThreshold - 1);

        Assert.Equal((AwardThreshold - 1, StartingCoins - AwardThreshold + 1), Balance(s, Recipient));
        Assert.DoesNotContain(s.Log, IsAchievementAward);

        Attack(s, Recipient);

        var received = Assert.Single(s.LastEvents<HostileReceived>());
        Assert.Equal(s.PlayerId(Recipient), received.PlayerId);
        AssertAward(s, Recipient, reward);
        Assert.Equal((AwardThreshold, StartingCoins - AwardThreshold + reward), Balance(s, Recipient));

        Attack(s, Recipient);

        Assert.Equal((AwardThreshold + 1, StartingCoins - AwardThreshold + reward - 1), Balance(s, Recipient));
        Assert.Single(s.Log, IsAchievementAward);
        Assert.DoesNotContain(s.Last.Events, IsAchievementAward);
    }

    [Fact]
    public void A_shielded_fifth_attempt_neither_counts_as_a_hit_nor_grants_the_award()
    {
        var s = Season();
        Attack(s, Recipient, AwardThreshold - 1);
        var before = Balance(s, Recipient);

        InterceptAttack(s, Recipient);

        Assert.Single(s.LastEvents<HostileIntercepted>());
        Assert.Empty(s.LastEvents<HostileReceived>());
        Assert.Equal(before, Balance(s, Recipient));
        Assert.DoesNotContain(s.Log, IsAchievementAward);

        Attack(s, Recipient);
        AssertAward(s, Recipient, DocumentedReward);
    }

    [Fact]
    public void A_published_achievement_yields_no_badge_or_coins_while_achievements_are_disabled()
    {
        var s = Season();
        Attack(s, Recipient, AwardThreshold - 1);
        s.WithRuleset(r => r with { Features = r.Features with { Achievements = false } });

        Attack(s, Recipient, 2);

        Assert.Equal((AwardThreshold + 1, StartingCoins - AwardThreshold - 1), Balance(s, Recipient));
        Assert.DoesNotContain(s.Log, IsAchievementAward);
    }

    [Fact]
    public void Each_recipient_and_each_season_has_its_own_award()
    {
        var first = Season();
        Attack(first, Recipient, AwardThreshold);
        AssertAward(first, Recipient, DocumentedReward);

        Attack(first, OtherRecipient, AwardThreshold - 1);
        Assert.Single(first.Log, IsAchievementAward);
        Attack(first, OtherRecipient);
        AssertAward(first, OtherRecipient, DocumentedReward);
        Assert.Equal(2, first.Log.Count(IsAchievementAward));

        var another = Season(seasonNumber: 2);
        Assert.Equal(first.Player(Recipient).UserId, another.Player(Recipient).UserId);
        Attack(another, Recipient, AwardThreshold);
        AssertAward(another, Recipient, DocumentedReward);
        Assert.Single(another.Log, IsAchievementAward);
    }

    [Fact]
    public void Undoing_the_awarding_attack_restores_the_balance_and_allows_a_new_fifth_hit()
    {
        var s = Season();
        Attack(s, Recipient, AwardThreshold - 1);
        var before = Balance(s, Recipient);
        Attack(s, Recipient);
        AssertAward(s, Recipient, DocumentedReward);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));

        s.Act(new UndoCommand(s.LastCommandId, "Атака ошибочной цели"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(before, Balance(s, Recipient));
        Assert.DoesNotContain(s.EffectiveLog, IsAchievementAward);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));

        Attack(s, Recipient);
        AssertAward(s, Recipient, DocumentedReward);
        Assert.Single(s.EffectiveLog, IsAchievementAward);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Property(MaxTest = 72)]
    public void Award_count_matches_the_number_of_players_who_took_at_least_five_landed_attacks(byte[] script)
    {
        var s = Season();
        var hostileCounts = new int[2];
        foreach (var step in (script ?? []).Take(12))
        {
            var recipient = step % 2;
            var target = recipient == 0 ? Recipient : OtherRecipient;
            if (step % 3 == 0)
            {
                InterceptAttack(s, target);
            }
            else
            {
                Attack(s, target);
                hostileCounts[recipient]++;
            }

            var hits = hostileCounts[recipient];
            var reward = hits >= AwardThreshold ? DocumentedReward : 0;
            Assert.Equal((hits, StartingCoins - hits + reward), Balance(s, target));
            Assert.Equal(hostileCounts.Count(count => count >= AwardThreshold), s.Log.Count(IsAchievementAward));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_first_finisher_cannot_receive_a_fifth_hit_or_its_award(bool approve)
    {
        var season = Season(mapLength: FinishSetup.MapLength);
        Attack(season, Recipient, AwardThreshold - 1);
        var finishingRun = FinishSetup.FinishRun(season, Recipient);
        if (approve)
        {
            FinishSetup.Approve(season, finishingRun);
            ScenarioAssert.Accepted(season);
        }

        var finish = season.Player(Recipient).Finish!;
        Assert.Equal(1, finish.Order);
        Assert.Equal(approve, finish.Frozen);
        var before = Balance(season, Recipient);

        var attack = season.Give(Attacker, AttackItem);
        season.Use(Attacker, attack, target: Recipient);

        Assert.Equal(RejectionCodes.ItemInvalidTarget, season.Last.Rejection?.Code);
        Assert.Empty(season.Last.Events);
        Assert.Equal(before, Balance(season, Recipient));
        Assert.Empty(season.Player(Recipient).Achievements);
    }

    private static Scenario Season(int reward = DocumentedReward, int seasonNumber = 1, int? mapLength = null)
    {
        var s = EconomyScenario.New()
            .WithRuleset(r => r with { Features = r.Features with { Achievements = true } })
            .WithCategory("Horror")
            .WithGame("Silent Hill", 6, "Horror");
        if (mapLength is { } length)
        {
            s.WithMapLength(length);
        }

        s.Act(new CreateSeason(SequentialIds.Make(0x30000000, seasonNumber), $"Тестовый сезон {seasonNumber}", s.Ruleset));
        ScenarioAssert.Accepted(s);
        s.Act(new ChangeSeasonStatus(SeasonStatus.Active));
        ScenarioAssert.Accepted(s);
        s.WithPlayers(Attacker, Recipient, OtherRecipient)
            .WithCoins(Attacker, 0)
            .WithCoins(Recipient, StartingCoins)
            .WithCoins(OtherRecipient, StartingCoins);

        var examples = EconomyScenario.Examples();
        var achievement = examples[AchievementId];
        if (reward != DocumentedReward)
        {
            var effect = achievement.Effect!;
            var grant = Assert.IsType<ChangeResourceAction>(Assert.Single(effect.Actions));
            achievement = achievement with { Effect = effect with { Actions = [grant with { Amount = ContentValue.Of(reward) }] } };
        }
        return s.WithContent(new ContentPack
        {
            Objects = [examples[AttackItem], examples[ShieldItem], examples["shield-effect"], achievement],
        });
    }

    private static void Attack(Scenario s, string target, int times = 1)
    {
        for (var attempt = 0; attempt < times; attempt++)
        {
            s.Give(Attacker, AttackItem);
            s.NextRandom(1).Used(Attacker, AttackItem, target: target);
        }
    }

    private static void InterceptAttack(Scenario s, string target)
    {
        s.Give(target, ShieldItem);
        s.Used(target, ShieldItem);
        s.Give(Attacker, AttackItem);
        s.Used(Attacker, AttackItem, target: target);
    }

    private static (int Hits, int Coins) Balance(Scenario s, string player) =>
        (s.Player(player).Wallet.HostileReceived, s.Player(player).Coins);

    private static bool IsAchievementAward(IGameEvent gameEvent) =>
        EventCatalog.Describe(gameEvent.GetType()).Name == "achievement-earned";

    private static void AssertAward(Scenario s, string player, int reward)
    {
        Assert.Single(s.Last.Events, IsAchievementAward);
        var change = Assert.Single(s.LastEvents<CoinsChanged>(), e => e.PlayerId == s.PlayerId(player) && e.Delta > 0);
        Assert.Equal(reward, change.Delta);
    }
}

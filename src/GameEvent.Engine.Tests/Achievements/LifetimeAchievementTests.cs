using System.Text.Json;
using GameEvent.Engine.Achievements;
using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Achievements;

/// <summary>D-927: «Джекпот» uses all resolved completion dice and awards both parts in one command.</summary>
public sealed class LifetimeAchievementTests
{
    private const string Player = "Вася";
    private const string Jackpot = "jackpot";
    private const int JackpotCoins = 10;

    [Fact]
    public void The_documented_lifetime_achievement_can_be_published()
    {
        var s = Season(6);

        s.Act(new PublishContent(EconomyScenario.ExamplePack(Jackpot), "Enable jackpot"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(AchievementScope.AllTime, s.State.Catalog.Live(Jackpot)?.Scope);
    }

    [Fact]
    public void Two_maximum_dice_do_not_earn_the_jackpot()
    {
        var s = Playing(6);

        s.NextRandom(4, 4).Complete(Player);

        Assert.Equal([new Die(4, 4), new Die(4, 4)], Assert.Single(s.LastEvents<CompletionRolled>()).Dice);
        AssertOutcome(s, earned: false);
    }

    [Theory]
    [InlineData(4, 4, 6, true)]
    [InlineData(3, 4, 6, false)]
    [InlineData(4, 3, 6, false)]
    [InlineData(4, 4, 5, false)]
    public void Base_challenge_and_bonus_dice_each_use_their_own_maximum(
        int baseFace, int challengeFace, int bonusFace, bool earned)
    {
        var bonus = AfterDiceEffect("bonus-d6", DiceStage.Count, 1, sides: 6);
        var s = Playing(3, bonus);

        s.NextRandom(baseFace, challengeFace, bonusFace).Complete(Player, challengeDone: true);

        var run = Assert.Single(s.State.Runs.Values);
        Assert.Equal(new Die(4, baseFace), Assert.Single(run.Dice));
        Assert.Equal(new Die(4, challengeFace), Assert.Single(run.ChallengeDice));
        Assert.Equal(new Die(6, bonusFace), Assert.Single(run.DiceMods.ExtraDice));
        AssertOutcome(s, earned);
    }

    [Theory]
    [InlineData(3, 4, true)]
    [InlineData(4, 3, false)]
    public void An_after_dice_effect_can_change_whether_the_throw_qualifies(int rolledFace, int finalFace, bool earned)
    {
        var changeFace = AfterDiceEffect("change-first-die", DiceStage.SetOne, finalFace);
        var s = Playing(9, changeFace);

        s.NextRandom(rolledFace, 4, 4).Complete(Player);

        Assert.Equal(rolledFace, Assert.Single(s.LastEvents<CompletionRolled>()).Dice[0].Value);
        var run = Assert.Single(s.State.Runs.Values);
        Assert.Equal(finalFace, run.DiceMods.SetOne);
        Assert.Equal([finalFace, 4, 4], run.Dice.Select((die, index) => run.DiceMods.Face(die, index == 0)));
        AssertOutcome(s, earned);
    }

    [Fact]
    public void A_chain_limit_with_room_for_only_one_award_event_grants_neither_part()
    {
        var ordinary = Playing(9).NextRandom(4, 4, 3).Complete(Player);
        var limit = ordinary.Last.Events.Count + 1;
        var s = Playing(9);
        s.WithRuleset(rules => rules with { Effects = rules.Effects with { MaxEventsPerCommand = limit } });

        s.NextRandom(4, 4, 4).Complete(Player);

        Assert.Single(s.LastEvents<EffectChainCut>());
        AssertOutcome(s, earned: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_throw_that_reaches_the_first_finish_earns_the_jackpot_even_with_immediate_freeze(bool requireApproval)
    {
        var s = Playing(9, mapLength: 12, requireApproval: requireApproval);

        s.NextRandom(4, 4, 4).Complete(Player);

        var finish = s.Player(Player).Finish;
        Assert.NotNull(finish);
        Assert.Equal(!requireApproval, finish.Frozen);
        AssertOutcome(s, earned: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_first_finisher_can_earn_the_jackpot_until_proof_approval_freezes_coins(bool approve)
    {
        var s = Playing(9, mapLength: 11);
        s.NextRandom(4, 4, 3).Complete(Player);
        AssertOutcome(s, earned: false);
        var finish = s.Player(Player).Finish;
        Assert.NotNull(finish);
        if (approve)
        {
            s.Act(new ApproveProof(finish.RunId, null, "Proof checked"));
            ScenarioAssert.Accepted(s);
        }

        Assert.Equal(approve, s.Player(Player).Finish?.Frozen);
        var coins = s.Player(Player).Coins;
        s.Roll(Player).Start(Player).NextRandom(4, 4, 4).Complete(Player);

        AssertOutcome(s, earned: !approve);
        if (approve)
        {
            Assert.Equal(coins, s.Player(Player).Coins);
        }
    }

    [Fact]
    public void Undoing_the_awarding_throw_removes_both_parts_and_allows_a_new_award()
    {
        var s = Playing(9);
        var before = s.State;
        s.NextRandom(4, 4, 4).Complete(Player);
        AssertOutcome(s, earned: true);

        s.Act(new UndoCommand(s.LastCommandId, "Undo the completion"));

        ScenarioAssert.Accepted(s);
        // Undo restores the run and player, while the season's score ordering counter stays monotonic.
        Assert.Equal(before with { PointsChanges = before.PointsChanges + 1 }, s.State);
        Assert.DoesNotContain(s.EffectiveLog, IsLifetimeAward);
        Assert.DoesNotContain(s.EffectiveLog.OfType<CoinsChanged>(), change => change.Reason == CoinsReason.Achievement);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));

        s.NextRandom(4, 4, 4).Complete(Player);

        AssertOutcome(s, earned: true);
        Assert.Single(s.EffectiveLog, IsLifetimeAward);
    }

    private static Scenario Season(decimal hours, int mapLength = 60, bool requireApproval = true) => EconomyScenario.New()
        .WithRuleset(rules => rules with
        {
            Features = rules.Features with { Achievements = true, Challenges = true },
            Finish = rules.Finish with { RequireApprovalForFirst = requireApproval },
        })
        .WithMapLength(mapLength)
        .WithCategory("Horror")
        .WithGame("Silent Hill", hours, "Horror")
        .WithGame("Alan Wake", hours, "Horror")
        .WithPlayers(Player);

    private static Scenario Playing(decimal hours, ObjectDefinition? effect = null, int mapLength = 60, bool requireApproval = true)
    {
        var pack = EconomyScenario.ExamplePack(Jackpot);
        if (effect is not null)
        {
            pack = pack with { Objects = [.. pack.Objects, effect] };
        }

        // Isolate the award scenarios from the separate publication test, as with an existing season log.
        var s = Season(hours, mapLength, requireApproval).AppendCraftedEvents(new ContentPublished(1, pack, "Season content"));
        if (effect is not null)
        {
            s.Give(Player, effect.Id);
        }

        return s.Roll(Player).Start(Player);
    }

    private static ObjectDefinition AfterDiceEffect(string id, DiceStage stage, int value, int? sides = null) => new()
    {
        Id = id,
        Kind = ObjectKind.Effect,
        Name = id,
        Description = id,
        Rarity = Rarity.Common,
        Effect = new EffectSpec
        {
            Trigger = Trigger.AfterDice,
            Actions = [new ModifyDiceAction { When = DiceWhen.Current, Stage = stage, Value = ContentValue.Of(value), Sides = sides }],
        },
    };

    private static bool IsLifetimeAward(IGameEvent gameEvent) =>
        EventCatalog.Describe(gameEvent.GetType()).Name == "lifetime-achievement-earned";

    private static void AssertOutcome(Scenario s, bool earned)
    {
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<AchievementEarned>());
        var badges = s.Last.Events.Where(IsLifetimeAward);
        var coins = s.LastEvents<CoinsChanged>().Where(change => change.Reason == CoinsReason.Achievement);
        if (earned)
        {
            var badge = Assert.Single(badges);
            using var payload = JsonDocument.Parse(EventCodec.Encode(badge).Data);
            Assert.Equal(s.PlayerId(Player), payload.RootElement.GetProperty("playerId").GetGuid());
            Assert.Equal(Jackpot, payload.RootElement.GetProperty("achievement").GetProperty("objectId").GetString());
            var reward = Assert.Single(coins);
            Assert.Equal((s.PlayerId(Player), JackpotCoins), (reward.PlayerId, reward.Delta));
        }
        else
        {
            Assert.Empty(badges);
            Assert.Empty(coins);
        }

        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }
}

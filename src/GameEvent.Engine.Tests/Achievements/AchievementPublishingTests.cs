using GameEvent.Engine.Achievements;
using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Achievements;

public sealed class AchievementPublishingTests
{
    private static readonly IReadOnlyDictionary<string, ObjectDefinition> s_examples = EconomyScenario.Examples();

    [Fact]
    public void A_lifetime_achievement_with_a_seasonal_condition_is_refused()
    {
        var s = WithAchievements();
        var jackpot = s_examples["jackpot"];
        var pack = new ContentPack
        {
            Objects = [jackpot with { Effect = jackpot.Effect! with { Condition = s_examples["unbreakable"].Effect!.Condition } }],
        };

        Assert.Contains(ContentPublishing.Check(pack, achievements: true), e => e.Path == "$.objects[0].effect.condition");
        s.Act(new PublishContent(pack, "Проверка определения"));
        Assert.Equal(RejectionCodes.ContentInvalid, s.Last.Rejection?.Code);
        Assert.False(s.State.Catalog.Objects.ContainsKey("jackpot"));
    }

    [Fact]
    public void A_run_completed_achievement_with_a_hostile_hit_condition_is_refused()
    {
        var s = WithAchievements();
        var horror = s_examples["horror-marathon"];
        var pack = new ContentPack
        {
            Objects = [horror with { Effect = horror.Effect! with { Condition = s_examples["unbreakable"].Effect!.Condition } }],
        };

        s.Act(new PublishContent(pack, "Check definition"));

        Assert.Equal(RejectionCodes.ContentInvalid, s.Last.Rejection?.Code);
        Assert.False(s.State.Catalog.Objects.ContainsKey(horror.Id));
    }

    [Theory]
    [InlineData("unbreakable")]
    [InlineData("horror-marathon")]
    public void A_pack_of_achievements_can_be_published_without_items(string objectId)
    {
        var s = Scenario.New(TestRuleset.Create()).WithRuleset(r => r with { Features = r.Features with { Achievements = true } })
            .WithPlayers("Вася");
        var badges = EconomyScenario.ExamplePack(objectId);

        s.Act(new PublishContent(badges, "Сезонное достижение"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(ObjectKind.Achievement, s.State.Catalog.Live(objectId)?.Kind);

        s.Act(new PublishContent(EconomyScenario.ExamplePack("unbreakable", "bird-thief"), "Предмет при выключенном флаге"));
        Assert.Equal(RejectionCodes.FeatureDisabled, s.Last.Rejection?.Code);
    }

    [Fact]
    public void A_reward_that_exceeds_the_hard_chain_limit_is_rejected()
    {
        var example = s_examples["unbreakable"];
        var effect = example.Effect!;
        var grant = Assert.Single(effect.Actions);
        var pack = new ContentPack
        {
            Objects = [example with { Effect = effect with { Actions = [.. Enumerable.Repeat(grant, Limits.MaxEventsPerCommand)] } }],
        };

        Assert.Contains(ContentPublishing.Check(pack, achievements: true), problem => problem.Path == "$.objects[0].effect.actions");
    }

    [Fact]
    public void Inventory_adjustments_and_loot_wheels_reject_badges()
    {
        var s = WithAchievements().WithContent(EconomyScenario.ExamplePack("unbreakable"));
        var pack = EconomyScenario.ExamplePack("unbreakable") with
        {
            Wheels = [new WheelDefinition
            {
                Id = "test-wheel",
                Name = "Колесо",
                Entries = [new WheelEntrySpec { ObjectId = "unbreakable", Weight = 1 }],
            }],
        };

        Assert.Contains(ContentPublishing.Check(pack, achievements: true), e => e.Path == "$.wheels[0].entries[0].objectId");
        s.Act(new AdjustInventory(s.PlayerId("Петя"), "unbreakable", null, "Попытка выдать значок"));
        Assert.Equal(RejectionCodes.ItemNotAnItem, s.Last.Rejection?.Code);
        Assert.Empty(s.Player("Петя").Wallet.Inventory);
        Assert.Empty(s.Player("Петя").Achievements);
    }

    [Fact]
    public void A_shop_cell_cannot_hand_out_an_achievement_as_inventory()
    {
        var map = MapBuilder.New().Path("start", "a", "shop", "b", "finish").Build();
        map = map with
        {
            Cells = [.. map.Cells.Select(cell => cell.Id == "shop" ? cell with { Type = CellType.Shop, Grants = "unbreakable" } : cell)],
        };
        var s = EconomyScenario.New().WithRuleset(r => r with { Features = r.Features with { Achievements = true } })
            .WithMap(map).WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася")
            .WithContent(EconomyScenario.ExamplePack("unbreakable"));

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Complete("Вася");

        Assert.Equal("shop", s.Player("Вася").CellId);
        Assert.Empty(s.LastEvents<ObjectGiven>());
        Assert.Empty(s.Player("Вася").Achievements);
    }

    [Fact]
    public void A_limit_that_fits_only_one_reward_event_cuts_both_and_a_later_hit_can_award_the_badge()
    {
        var s = WithAchievements().WithCoins("Петя", 20)
            .WithContent(EconomyScenario.ExamplePack("unbreakable", "bird-thief"));
        for (var hit = 0; hit < 4; hit++)
        {
            Attack(s);
        }

        var normalLimit = s.Ruleset.Effects.MaxEventsPerCommand;
        var attackEvents = s.Last.Events.Count;
        s.WithRuleset(r => r with { Effects = r.Effects with { MaxEventsPerCommand = attackEvents + 1 } });
        Attack(s);

        Assert.Equal(new EffectChainCut(EffectChainLimit.Events, 1, attackEvents), Assert.Single(s.LastEvents<EffectChainCut>()));
        Assert.Equal(5, s.Player("Петя").Wallet.HostileReceived);
        Assert.Empty(s.Player("Петя").Achievements);
        Assert.Empty(s.LastEvents<AchievementEarned>());
        Assert.DoesNotContain(s.LastEvents<CoinsChanged>(), change => change.Reason == CoinsReason.Achievement);

        s.WithRuleset(r => r with { Effects = r.Effects with { MaxEventsPerCommand = normalLimit } });
        Attack(s);

        var award = Assert.Single(s.LastEvents<AchievementEarned>());
        Assert.Equal(award.Achievement, Assert.Single(s.Player("Петя").Achievements));
        Assert.Equal(5, Assert.Single(s.LastEvents<CoinsChanged>(), coin => coin.Reason == CoinsReason.Achievement).Delta);
    }

    [Fact]
    public void The_awarded_badge_survives_republication_of_a_renamed_definition()
    {
        var pack = EconomyScenario.ExamplePack("unbreakable", "bird-thief");
        var s = WithAchievements().WithCoins("Петя", 20).WithContent(pack);
        for (var hit = 0; hit < 5; hit++)
        {
            Attack(s);
        }

        var earned = Assert.Single(s.Player("Петя").Achievements);
        s.WithContent(pack with
        {
            Objects = [s_examples["bird-thief"], s_examples["unbreakable"] with { Name = "Переименованный значок" }],
        });
        Attack(s);

        Assert.Empty(s.LastEvents<AchievementEarned>());
        Assert.Equal(earned, Assert.Single(s.Player("Петя").Achievements));
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    private static Scenario WithAchievements() => EconomyScenario.New()
        .WithRuleset(r => r with { Features = r.Features with { Achievements = true } })
        .WithPlayers("Вася", "Петя");

    private static void Attack(Scenario s)
    {
        s.Give("Вася", "bird-thief");
        s.NextRandom(1).Used("Вася", "bird-thief", target: "Петя");
    }
}

using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rulesets;

/// <summary>
/// The safety ceilings of the economy and the bets (SEC-5, D-651): every number the shop, the bets and reflection compute
/// with has a floor and a ceiling, each reported at its JSON path. Below the floor a shop roll paid coins or a bet divided by
/// zero; over the ceiling a lot's lifetime, a payout or a weight sum went out of range inside someone's command.
/// </summary>
public class RulesetValidatorTests
{
    private static Ruleset Shop(Ruleset r, Func<ShopRules, ShopRules> change) => r with { Economy = r.Economy with { Shop = change(r.Economy.Shop) } };

    private static Ruleset Bets(Ruleset r, Func<BetRules, BetRules> change) => r with { Bets = change(r.Bets) };

    private static Ruleset Weights(Ruleset r, Func<RarityWeights, RarityWeights> change) =>
        r with { Economy = r.Economy with { RarityWeights = change(r.Economy.RarityWeights) } };

    /// <summary>A field, how to set it, its floor and its ceiling.</summary>
    private static readonly (string Path, Func<Ruleset, int, Ruleset> Set, int Floor, int Ceiling)[] s_fields =
    [
        ("economy.shop.rollCost", (r, v) => Shop(r, s => s with { RollCost = v }), 0, 1_000_000),
        ("economy.shop.rerollCostStep", (r, v) => Shop(r, s => s with { RerollCostStep = v }), 0, 1_000_000),
        ("economy.shop.lotsPerRoll", (r, v) => Shop(r, s => s with { LotsPerRoll = v }), 1, 10),
        ("economy.shop.lotLifetimeMinutes", (r, v) => Shop(r, s => s with { LotLifetimeMinutes = v }), 1, 7 * 24 * 60),
        ("economy.inventoryLimit", (r, v) => r with { Economy = r.Economy with { InventoryLimit = v } }, 0, 100),
        ("economy.rarityWeights.common", (r, v) => Weights(r, w => w with { Common = v }), 0, 1_000_000),
        ("economy.rarityWeights.epic", (r, v) => Weights(r, w => w with { Epic = v }), 0, 1_000_000),
        ("economy.rarityWeights.legendary", (r, v) => Weights(r, w => w with { Legendary = v }), 0, 1_000_000),
        ("bets.maxStake", (r, v) => Bets(r, b => b with { MaxStake = v }), 1, 1_000_000),
        ("bets.windowHoursAfterRoll", (r, v) => Bets(r, b => b with { WindowHoursAfterRoll = v }), 0, int.MaxValue),
        ("bets.maxOpenBetsPerPlayer", (r, v) => Bets(r, b => b with { MaxOpenBetsPerPlayer = v }), 0, int.MaxValue),
        ("bets.deadlineOptionsDays[0]", (r, v) => Bets(r, b => b with { DeadlineOptionsDays = [v] }), 1, 365),
        ("effects.reflect.multiplier", (r, v) => r with { Effects = r.Effects with { Reflect = new CoinReflect { CoinsAtMost = -30, Multiplier = v } } }, 1, 10),
    ];

    public static TheoryData<string, int> OutOfRange()
    {
        var data = new TheoryData<string, int>();
        foreach (var (path, _, floor, ceiling) in s_fields)
        {
            data.Add(path, -1);
            if (floor > 0)
            {
                data.Add(path, 0);
            }

            if (ceiling < int.MaxValue)
            {
                data.Add(path, ceiling + 1);
                data.Add(path, int.MaxValue);
            }

            data.Add(path, int.MinValue);
        }

        return data;
    }

    public static TheoryData<string, int> AtTheEdges()
    {
        var data = new TheoryData<string, int>();
        foreach (var (path, _, floor, ceiling) in s_fields)
        {
            data.Add(path, floor);
            data.Add(path, ceiling);
        }

        return data;
    }

    private static IReadOnlyList<RulesetError> Errors(string path, int value) =>
        RulesetValidator.Validate(s_fields.Single(f => f.Path == path).Set(TestRuleset.Create(), value));

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void A_number_out_of_its_range_is_an_error_at_its_path(string path, int value)
    {
        var errors = Errors(path, value);

        Assert.True(errors.Any(e => e.Path == path), $"{path} = {value}: expected an error there, got [{string.Join(", ", errors.Select(e => e.Path))}]");
        Assert.All(errors, e => Assert.False(string.IsNullOrWhiteSpace(e.Message)));
    }

    [Theory]
    [MemberData(nameof(AtTheEdges))]
    public void A_number_at_the_edge_of_its_range_is_valid(string path, int value)
    {
        Assert.DoesNotContain(Errors(path, value), e => e.Path == path);
    }

    [Fact]
    public void A_negative_reroll_step_that_made_a_shop_roll_pay_is_refused()
    {
        var r = TestRuleset.Create();

        Assert.Contains(RulesetValidator.Validate(Shop(r, s => s with { RerollCostStep = -5 })), e => e.Path == "economy.shop.rerollCostStep");
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 3, 3 })]
    public void Bet_deadlines_are_a_non_empty_list_without_repeats(int[] days)
    {
        var r = TestRuleset.Create();

        Assert.Contains(RulesetValidator.Validate(Bets(r, b => b with { DeadlineOptionsDays = [.. days] })), e => e.Path == "bets.deadlineOptionsDays");
    }

    [Fact]
    public void A_zero_day_deadline_that_divided_by_zero_is_refused_at_its_index()
    {
        var r = TestRuleset.Create();

        Assert.Contains(RulesetValidator.Validate(Bets(r, b => b with { DeadlineOptionsDays = [1, 0] })), e => e.Path == "bets.deadlineOptionsDays[1]");
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(100.01, false)]
    [InlineData(1e20, false)]
    public void A_payout_multiplier_is_at_most_a_hundred(double multiplier, bool valid)
    {
        var r = TestRuleset.Create();
        var steps = Bets(r, b => b with { PayoutByHoursPerDay = [new PayoutStep { UpTo = 1, Multiplier = 2 }, new PayoutStep { UpTo = null, Multiplier = (decimal)multiplier }] });

        Assert.Equal(!valid, RulesetValidator.Validate(steps).Any(e => e.Path == "bets.payoutByHoursPerDay[1].multiplier"));
    }

    [Fact]
    public void The_default_ruleset_is_within_every_bound()
    {
        Assert.Empty(RulesetValidator.Validate(RulesetJson.Default()));
        Assert.Empty(RulesetValidator.Validate(TestRuleset.Create()));
    }
}

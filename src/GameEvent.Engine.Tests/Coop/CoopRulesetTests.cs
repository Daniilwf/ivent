using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// The coop numbers of the ruleset (D-521, D-523, D-540, Н-31): with <c>features.coop</c> on, <c>reward.coop.coinsShare</c>
/// in (0, 1] and <c>interactions.maxOpenInvitesPerPlayer</c> ≥ 1 are required; with it off a season created before coop
/// reads as it did. There is no <c>reward.coop.partnerMayHaveOwnRun</c>: the partner never has a run of his own, and a config
/// naming it is refused (the ruleset JSON refuses unknown fields).
/// </summary>
public class CoopRulesetTests
{
    private static Ruleset Coop(Func<Ruleset, Ruleset>? change = null)
    {
        var rules = TestRuleset.Create().WithCoop();
        return change is null ? rules : change(rules);
    }

    [Fact]
    public void The_coop_numbers_of_the_decisions_are_valid()
    {
        Assert.Empty(RulesetValidator.Validate(Coop()));
    }

    [Fact]
    public void Without_coop_the_numbers_may_be_missing()
    {
        var rules = TestRuleset.Create();
        Assert.Null(rules.Reward.Coop.CoinsShare);
        Assert.Null(rules.Interactions.MaxOpenInvitesPerPlayer);

        Assert.Empty(RulesetValidator.Validate(rules));
    }

    [Fact]
    public void With_coop_on_the_coins_share_is_required()
    {
        var errors = RulesetValidator.Validate(Coop(r => r with { Reward = r.Reward with { Coop = r.Reward.Coop with { CoinsShare = null } } }));

        Assert.Contains(errors, e => e.Path == "reward.coop.coinsShare");
    }

    [Fact]
    public void With_coop_on_the_limit_of_open_invitations_is_required()
    {
        var errors = RulesetValidator.Validate(Coop(r => r with { Interactions = r.Interactions with { MaxOpenInvitesPerPlayer = null } }));

        Assert.Contains(errors, e => e.Path == "interactions.maxOpenInvitesPerPlayer");
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(-0.5, false)]
    [InlineData(1.01, false)]
    [InlineData(0.01, true)]
    [InlineData(0.5, true)]
    [InlineData(1.0, true)]
    public void The_coins_share_is_above_zero_and_at_most_one(double share, bool valid)
    {
        var errors = RulesetValidator.Validate(Coop(r => r.WithCoop(coinsShare: (decimal)share)));

        Assert.Equal(!valid, errors.Any(e => e.Path == "reward.coop.coinsShare"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void At_least_one_open_invitation_is_allowed(int max, bool valid)
    {
        var errors = RulesetValidator.Validate(Coop(r => r.WithCoop(maxOpenInvites: max)));

        Assert.Equal(!valid, errors.Any(e => e.Path == "interactions.maxOpenInvitesPerPlayer"));
    }

    [Fact]
    public void A_ruleset_naming_partner_may_have_own_run_is_refused()
    {
        // Н-31, D-540: the field does not exist; the partner's own run is a hard rule of the engine
        var root = JsonNode.Parse(TestRuleset.Json)!.AsObject();
        root["reward"]!["coop"]!["partnerMayHaveOwnRun"] = true;

        var error = Assert.ThrowsAny<JsonException>(() => RulesetJson.Parse(root.ToJsonString()));

        Assert.Contains("partnerMayHaveOwnRun", error.Message, StringComparison.Ordinal);
    }
}

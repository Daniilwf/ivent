using System.Text.Json.Nodes;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// The partner board in the ruleset (EV35; D-524, S5.11-06): <c>features.partnerBoard</c> needs <c>features.coop</c>; with
/// the flag on, <c>partnerBoard.requestTtlHours</c> is required and lies in 1…720; with it off the block may be missing, so
/// a season created before the board reads as it did.
/// </summary>
public class PartnerBoardRulesetTests
{
    [Fact]
    public void The_board_with_coop_and_the_numbers_of_the_decision_is_valid()
    {
        Assert.Empty(RulesetValidator.Validate(TestRuleset.Create().WithPartnerBoard()));
    }

    [Fact]
    public void Without_the_board_its_block_may_be_missing()
    {
        var rules = TestRuleset.Create();
        Assert.False(rules.Features.PartnerBoard);
        Assert.Null(rules.PartnerBoard);

        Assert.Empty(RulesetValidator.Validate(rules));
    }

    [Fact]
    public void The_board_without_coop_is_refused()
    {
        var rules = TestRuleset.Create().WithPartnerBoard();
        rules = rules with { Features = rules.Features with { Coop = false } };

        var errors = RulesetValidator.Validate(rules);

        Assert.Contains(errors, e => e.Path == "features.partnerBoard");
    }

    [Fact]
    public void With_the_board_on_the_request_term_is_required()
    {
        var rules = TestRuleset.Create().WithPartnerBoard() with { PartnerBoard = null };

        var errors = RulesetValidator.Validate(rules);

        Assert.Contains(errors, e => e.Path == "partnerBoard.requestTtlHours");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(721, false)]
    [InlineData(1, true)]
    [InlineData(48, true)]
    [InlineData(720, true)]
    public void The_request_term_is_from_one_hour_to_thirty_days(int hours, bool valid)
    {
        var errors = RulesetValidator.Validate(TestRuleset.Create().WithPartnerBoard(requestTtlHours: hours));

        Assert.Equal(!valid, errors.Any(e => e.Path == "partnerBoard.requestTtlHours"));
    }

    [Fact]
    public void The_ruleset_json_reads_the_board_block()
    {
        var root = JsonNode.Parse(TestRuleset.Json)!.AsObject();
        root["features"]!["coop"] = true;
        root["features"]!["partnerBoard"] = true;
        root["partnerBoard"] = new JsonObject { ["requestTtlHours"] = 12 };

        var rules = RulesetJson.Parse(root.ToJsonString());

        Assert.True(rules.Features.PartnerBoard);
        Assert.Equal(12, rules.PartnerBoard!.RequestTtlHours);
    }
}

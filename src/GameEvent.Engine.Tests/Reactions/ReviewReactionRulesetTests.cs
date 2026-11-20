using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Reactions;

public sealed class ReviewReactionRulesetTests
{
    [Fact]
    public void Seasons_can_enable_review_reactions_with_the_configured_reaction_set()
    {
        var rules = TestRuleset.Create();
        var enabled = rules with { Features = rules.Features with { Reactions = true } };

        Assert.Empty(RulesetValidator.Validate(enabled));
    }
}

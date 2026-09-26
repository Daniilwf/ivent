using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Tests.Rulesets;

/// <summary>docs/ruleset.default.json ships inside the engine and is playable by this build.</summary>
public class DefaultRulesetTests
{
    [Fact]
    public void Default_ruleset_parses_from_the_embedded_docs_file()
    {
        var ruleset = RulesetJson.Default();

        Assert.True(ruleset.Version >= 1);
        Assert.True(ruleset.Map.LinearLength >= 1);
        Assert.True(ruleset.Reward.DiceCount.HoursPerDie > 0);
        Assert.True(ruleset.Reward.DiceCount.Min <= ruleset.Reward.DiceCount.Max);
    }

    [Fact]
    public void Default_ruleset_limits_unchecked_runs_to_two()
    {
        // D-134: the customer's «вариант 2, N = 2»
        Assert.Equal(2, RulesetJson.Default().Season.MaxUncheckedRuns);
    }

    [Fact]
    public void Embedded_default_is_the_docs_file()
    {
        var docs = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "ruleset.default.json"));

        Assert.Equal(Normalize(docs), Normalize(RulesetJson.DefaultJson()));
    }

    [Fact]
    public void Default_ruleset_is_supported_by_this_build()
    {
        Assert.Empty(RulesetSupport.Unsupported(RulesetJson.Default()));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GameEvent.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("GameEvent.slnx not found above the test output.");
    }
}

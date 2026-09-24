using GameEvent.Architecture.Tests.Fixtures;

namespace GameEvent.Architecture.Tests;

public class BannedMemberTests
{
    [Fact]
    public void Engine_IL_contains_no_banned_members()
    {
        var violations = BannedMemberScanner.FindViolations(Assemblies.Engine.Location);

        Assert.Empty(violations);
    }

    [Fact]
    public void Scanner_detects_each_kind_of_violation()
    {
        var fixtureAssembly = typeof(ViolatingFixture).Assembly.Location;

        var violations = BannedMemberScanner.FindViolations(
            fixtureAssembly,
            t => t.FullName == typeof(ViolatingFixture).FullName);

        Assert.Contains(violations, v => v.Contains("System.DateTime::get_UtcNow", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("System.Guid::NewGuid", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("System.Random::", StringComparison.Ordinal));
    }
}

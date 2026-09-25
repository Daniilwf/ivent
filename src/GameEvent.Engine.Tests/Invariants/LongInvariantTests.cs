using System.Globalization;
using System.Reflection;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.FSharp.Core;

namespace GameEvent.Engine.Tests.Invariants;

/// <summary>
/// C13: the long run of the random games (<c>npm run test:invariants:long</c>, nightly). Every <see cref="PropertyAttribute"/>
/// of the engine tests — the invariant suites, undo, the chain limits — is checked again on <c>INVARIANT_RUNS</c> games
/// (default 20 000) with longer scripts (<c>INVARIANT_SIZE</c>, default 150 commands at most), through the very same
/// property body and generators. The short runs (their <c>MaxTest</c>) stay in <c>test:fast</c>. A failure is the shrunk
/// game printed as builder code (<see cref="Support.Scenario.Explained"/>).
/// </summary>
public class LongInvariantTests
{
    private const int DefaultRuns = 20_000;

    private const int DefaultSize = 150;

    public static TheoryData<string> Properties() => [.. PropertyMethods().Select(Name)];

    [Theory]
    [MemberData(nameof(Properties))]
    [Trait("Category", "Long")]
    public void Holds_over_the_long_run(string property)
    {
        var method = PropertyMethods().Single(m => Name(m) == property);
        var target = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!);
        var config = Config.QuickThrowOnFailure
            .WithMaxTest(FromEnvironment("INVARIANT_RUNS", DefaultRuns))
            .WithEndSize(FromEnvironment("INVARIANT_SIZE", DefaultSize))
            .WithQuietOnSuccess(true)
            .WithParallelRunConfig(FSharpOption<ParallelRunConfig>.Some(new ParallelRunConfig(Environment.ProcessorCount)));

        Check.Method(config, method, target is null ? FSharpOption<object>.None : FSharpOption<object>.Some(target));
    }

    [Fact]
    public void The_long_run_covers_every_invariant_property()
    {
        // A property that the reflection misses would silently get no long run
        var names = PropertyMethods().Select(Name).ToList();

        Assert.Contains("PlayerAdminInvariantTests.Invariants_hold_in_the_combined_game", names);
        Assert.Contains("SliceInvariantTests.Invariants_hold_after_every_command", names);
        Assert.Contains("UndoTests.An_accepted_undo_equals_the_log_without_the_command", names);
        Assert.Contains("ChainLimitTests.Chains_stay_within_the_limits", names);
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    private static IEnumerable<MethodInfo> PropertyMethods() =>
        typeof(LongInvariantTests).Assembly.GetTypes()
            .Where(t => t.IsPublic && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<PropertyAttribute>() is not null)
            .OrderBy(Name, StringComparer.Ordinal);

    private static string Name(MethodInfo method) => $"{method.DeclaringType!.Name}.{method.Name}";

    private static int FromEnvironment(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
}

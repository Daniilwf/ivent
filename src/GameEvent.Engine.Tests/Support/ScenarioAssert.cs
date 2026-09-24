using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Tests.Support;

/// <summary>Shared assertions for scenario outcomes.</summary>
public static class ScenarioAssert
{
    /// <summary>
    /// The last command was refused with <paramref name="code"/>: no events, and the state is exactly
    /// <paramref name="before"/>. The scenario log must not grow either.
    /// </summary>
    public static void Rejected(Scenario scenario, SeasonState before, int logLengthBefore, string code)
    {
        Assert.False(scenario.Last.IsAccepted, "The command was expected to be rejected.");
        Assert.Equal(code, scenario.Last.Rejection!.Code);
        Assert.Empty(scenario.Last.Events);
        Assert.Equal(before, scenario.Last.State);
        Assert.Equal(before, scenario.State);
        Assert.Equal(logLengthBefore, scenario.Log.Count);
    }

    /// <summary>Executes <paramref name="act"/> and checks it is refused with <paramref name="code"/> and changes nothing.</summary>
    public static void RejectsWithoutChanges(Scenario scenario, Func<Scenario, Scenario> act, string code)
    {
        var before = scenario.State;
        var logLength = scenario.Log.Count;
        act(scenario.ExpectRejection());
        Rejected(scenario, before, logLength, code);
    }

    /// <summary>The last command was accepted.</summary>
    public static void Accepted(Scenario scenario) =>
        Assert.True(scenario.Last.IsAccepted, $"The command was rejected: {scenario.Last.Rejection}");
}

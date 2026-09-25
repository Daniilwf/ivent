using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Lifecycle;

/// <summary>
/// Shared setup of the season lifecycle tests (C10, D-101): a running season with twelve 6-hour games on a 60-step map
/// (test ruleset: normal d4, 2 dice per run, coins 6 per run), Вася, Петя and Маша, and a deadline one day after the start.
/// </summary>
internal static class LifecycleSetup
{
    public static readonly TimeSpan UntilDeadline = TimeSpan.FromDays(1);

    private static readonly string[] s_titles =
    [
        "Silent Hill", "Alan Wake", "Dead Space", "Outlast", "Amnesia", "Soma",
        "Prey", "Doom", "Quake", "Portal", "Limbo", "Inside",
    ];

    public static Scenario New(Func<Ruleset, Ruleset>? ruleset = null)
    {
        var s = Scenario.New();
        if (ruleset is not null)
        {
            s.WithRuleset(ruleset);
        }

        s.WithCategory("Horror");
        foreach (var title in s_titles)
        {
            s.WithGame(title, 6, "Horror");
        }

        s.WithPlayers("Вася", "Петя", "Маша");
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow + UntilDeadline));
        ScenarioAssert.Accepted(s);
        return s;
    }

    public static DateTimeOffset Deadline(Scenario s) => s.State.Deadline!.Value;

    /// <summary>Moves the clock exactly to the deadline.</summary>
    public static void ToDeadline(Scenario s) => s.Advance(Deadline(s) - s.Clock.UtcNow);

    /// <summary>Moves the clock to one second before the deadline.</summary>
    public static void JustBeforeDeadline(Scenario s) => s.Advance(Deadline(s) - s.Clock.UtcNow - TimeSpan.FromSeconds(1));

    /// <summary>The player rolls, starts and completes with the given dice (the clock does not move).</summary>
    public static Guid CompleteRun(Scenario s, string player, int[] dice)
    {
        s.Roll(player).Start(player);
        var runId = s.Player(player).ActiveRunId!.Value;
        s.NextRandom(dice).Complete(player);
        ScenarioAssert.Accepted(s);
        return runId;
    }

    /// <summary>The deadline passes and the scheduler's command closes the season.</summary>
    public static void Close(Scenario s)
    {
        ToDeadline(s);
        s.Act(new ReachDeadline());
        ScenarioAssert.Accepted(s);
        Assert.Equal(SeasonStatus.Closing, s.State.Status);
    }

    public static Scenario Approve(Scenario s, Guid runId) => s.Act(new ApproveProof(runId, null, "Видел на стриме"));

    public static Scenario Reject(Scenario s, Guid runId) => s.Act(new RejectProof(runId, "На скрине другая игра"));

    /// <summary>A command is refused with <paramref name="code"/>: no events, nothing changes.</summary>
    public static void Refused(Scenario s, ICommand command, string code) =>
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(command), code);
}

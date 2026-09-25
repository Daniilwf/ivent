using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Proofs;

/// <summary>
/// Shared setup of the proof tests (C8, D-98): Вася, Петя and Маша in a running season; Вася completes a run with the
/// dice the test scripts. Test ruleset: hoursPerDie 3, normal d4, hard d6, extreme d6 with a good event; coins 1 per
/// hour, min 3; a linear map of 60 steps.
/// </summary>
internal static class ProofSetup
{
    public const string Link = "https://imgur.com/a/credits";
    public const string Note = "Титры";
    public const string Comment = "На скрине другая игра";

    /// <summary>Вася completes «Silent Hill» of <paramref name="hours"/> hours on <paramref name="difficulty"/>.</summary>
    public static (Scenario S, Guid RunId) Completed(
        int[] dice,
        Difficulty difficulty = Difficulty.Normal,
        decimal hours = 6,
        Func<Ruleset, Ruleset>? ruleset = null,
        bool onlyGame = false,
        bool challenge = false)
    {
        var s = Scenario.New();
        if (ruleset is not null)
        {
            s.WithRuleset(ruleset);
        }

        s.WithCategory("Horror").WithGame("Silent Hill", hours, "Horror");
        if (!onlyGame)
        {
            s.WithGame("Alan Wake", hours, "Horror").WithGame("Dead Space", hours, "Horror");
        }

        s.WithPlayers("Вася", "Петя", "Маша");
        var runId = CompleteRun(s, "Вася", dice, difficulty, challenge);
        return (s, runId);
    }

    /// <summary>The player rolls, starts and completes with the given dice; the clock moves an hour after.</summary>
    public static Guid CompleteRun(Scenario s, string player, int[] dice, Difficulty difficulty = Difficulty.Normal, bool challenge = false)
    {
        s.Roll(player).Start(player);
        var runId = s.Player(player).ActiveRunId!.Value;
        s.NextRandom(dice).Complete(player, difficulty, challengeDone: challenge);
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(1));
        return runId;
    }

    public static Scenario Submit(Scenario s, string player, Guid runId, EquatableArray<string> links, string? note = null, Guid? witness = null) =>
        s.Act(new SubmitProof(s.PlayerId(player), runId, links, note, witness));

    public static Scenario Approve(Scenario s, Guid runId, Difficulty? difficulty = null, string? comment = null) =>
        s.Act(new ApproveProof(runId, difficulty, comment));

    public static Scenario Reject(Scenario s, Guid runId, string comment = Comment) =>
        s.Act(new RejectProof(runId, comment));

    /// <summary>Moves the season forward to <paramref name="status"/> through every status in between.</summary>
    public static void MoveSeasonTo(Scenario s, SeasonStatus status)
    {
        for (var next = s.State.Status + 1; next <= status; next++)
        {
            s.Act(new ChangeSeasonStatus(next));
            ScenarioAssert.Accepted(s);
        }
    }

    /// <summary>Вася rolls, starts and drops a game; returns the dropped run.</summary>
    public static Guid DroppedRun(Scenario s, string player = "Вася")
    {
        s.Roll(player).Start(player);
        var runId = s.Player(player).ActiveRunId!.Value;
        s.Advance(TimeSpan.FromHours(2));
        s.Act(new DropRun(s.PlayerId(player)));
        ScenarioAssert.Accepted(s);
        return runId;
    }
}

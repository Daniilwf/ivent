using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Finish;

/// <summary>
/// Shared setup of the finish tests (C9a, D-99): a linear map of <see cref="MapLength"/> steps (start, c1, c2, c3,
/// finish), a pool of twelve 6-hour games in one category, so each completion rolls two dice of the difficulty's type
/// (normal d4, hard d6, extreme d6 with a good event) and pays 6 coins. From the start the dice 3 + 1 reach the finish,
/// 1 + 1 stop on c2. Test ruleset: <c>finish.requireApprovalForFirst</c> true, <c>bonusByOrder</c> [10, 8, 6, 4],
/// <c>bonusAfterList</c> 2; drop penalty 2d4 on points and position with a bad event.
/// </summary>
internal static class FinishSetup
{
    public const int MapLength = 4;

    public const int GameHours = 6;

    public const int CoinsPerRun = 6;

    public const string Comment = "На скрине другая игра";

    public static readonly string[] Names = ["Вася", "Петя", "Маша", "Коля", "Оля", "Дима", "Лёша"];

    private static readonly string[] s_titles =
    [
        "Silent Hill", "Alan Wake", "Dead Space", "Outlast", "Amnesia", "Soma",
        "Prey", "Doom", "Quake", "Portal", "Limbo", "Inside",
    ];

    /// <summary>A running season with the first <paramref name="players"/> of <see cref="Names"/> on the start.</summary>
    public static Scenario New(int players = 3, Func<Ruleset, Ruleset>? ruleset = null, int games = 12)
    {
        var s = Scenario.New();
        if (ruleset is not null)
        {
            s.WithRuleset(ruleset);
        }

        s.WithMapLength(MapLength).WithCategory("Horror");
        foreach (var title in s_titles.Take(games))
        {
            s.WithGame(title, GameHours, "Horror");
        }

        s.WithPlayers([.. Names.Take(players)]);
        return s;
    }

    /// <summary>
    /// The player rolls, starts and completes with the given dice; returns the run and the moment of the completion
    /// (the clock moves an hour after, so every command has its own time).
    /// </summary>
    public static (Guid RunId, DateTimeOffset At) Complete(Scenario s, string player, int[] dice, Difficulty difficulty = Difficulty.Normal)
    {
        s.Roll(player).Start(player);
        var runId = s.Player(player).ActiveRunId!.Value;
        var at = s.Clock.UtcNow;
        s.NextRandom(dice).Complete(player, difficulty);
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(1));
        return (runId, at);
    }

    /// <summary>From the start: 3 + 1 → 4 points, the finish, 6 coins.</summary>
    public static Guid FinishRun(Scenario s, string player) => Complete(s, player, [3, 1]).RunId;

    /// <summary>The player rolls and starts a game; returns the run being played.</summary>
    public static Guid Playing(Scenario s, string player)
    {
        s.Roll(player).Start(player);
        return s.Player(player).ActiveRunId!.Value;
    }

    public static Scenario Approve(Scenario s, Guid runId, string? comment = "Видел на стриме") =>
        s.Act(new ApproveProof(runId, null, comment));

    public static Scenario Reject(Scenario s, Guid runId, string comment = Comment) =>
        s.Act(new RejectProof(runId, comment));

    /// <summary>The first of the season is frozen: finished, the finishing run approved.</summary>
    public static Guid FrozenFirst(Scenario s, string player)
    {
        var runId = FinishRun(s, player);
        Approve(s, runId);
        ScenarioAssert.Accepted(s);
        Assert.True(s.Player(player).Finish?.Frozen, $"{player} is not frozen after the approval.");
        return runId;
    }

    /// <summary>Index of the single event of type <typeparamref name="T"/> matching <paramref name="match"/> in the last command.</summary>
    public static int IndexOf<T>(Scenario s, Func<T, bool>? match = null) where T : IGameEvent
    {
        var indexes = s.Last.Events
            .Select((e, i) => (e, i))
            .Where(x => x.e is T t && (match is null || match(t)))
            .Select(x => x.i)
            .ToList();
        return Assert.Single(indexes);
    }

    /// <summary>Finish bonus changes of the last command as (player, delta, reason), without the run they link to.</summary>
    public static List<(Guid Player, int Delta, PointsReason Reason)> BonusChanges(Scenario s) =>
        [.. s.LastEvents<PointsChanged>()
            .Where(e => e.Reason is PointsReason.FinishBonus or PointsReason.FinishBonusRevoked)
            .Select(e => (e.PlayerId, e.Delta, e.Reason))];

    public static FinishState? FinishOf(Scenario s, string player) => s.Player(player).Finish;
}

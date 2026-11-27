using GameEvent.Engine.Interactions;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Statistics;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;

namespace GameEvent.Engine.Tests.Statistics;

/// <summary>Completed runs in the season recap and the most-runs nomination (SPEC «Итоги сезона», D-716, D-793).</summary>
public sealed class CompletionStatisticsTests
{
    [Fact]
    public void An_empty_state_has_no_players_or_candidates()
    {
        var stats = CompletionStatistics.Build(SeasonState.Empty);

        Assert.Empty(stats.Players);
        Assert.Empty(stats.MostRunsCandidates);
    }

    [Fact]
    public void Players_with_no_completions_have_zero_counts_and_are_not_candidates()
    {
        var scenario = Season();
        var stats = CompletionStatistics.Build(scenario.State);

        AssertCounts(scenario, stats, vasya: 0, petya: 0, masha: 0, kolya: 0);
        Assert.Empty(stats.MostRunsCandidates);
    }

    [Fact]
    public void Only_completed_runs_count_toward_the_single_candidate()
    {
        var scenario = Season();
        var completed = scenario.CompleteTitle("Вася", SoloGame, 1, 1);
        var rejected = scenario.CompleteTitle("Вася", OtherSoloGame, 2, 2);
        scenario.Act(new RejectProof(rejected, Comment));
        ScenarioAssert.Accepted(scenario);
        Assert.Single(scenario.LastEvents<ProofRejected>());

        var dropped = scenario.PlayTitle("Петя", ThirdSoloGame);
        scenario.NextRandom(1, 1).Act(new DropRun(scenario.PlayerId("Петя")));
        ScenarioAssert.Accepted(scenario);

        var playing = scenario.PlayTitle("Коля", "Doom");
        var rerolled = scenario.PlayTitle("Маша", "Outlast");
        scenario.Act(new TechReroll(scenario.PlayerId("Маша"), TechRerollReason.DoesNotLaunch, null));
        ScenarioAssert.Accepted(scenario);

        Assert.Equal(RunStatus.Completed, scenario.State.Runs[completed].Status);
        Assert.Equal(RunStatus.Rejected, scenario.State.Runs[rejected].Status);
        Assert.Equal(RunStatus.Dropped, scenario.State.Runs[dropped].Status);
        Assert.Equal(RunStatus.TechRerolled, scenario.State.Runs[rerolled].Status);
        Assert.Equal(RunStatus.Playing, scenario.State.Runs[playing].Status);
        var stats = CompletionStatistics.Build(scenario.State);
        AssertCounts(scenario, stats, vasya: 1, petya: 0, masha: 0, kolya: 0);
        Assert.Equal(scenario.PlayerId("Вася"), Assert.Single(stats.MostRunsCandidates));
    }

    [Fact]
    public void Equal_positive_counts_give_both_candidates_even_with_different_points()
    {
        var scenario = Season();
        scenario.CompleteTitle("Маша", SoloGame, 1, 1);
        scenario.CompleteTitle("Петя", OtherSoloGame, 4, 4);
        Assert.NotEqual(scenario.Player("Маша").Points, scenario.Player("Петя").Points);

        var stats = CompletionStatistics.Build(scenario.State);
        AssertCounts(scenario, stats, vasya: 0, petya: 1, masha: 1, kolya: 0);
        Guid[] expected = [scenario.PlayerId("Петя"), scenario.PlayerId("Маша")];
        Assert.Equal(expected.Order(), stats.MostRunsCandidates);

        var reverse = Season();
        reverse.CompleteTitle("Петя", OtherSoloGame, 4, 4);
        reverse.CompleteTitle("Маша", SoloGame, 1, 1);
        Assert.Equal(stats.MostRunsCandidates, CompletionStatistics.Build(reverse.State).MostRunsCandidates);

        scenario.CompleteTitle("Маша", ThirdSoloGame, 1, 1);
        Assert.Equal(scenario.PlayerId("Маша"), Assert.Single(CompletionStatistics.Build(scenario.State).MostRunsCandidates));
    }

    [Fact]
    public void Disabling_the_nomination_preserves_completion_counts()
    {
        var scenario = Season();
        scenario.CompleteTitle("Вася", SoloGame, 1, 1);
        Assert.Equal(scenario.PlayerId("Вася"), Assert.Single(CompletionStatistics.Build(scenario.State).MostRunsCandidates));

        scenario.WithRuleset(rules => rules with { Nominations = rules.Nominations with { Auto = [AutoNomination.MostHours] } });
        ScenarioAssert.Accepted(scenario);

        var stats = CompletionStatistics.Build(scenario.State);
        AssertCounts(scenario, stats, vasya: 1, petya: 0, masha: 0, kolya: 0);
        Assert.Empty(stats.MostRunsCandidates);
    }

    [Fact]
    public void A_coop_completion_counts_for_both_and_undo_restores_the_rejected_counts()
    {
        var scenario = Season();
        var run = scenario.CoopRun();
        AssertCounts(scenario, CompletionStatistics.Build(scenario.State), vasya: 0, petya: 0, masha: 0, kolya: 0);

        scenario.CompleteWith("Вася", 3, 4);
        Assert.Single(scenario.LastEvents<CoopRewardSplit>());
        var completion = CompletionStatistics.Build(scenario.State);
        AssertCounts(scenario, completion, vasya: 1, petya: 1, masha: 0, kolya: 0);
        Guid[] expected = [scenario.PlayerId("Вася"), scenario.PlayerId("Петя")];
        Assert.Equal(expected.Order(), completion.MostRunsCandidates);

        scenario.Act(new RejectProof(run, Comment));
        ScenarioAssert.Accepted(scenario);
        var rejection = scenario.LastCommandId;
        var rejected = CompletionStatistics.Build(scenario.State);
        AssertCounts(scenario, rejected, vasya: 0, petya: 0, masha: 0, kolya: 0);
        Assert.Empty(rejected.MostRunsCandidates);

        scenario.Act(new UndoCommand(rejection, "Ошибка проверки пруфа"));
        ScenarioAssert.Accepted(scenario);
        Assert.Single(scenario.LastEvents<CommandUndone>());
        Assert.Equal(RunStatus.Completed, scenario.State.Runs[run].Status);
        var restored = CompletionStatistics.Build(scenario.State);
        AssertCounts(scenario, restored, vasya: 1, petya: 1, masha: 0, kolya: 0);
        Assert.Equal(completion.MostRunsCandidates, restored.MostRunsCandidates);

        var replayed = CompletionStatistics.Build(SeasonEngine.Replay(scenario.StoredLog));
        Assert.Equal(restored.Players, replayed.Players);
        Assert.Equal(restored.MostRunsCandidates, replayed.MostRunsCandidates);
    }

    [Fact]
    public void A_partner_who_left_before_the_split_does_not_get_a_completion()
    {
        var scenario = Season();
        scenario.CoopRun();
        scenario.Act(new LeaveCoop(scenario.PlayerId("Петя")));
        ScenarioAssert.Accepted(scenario);
        Assert.Single(scenario.LastEvents<CoopLeft>());

        scenario.CompleteWith("Вася", 3, 4);
        Assert.Empty(scenario.LastEvents<CoopRewardSplit>());
        var stats = CompletionStatistics.Build(scenario.State);
        AssertCounts(scenario, stats, vasya: 1, petya: 0, masha: 0, kolya: 0);
        Assert.Equal(scenario.PlayerId("Вася"), Assert.Single(stats.MostRunsCandidates));
    }

    [Fact]
    public void Completions_in_free_mode_count_in_personal_statistics()
    {
        var scenario = Season(mapLength: 4);
        scenario.CompleteTitle("Вася", SoloGame, 4, 4);
        Assert.Equal(1, scenario.Player("Вася").Finish?.Order);

        var playedAfterFinish = scenario.CompleteTitle("Вася", OtherSoloGame, 1, 1);
        Assert.True(scenario.State.Runs[playedAfterFinish].FreeMode);
        var stats = CompletionStatistics.Build(scenario.State);
        AssertCounts(scenario, stats, vasya: 2, petya: 0, masha: 0, kolya: 0);
        Assert.Equal(scenario.PlayerId("Вася"), Assert.Single(stats.MostRunsCandidates));
    }

    private static Scenario Season(int mapLength = 40) =>
        New(rules => rules with { Nominations = rules.Nominations with { Auto = [AutoNomination.MostRuns] } }, mapLength: mapLength);

    private static void AssertCounts(Scenario scenario, SeasonCompletionStatistics stats, int vasya, int petya, int masha, int kolya)
    {
        (Guid Id, int Completed)[] expected =
        [
            (scenario.PlayerId("Вася"), vasya),
            (scenario.PlayerId("Петя"), petya),
            (scenario.PlayerId("Маша"), masha),
            (scenario.PlayerId("Коля"), kolya),
        ];
        Assert.Equal(expected.OrderBy(player => player.Id), stats.Players
            .Select(player => (player.PlayerId, player.CompletedRuns)));
    }
}

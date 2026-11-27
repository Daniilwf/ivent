using GameEvent.Engine.Interactions;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Statistics;
using GameEvent.Engine.Tests.Finish;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;

namespace GameEvent.Engine.Tests.Statistics;

/// <summary>The season-wide drop counts and the MostDrops nomination (SPEC «Итоги сезона», D-101, D-324, D-522).</summary>
public sealed class DropStatisticsTests
{
    [Fact]
    public void Uncreated_and_drop_free_seasons_have_no_candidates()
    {
        var empty = Read(SeasonState.Empty);
        Assert.Empty(empty.Players);
        Assert.Empty(empty.MostDropsCandidates);

        var season = Season();
        var dropFree = Read(season.State);
        AssertCounts(season, dropFree, ("Вася", 0), ("Петя", 0), ("Маша", 0), ("Коля", 0));
        Assert.Empty(dropFree.MostDropsCandidates);
    }

    [Fact]
    public void A_completion_does_not_reset_season_drops_and_proof_rejections_are_not_drops()
    {
        var season = Season();
        var firstDrop = season.PlayTitle("Вася", SoloGame);
        Drop(season, "Вася", firstDrop);
        var completed = season.CompleteTitle("Вася", OtherSoloGame, 1, 1);
        var secondDrop = season.PlayTitle("Вася", ThirdSoloGame);
        Drop(season, "Вася", secondDrop);

        var rejected = season.CompleteTitle("Маша", "Doom", 1, 1);
        season.Act(new RejectProof(rejected, Comment));
        ScenarioAssert.Accepted(season);
        Assert.Single(season.LastEvents<ProofRejected>());
        var penalized = season.CompleteTitle("Маша", "Outlast", 2, 2);
        season.NextRandom(1, 2).Act(new RejectProofWithDropPenalty(penalized, Comment));
        ScenarioAssert.Accepted(season);
        Assert.Single(season.LastEvents<ProofRejectPenalized>());

        var playing = season.PlayTitle("Коля", "Prey");
        Assert.Equal(RunStatus.Completed, season.State.Runs[completed].Status);
        Assert.Equal(RunStatus.Rejected, season.State.Runs[rejected].Status);
        Assert.Equal(RunStatus.Rejected, season.State.Runs[penalized].Status);
        Assert.Equal(RunStatus.Playing, season.State.Runs[playing].Status);

        var stats = Read(season.State);
        AssertCounts(season, stats, ("Вася", 2), ("Петя", 0), ("Маша", 0), ("Коля", 0));
        Assert.Equal(season.PlayerId("Вася"), Assert.Single(stats.MostDropsCandidates));
    }

    [Fact]
    public void A_wish_reroll_and_an_unconverted_tech_reroll_do_not_count()
    {
        var season = Scenario.New()
            .WithRuleset(rules => EnableNomination(rules) with { Roll = rules.Roll with { WishRerollTags = ["RPG"] } })
            .WithCategory("Any")
            .WithGame("Persona", 6, "Any", "RPG")
            .WithGame("Dragon Quest", 6, "Any", "RPG")
            .WithGame("Outlast", 6, "Any", "Horror")
            .WithGame("Doom", 6, "Any", "Action")
            .WithPlayers("Вася", "Маша");
        var technical = season.PlayTitle("Маша", "Outlast");
        Reroll(season, "Маша", technical, TechRerollReason.DoesNotLaunch);
        var wished = season.PlayTitle("Вася", "Persona");
        Reroll(season, "Вася", wished, TechRerollReason.Wish);

        var stats = Read(season.State);
        AssertCounts(season, stats, ("Вася", 0), ("Маша", 0));
        Assert.Empty(stats.MostDropsCandidates);
    }

    [Fact]
    public void An_admin_conversion_during_closing_counts_the_original_run_once()
    {
        var season = Season();
        var run = season.PlayTitle("Петя", SoloGame);
        Reroll(season, "Петя", run, TechRerollReason.DoesNotLaunch);
        var beforeConversion = season.State;
        season.MoveStatusTo(SeasonStatus.Closing);
        Convert(season, run);

        var after = Read(season.State);
        AssertCounts(season, after, ("Вася", 0), ("Петя", 1), ("Маша", 0), ("Коля", 0));
        Assert.Equal(season.PlayerId("Петя"), Assert.Single(after.MostDropsCandidates));
        AssertCounts(season, Read(beforeConversion), ("Вася", 0), ("Петя", 0), ("Маша", 0), ("Коля", 0));
        AssertReplay(season, after);
    }

    [Fact]
    public void Equal_positive_counts_give_all_top_candidates_by_player_id_and_can_be_disabled()
    {
        var season = Season();
        season.CompleteTitle("Петя", SoloGame, 4, 4);
        Drop(season, "Коля", season.PlayTitle("Коля", OtherSoloGame));
        Drop(season, "Петя", season.PlayTitle("Петя", ThirdSoloGame));
        Assert.NotEqual(season.Player("Петя").Points, season.Player("Коля").Points);
        var tiedState = season.State;

        Drop(season, "Петя", season.PlayTitle("Петя", "Outlast"));
        var aheadState = season.State;
        season.WithRuleset(rules => rules with { Nominations = rules.Nominations with { Auto = [AutoNomination.MostRuns] } });
        ScenarioAssert.Accepted(season);

        var tied = Read(tiedState);
        AssertCounts(season, tied, ("Вася", 0), ("Петя", 1), ("Маша", 0), ("Коля", 1));
        Guid[] expected = [season.PlayerId("Петя"), season.PlayerId("Коля")];
        Assert.Equal(expected.Order(), tied.MostDropsCandidates);

        var ahead = Read(aheadState);
        Assert.Equal(season.PlayerId("Петя"), Assert.Single(ahead.MostDropsCandidates));
        var disabled = Read(season.State);
        Assert.Equal(ahead.Players, disabled.Players);
        Assert.Empty(disabled.MostDropsCandidates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_coop_drop_counts_for_the_roller_only_even_if_the_partner_left(bool partnerLeaves)
    {
        var season = Season();
        var run = season.CoopRun();
        if (partnerLeaves)
        {
            season.Act(new LeaveCoop(season.PlayerId("Петя")));
            ScenarioAssert.Accepted(season);
            Assert.Single(season.LastEvents<CoopLeft>());
        }

        var beforeDrop = season.State;
        Drop(season, "Вася", run);

        var stats = Read(season.State);
        AssertCounts(season, stats, ("Вася", 1), ("Петя", 0), ("Маша", 0), ("Коля", 0));
        Assert.Equal(season.PlayerId("Вася"), Assert.Single(stats.MostDropsCandidates));
        AssertCounts(season, Read(beforeDrop), ("Вася", 0), ("Петя", 0), ("Маша", 0), ("Коля", 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_frozen_firsts_free_mode_drop_still_counts_without_a_penalty(bool converted)
    {
        var season = FinishSetup.New(ruleset: EnableNomination);
        FinishSetup.FrozenFirst(season, "Вася");
        var run = FinishSetup.Playing(season, "Вася");
        Assert.True(season.Player("Вася").Finish?.Frozen);
        var points = season.Player("Вася").Points;
        DropOrConvert(season, "Вася", run, converted);
        Assert.Equal(points, season.Player("Вася").Points);

        var stats = Read(season.State);
        AssertCounts(season, stats, ("Вася", 1), ("Петя", 0), ("Маша", 0));
        Assert.Equal(season.PlayerId("Вася"), Assert.Single(stats.MostDropsCandidates));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Undo_removes_the_drop_from_statistics_and_replay_agrees(bool converted)
    {
        var season = Season();
        var run = season.PlayTitle("Вася", SoloGame);
        DropOrConvert(season, "Вася", run, converted);
        var afterDrop = season.State;
        var dropCommand = season.LastCommandId;
        season.Act(new UndoCommand(dropCommand, "Ошибка регистрации дропа"));
        ScenarioAssert.Accepted(season);
        Assert.Single(season.LastEvents<CommandUndone>());
        Assert.Equal(converted ? RunStatus.TechRerolled : RunStatus.Playing, season.State.Runs[run].Status);

        AssertCounts(season, Read(afterDrop), ("Вася", 1), ("Петя", 0), ("Маша", 0), ("Коля", 0));
        var restored = Read(season.State);
        AssertCounts(season, restored, ("Вася", 0), ("Петя", 0), ("Маша", 0), ("Коля", 0));
        Assert.Empty(restored.MostDropsCandidates);
        AssertReplay(season, restored);
    }

    private static Ruleset EnableNomination(Ruleset rules) =>
        rules with { Nominations = rules.Nominations with { Auto = [AutoNomination.MostDrops] } };

    private static Scenario Season() => New(EnableNomination);

    private static void Drop(Scenario season, string player, Guid run)
    {
        season.NextRandom(1, 1).Act(new DropRun(season.PlayerId(player)));
        ScenarioAssert.Accepted(season);
        Assert.Equal(run, Assert.Single(season.LastEvents<RunDropped>()).RunId);
        Assert.Equal(RunStatus.Dropped, season.State.Runs[run].Status);
    }

    private static void Reroll(Scenario season, string player, Guid run, TechRerollReason reason)
    {
        season.Act(new TechReroll(season.PlayerId(player), reason, null));
        ScenarioAssert.Accepted(season);
        var rerolled = Assert.Single(season.LastEvents<RunTechRerolled>());
        Assert.Equal((run, reason), (rerolled.RunId, rerolled.Reason));
        Assert.Equal(RunStatus.TechRerolled, season.State.Runs[run].Status);
    }

    private static void Convert(Scenario season, Guid run)
    {
        season.NextRandom(1, 1).Act(new ConvertTechRerollToDrop(run, "Это был дроп"));
        ScenarioAssert.Accepted(season);
        Assert.Equal(run, Assert.Single(season.LastEvents<TechRerollConvertedToDrop>()).RunId);
        Assert.Equal(RunStatus.Dropped, season.State.Runs[run].Status);
    }

    private static void DropOrConvert(Scenario season, string player, Guid run, bool converted)
    {
        if (converted)
        {
            Reroll(season, player, run, TechRerollReason.DoesNotLaunch);
            Convert(season, run);
        }
        else
        {
            Drop(season, player, run);
        }
    }

    private static void AssertCounts(Scenario season, ObservedStatistics stats, params (string Player, int Count)[] expected) =>
        Assert.Equal(expected.Select(row => (season.PlayerId(row.Player), row.Count)).OrderBy(row => row.Item1), stats.Players);

    private static void AssertReplay(Scenario season, ObservedStatistics expected)
    {
        var replayed = Read(SeasonEngine.Replay(season.StoredLog));
        Assert.Equal(expected.Players, replayed.Players);
        Assert.Equal(expected.MostDropsCandidates, replayed.MostDropsCandidates);
    }

    private static ObservedStatistics Read(SeasonState state)
    {
        var result = DropStatistics.Build(state);
        return new(result.Players.Select(row => (row.PlayerId, row.DroppedRuns)).ToArray(), result.MostDropsCandidates.ToArray());
    }

    private sealed record ObservedStatistics((Guid PlayerId, int DroppedRuns)[] Players, Guid[] MostDropsCandidates);
}

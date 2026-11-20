using System.Text.Json;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;

namespace GameEvent.Engine.Tests.Statistics;

public sealed class SeasonNominationTests
{
    private const string ResultEventType = "season-nominations-recorded";

    [Fact]
    public void Finish_records_all_configured_awards_using_credited_runs_and_effective_attacks()
    {
        var season = New(rules => Configure(rules.WithEconomy(), Enum.GetValues<AutoNomination>()) with
        {
            Drop = rules.Drop with { MandatoryEvent = MandatoryEvent.None },
        }).WithContent(EconomyScenario.ExamplePack("bird-thief"));

        var coop = season.CoopRun();
        season.CompleteWith("Вася", 1, 1);
        season.NextRandom(1).Act(new CorrectRunHours(coop, 9m, "Уточнили часы игры"));
        ScenarioAssert.Accepted(season);
        Approve(season, coop);
        Approve(season, season.CompleteTitle("Маша", SoloGame, 2, 2));

        var rejected = season.CompleteTitle("Коля", "Doom", 1, 1);
        season.Act(new RejectProof(rejected, Comment));
        ScenarioAssert.Accepted(season);

        Drop(season, "Вася", OtherSoloGame);
        Drop(season, "Петя", ThirdSoloGame);
        season.PlayTitle("Коля", "Outlast");
        season.Act(new TechReroll(season.PlayerId("Коля"), TechRerollReason.DoesNotLaunch, null));
        ScenarioAssert.Accepted(season);

        season.WithCoins("Вася", 10).WithCoins("Коля", 10).WithCoins("Маша", 10);
        Steal(season, "Маша", "Вася");
        Steal(season, "Петя", "Коля");
        Steal(season, "Маша", "Коля");
        season.Act(new UndoCommand(season.LastCommandId, "Отмена ошибочной атаки"));
        ScenarioAssert.Accepted(season);
        Assert.Equal(2, season.EffectiveLog.OfType<HostileReceived>().Count());

        season.MoveStatusTo(SeasonStatus.Closing);
        Assert.DoesNotContain(season.Log.Select(EventCodec.Encode), row => row.Type == ResultEventType);

        var awards = Finish(season);

        Assert.Equal(5, awards.Length);
        AssertAward(season, awards, "mostHours", 9m, "Вася", "Петя");
        AssertAward(season, awards, "mostRuns", 1m, "Вася", "Петя", "Маша");
        AssertAward(season, awards, "unluckiestDice", 0m, "Вася");
        AssertAward(season, awards, "mostDrops", 1m, "Вася", "Петя");
        AssertAward(season, awards, "mostAttacks", 1m, "Петя", "Маша");

        var snapshot = Assert.Single(season.Last.Events.Select(EventCodec.Encode), row => row.Type == ResultEventType);
        Assert.Equal(snapshot, EventCodec.Encode(EventCodec.Decode(snapshot)));
        season.MoveStatusTo(SeasonStatus.Archived);
        Assert.Single(season.Log.Select(EventCodec.Encode), row => row.Type == ResultEventType);
        Assert.Equal(season.State, SeasonEngine.Replay(season.StoredLog));
    }

    [Fact]
    public void Unluckiest_compares_player_means_and_uses_faces_saved_after_a_reroll()
    {
        var season = New(rules => Configure(rules.WithEconomy(), AutoNomination.UnluckiestDice))
            .WithContent(EconomyScenario.ExamplePack("reroll-dice"));

        Approve(season, season.CompleteTitle("Вася", SoloGame, 1, 1));
        var secondRoll = season.CompleteTitle("Вася", OtherSoloGame, 1, 1);
        season.Give("Вася", "reroll-dice");
        season.NextRandom(4, 4).Used("Вася", "reroll-dice");
        Assert.Single(season.LastEvents<RunDiceRerolled>());
        Approve(season, secondRoll);

        Approve(season, season.CompleteTitle("Маша", ThirdSoloGame, 2, 2));
        var longerRoll = season.CompleteTitle("Петя", "Doom", 2, 2);
        season.NextRandom(2).Act(new CorrectRunHours(longerRoll, 9m, "Уточнили часы игры"));
        ScenarioAssert.Accepted(season);
        Approve(season, longerRoll);

        var awards = Finish(season);

        // The two rolls by Vasya average to 1/2. The other players reach 1/3 using different dice counts.
        Assert.Single(awards);
        AssertAward(season, awards, "unluckiestDice", 1m / 3m, "Маша", "Петя");
    }

    [Fact]
    public void Unluckiest_keeps_exactly_equal_means_across_different_numbers_of_rolls()
    {
        var season = New(rules => Configure(rules, AutoNomination.UnluckiestDice));
        Approve(season, season.CompleteTitle("Вася", SoloGame, 1, 1));
        Approve(season, season.CompleteTitle("Вася", OtherSoloGame, 3, 3));
        Approve(season, season.CompleteTitle("Маша", ThirdSoloGame, 2, 2));

        // The exact mean of 0 and 2/3 ties a single 1/3. Averaging pre-rounded decimals loses the tie.
        AssertAward(season, Finish(season), "unluckiestDice", 1m / 3m, "Вася", "Маша");
    }

    [Fact]
    public void Zero_counters_and_no_rolls_do_not_make_winners()
    {
        var season = New(rules => Configure(rules, Enum.GetValues<AutoNomination>()));

        var awards = Finish(season);

        Assert.All(awards, award => Assert.Empty(award.GetProperty("winnerPlayerIds").EnumerateArray()));
    }

    [Fact]
    public void A_season_with_nominations_off_records_the_result_without_awards()
    {
        var season = New(rules => rules with
        {
            Features = rules.Features with { SeasonNominations = false },
            Nominations = rules.Nominations with { Auto = [AutoNomination.MostRuns] },
        });
        Approve(season, season.CompleteTitle("Вася", SoloGame, 1, 1));

        season.MoveStatusTo(SeasonStatus.Finished);

        Assert.Single(season.LastEvents<SeasonResultRecorded>());
        Assert.DoesNotContain(season.Last.Events.Select(EventCodec.Encode), row => row.Type == ResultEventType);
    }

    private static Ruleset Configure(Ruleset rules, params AutoNomination[] automatic) => rules with
    {
        Features = rules.Features with { SeasonNominations = true },
        Nominations = rules.Nominations with { Auto = [.. automatic], Voted = [] },
    };

    private static void Approve(Scenario season, Guid runId)
    {
        season.Act(new ApproveProof(runId, null, Comment));
        ScenarioAssert.Accepted(season);
    }

    private static void Drop(Scenario season, string player, string title)
    {
        var run = season.PlayTitle(player, title);
        season.NextRandom(1, 1).Act(new DropRun(season.PlayerId(player)));
        ScenarioAssert.Accepted(season);
        Assert.Equal(run, Assert.Single(season.LastEvents<RunDropped>()).RunId);
    }

    private static void Steal(Scenario season, string attacker, string target)
    {
        season.Give(attacker, "bird-thief");
        season.NextRandom(1).Used(attacker, "bird-thief", target);
        var hit = Assert.Single(season.LastEvents<HostileReceived>());
        Assert.Equal((season.PlayerId(attacker), season.PlayerId(target)), (hit.FromPlayerId, hit.PlayerId));
    }

    private static JsonElement[] Finish(Scenario season)
    {
        season.MoveStatusTo(SeasonStatus.Finished);
        Assert.Single(season.LastEvents<SeasonResultRecorded>());
        var snapshot = Assert.Single(season.Last.Events.Select(EventCodec.Encode), row => row.Type == ResultEventType);
        using var document = JsonDocument.Parse(snapshot.Data);
        return [.. document.RootElement.GetProperty("automatic").EnumerateArray().Select(row => row.Clone())];
    }

    private static void AssertAward(
        Scenario season, JsonElement[] awards, string nomination, decimal value, params string[] winners)
    {
        var award = Assert.Single(awards, row => row.GetProperty("nomination").GetString() == nomination);
        Assert.Equal(value, award.GetProperty("value").GetDecimal());
        Assert.Equal(winners.Select(season.PlayerId).Order(),
            award.GetProperty("winnerPlayerIds").EnumerateArray().Select(id => id.GetGuid()).Order());
    }
}

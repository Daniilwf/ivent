using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Seasons;

/// <summary>Creating a season and adding players; commands before the season exists are refused.</summary>
public class SeasonSetupTests
{
    [Fact]
    public void Creating_a_season_twice_is_rejected()
    {
        var s = Scenario.New().WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new CreateSeason(SequentialIds.Make(0x30000000, 2), "Тестовый сезон", TestRuleset.Create())), RejectionCodes.SeasonAlreadyCreated);
    }

    [Fact]
    public void Adding_the_same_player_twice_is_rejected()
    {
        var s = Scenario.New().WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new AddSeasonPlayer(s.PlayerId("Вася"), SequentialIds.Make(0x40000000, 99), "Вася")), RejectionCodes.PlayerAlreadyAdded);
    }

    [Fact]
    public void Adding_the_same_user_twice_under_another_player_id_is_rejected()
    {
        var s = Scenario.New().WithPlayers("Вася");
        var vasyaUser = s.Player("Вася").UserId;

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new AddSeasonPlayer(SequentialIds.Make(0x10000000, 99), vasyaUser, "Вася-2")), RejectionCodes.PlayerAlreadyAdded);
    }

    [Fact]
    public void New_player_starts_on_start_with_zero_points_and_idle()
    {
        var s = Scenario.New().WithPlayers("Вася");

        var player = s.Player("Вася");
        Assert.Equal(LinearMap.StartId, player.CellId);
        Assert.Equal(0, player.Points);
        Assert.Equal(TurnPhase.Idle, player.Phase);
        Assert.Null(player.Offer);
        Assert.Null(player.ActiveRunId);
    }

    public static TheoryData<string, ICommand> CommandsBeforeSeason() => new()
    {
        { "add player", new AddSeasonPlayer(SequentialIds.Make(0x10000000, 1), SequentialIds.Make(0x40000000, 1), "Вася") },
        { "roll", new RollGame(SequentialIds.Make(0x10000000, 1)) },
        { "start", new StartRun(SequentialIds.Make(0x10000000, 1)) },
        { "complete", new CompleteRun(SequentialIds.Make(0x10000000, 1), Difficulty.Normal, 3) },

        // C13: every other command of stage 1 refuses the same way before the season exists
        { "review", new ReviewRun(SequentialIds.Make(0x10000000, 1), SequentialIds.Make(0x60000000, 1), new RunReview(8, null)) },
        { "correct hours", new CorrectRunHours(SequentialIds.Make(0x60000000, 1), 5, "Часы по пруфу") },
        { "change difficulty", new ChangeRunDifficulty(SequentialIds.Make(0x60000000, 1), Difficulty.Easy, "Сложность по пруфу") },
        { "drop", new DropRun(SequentialIds.Make(0x10000000, 1)) },
        { "tech reroll", new TechReroll(SequentialIds.Make(0x10000000, 1), TechRerollReason.DoesNotLaunch, null) },
        { "convert to a drop", new ConvertTechRerollToDrop(SequentialIds.Make(0x60000000, 1), "Это был дроп") },
        { "adjust", new AdjustPlayer(SequentialIds.Make(0x10000000, 1), "Правка", PointsDelta: 1) },
        { "inactive", new SetPlayerInactive(SequentialIds.Make(0x10000000, 1), true) },
        { "resolve an effect", new Engine.Effects.ResolveManualEffect(SequentialIds.Make(0x70000000, 1), Engine.Effects.ManualEffectOutcome.Applied, "Разыграли", null) },
        { "change the rules", new ChangeRuleset(TestRuleset.Create()) },
        { "undo", new Engine.Undo.UndoCommand(SequentialIds.Make(0x7C000000, 1), "Ошибка") },
        { "deadline", new SetSeasonDeadline(FixedClock.SeasonStart.AddDays(3)) },
        { "reach the deadline", new ReachDeadline() },
        { "status", new ChangeSeasonStatus(SeasonStatus.Active) },
        { "reroll", new Reroll(SequentialIds.Make(0x10000000, 1)) },
        { "already played", new DeclareAlreadyPlayed(SequentialIds.Make(0x10000000, 1), SequentialIds.Make(0x20000000, 1)) },
        { "choose", new Engine.Turns.MakeChoice(SequentialIds.Make(0x10000000, 1), SequentialIds.Make(0x50000000, 1), "a") },
        { "proof", new Engine.Proofs.SubmitProof(SequentialIds.Make(0x10000000, 1), SequentialIds.Make(0x60000000, 1), ["https://imgur.com/a/1"]) },
        { "approve", new Engine.Proofs.ApproveProof(SequentialIds.Make(0x60000000, 1), null, "Видел") },
        { "reject", new Engine.Proofs.RejectProof(SequentialIds.Make(0x60000000, 1), "Другая игра") },
        { "reject with the drop penalty", new Engine.Proofs.RejectProofWithDropPenalty(SequentialIds.Make(0x60000000, 1), "Обман") },
    };

    [Theory]
    [MemberData(nameof(CommandsBeforeSeason))]
    public void Commands_before_the_season_exists_are_rejected(string what, ICommand command)
    {
        _ = what;
        var s = Scenario.New().WithCategory("Horror").WithGame("Silent Hill", 12, "Horror");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(command), RejectionCodes.SeasonNotCreated);
    }

    public static TheoryData<string, Func<Ruleset, Ruleset>> UnsupportedRulesets() => new()
    {
        { "two active runs", r => r with { Season = r.Season with { MaxActiveRunsPerPlayer = 2 } } },
    };

    [Theory]
    [MemberData(nameof(UnsupportedRulesets))]
    public void Season_with_a_mechanic_not_implemented_yet_is_rejected(string what, Func<Ruleset, Ruleset> change)
    {
        _ = what;
        var s = Scenario.New().WithRuleset(change);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new CreateSeason(SequentialIds.Make(0x30000000, 1), "Тестовый сезон", x.Ruleset)), RejectionCodes.RulesetInvalid);
    }

    [Fact]
    public void Ruleset_cannot_be_changed_mid_season_to_a_mechanic_not_implemented_yet()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
            .WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new ChangeRuleset(x.Ruleset with { Season = x.Ruleset.Season with { MaxActiveRunsPerPlayer = 2 } })),
            RejectionCodes.RulesetInvalid);
    }
}

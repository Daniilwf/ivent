using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Ranking;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;
using static GameEvent.Engine.Tests.Lifecycle.LifecycleSetup;

namespace GameEvent.Engine.Tests.Lifecycle;

/// <summary>
/// The deadline by the engine's clock (SE1, SE3, invariant 11; SPEC «Дедлайн»: «Засчитывается бросок, сделанный до
/// дедлайна»; K-7; D-101). Once <c>IClock</c> ≥ the deadline, every player turn command — roll, «уже проходил», reroll,
/// choice, start, completion, drop, tech reroll, the admin's tech reroll on the player's behalf included — is refused
/// with <c>season.deadlinePassed</c>, even while the season is still Active (the scheduler may be late). One second
/// before the deadline they are accepted. The admin's corrections, proofs, reviews stay allowed. A run left playing at
/// the deadline is not counted: it stays Playing without dice.
/// </summary>
public class DeadlineTests
{
    public static TheoryData<string> TurnCommands =>
    [
        "roll", "alreadyPlayed", "reroll", "choose", "start", "complete", "drop", "techReroll", "techRerollByAdmin",
    ];

    /// <summary>A season where a player is in the phase the command needs; returns the command.</summary>
    private static (Scenario S, ICommand Command) Prepare(string kind)
    {
        if (kind == "choose")
        {
            // A choice of two games: Маша's roll leaves a pending choice
            var withChoice = New(r => r with { Roll = r.Roll with { ChoiceCount = 2 } });
            withChoice.Roll("Маша");
            var choice = withChoice.Player("Маша").Choice!;
            return (withChoice, new MakeChoice(withChoice.PlayerId("Маша"), choice.ChoiceId, choice.Options[0].Id));
        }

        var s = New();
        var vasya = s.PlayerId("Вася");
        switch (kind)
        {
            case "roll":
                return (s, new RollGame(vasya));
            case "alreadyPlayed":
            case "reroll":
            case "start":
                s.Roll("Вася");
                var offered = s.Player("Вася").Offer!.GameId;
                return (s, kind switch
                {
                    "alreadyPlayed" => new DeclareAlreadyPlayed(vasya, offered),
                    "reroll" => new Reroll(vasya),
                    _ => new StartRun(vasya),
                });
            default:
                s.Roll("Вася").Start("Вася");
                return (s, kind switch
                {
                    "complete" => new CompleteRun(vasya, Difficulty.Normal),
                    "drop" => new DropRun(vasya),
                    "techReroll" => new TechReroll(vasya, TechRerollReason.DoesNotLaunch, null),
                    _ => new TechReroll(vasya, TechRerollReason.DoesNotLaunch, "попросил в чате", ByAdmin: true),
                });
        }
    }

    [Theory]
    [MemberData(nameof(TurnCommands))]
    public void Turn_command_at_the_deadline_is_refused_while_the_season_is_still_active(string kind)
    {
        var (s, command) = Prepare(kind);
        ToDeadline(s);
        Assert.Equal(SeasonStatus.Active, s.State.Status);

        Refused(s, command, RejectionCodes.SeasonDeadlinePassed);
    }

    [Theory]
    [MemberData(nameof(TurnCommands))]
    public void Turn_command_after_the_deadline_is_refused(string kind)
    {
        var (s, command) = Prepare(kind);
        ToDeadline(s);
        s.Advance(TimeSpan.FromHours(5));

        Refused(s, command, RejectionCodes.SeasonDeadlinePassed);
    }

    [Theory]
    [MemberData(nameof(TurnCommands))]
    public void Turn_command_just_before_the_deadline_is_accepted(string kind)
    {
        var (s, command) = Prepare(kind);
        JustBeforeDeadline(s);

        s.Act(command);

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Season_without_a_deadline_never_refuses_by_the_clock()
    {
        var s = New();
        s.Act(new SetSeasonDeadline(null));
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromDays(400));

        s.Roll("Вася");

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Is_past_deadline_follows_the_clock()
    {
        var s = New();

        Assert.False(SeasonSetup.IsPastDeadline(s.State, Deadline(s) - TimeSpan.FromTicks(1)));
        Assert.True(SeasonSetup.IsPastDeadline(s.State, Deadline(s)));
        Assert.True(SeasonSetup.IsPastDeadline(s.State, Deadline(s) + TimeSpan.FromDays(1)));

        var deadline = Deadline(s);
        s.Act(new SetSeasonDeadline(null));
        ScenarioAssert.Accepted(s);
        Assert.False(SeasonSetup.IsPastDeadline(s.State, deadline + TimeSpan.FromDays(100)));
    }

    [Fact]
    public void Moving_the_deadline_later_while_active_opens_the_turns_again()
    {
        // The deadline changes in Draft and Active only; while Active, even after it has passed
        var s = New();
        ToDeadline(s);
        s.ExpectRejection().Roll("Вася");
        Assert.False(s.Last.IsAccepted, "A roll at the deadline was accepted.");
        Assert.Equal(RejectionCodes.SeasonDeadlinePassed, s.Last.Rejection!.Code);

        s.Act(new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromDays(2)));
        ScenarioAssert.Accepted(s);
        s.Roll("Вася");

        ScenarioAssert.Accepted(s);
    }

    // ---- The admin after the deadline, before closing (K-7) ----

    [Fact]
    public void Admin_corrections_proofs_and_reviews_are_allowed_after_the_deadline()
    {
        var s = New();
        var runId = CompleteRun(s, "Вася", [2, 2]);
        ToDeadline(s);
        var vasya = s.PlayerId("Вася");

        s.Act(new SubmitProof(vasya, runId, ["https://imgur.com/a/credits"]));
        ScenarioAssert.Accepted(s);
        s.NextRandom(3, 3).Act(new CorrectRunHours(runId, 12, "Часы по HLTB"));
        ScenarioAssert.Accepted(s);
        s.Act(new ChangeRunDifficulty(runId, Difficulty.Hard, "По пруфу — сложная"));
        ScenarioAssert.Accepted(s);
        s.Review("Вася", runId, 8, "Хорошая игра");
        ScenarioAssert.Accepted(s);
        s.Act(new AdjustPlayer(vasya, "Бонус за стрим", PointsDelta: 1));
        ScenarioAssert.Accepted(s);
        Approve(s, runId);
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Admin_correction_after_the_deadline_rolls_the_missing_dice()
    {
        // K-7: a correction of a roll made before the deadline may add dice after it — it is not a player's roll
        var s = New();
        var runId = CompleteRun(s, "Вася", [2, 2]);
        ToDeadline(s);

        s.NextRandom(3, 3).Act(new CorrectRunHours(runId, 12, "Часы по HLTB"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([2, 2, 3, 3], s.State.Runs[runId].Dice.Select(d => d.Value));
    }

    // ---- A run left playing at the deadline ----

    [Fact]
    public void Run_left_playing_at_the_deadline_is_not_counted()
    {
        // Маша started before the deadline and tries to complete after it: refused; the run stays Playing, no dice
        var s = New();
        s.Roll("Маша").Start("Маша");
        var runId = s.Player("Маша").ActiveRunId!.Value;
        ToDeadline(s);

        Refused(s, new CompleteRun(s.PlayerId("Маша"), Difficulty.Normal), RejectionCodes.SeasonDeadlinePassed);

        var run = s.State.Runs[runId];
        Assert.Equal(RunStatus.Playing, run.Status);
        Assert.Empty(run.Dice);
        Assert.Equal(0, s.Player("Маша").Points);
        Assert.Equal(0, Leaderboard.Entries(s.State).Single(e => e.PlayerId == s.PlayerId("Маша")).CompletedRuns);
        Assert.DoesNotContain(runId, ProofReviewOrder.Order(s.State));
    }

    [Fact]
    public void Run_left_playing_stays_out_of_the_result()
    {
        var s = New();
        CompleteRun(s, "Вася", [2, 2]);
        s.Roll("Маша").Start("Маша");
        var mashaRun = s.Player("Маша").ActiveRunId!.Value;
        var vasyaRun = s.State.Runs.Values.Single(r => r.PlayerId == s.PlayerId("Вася")).RunId;
        Close(s);
        Approve(s, vasyaRun);
        ScenarioAssert.Accepted(s);

        s.Act(new ChangeSeasonStatus(SeasonStatus.Finished));

        // The queue holds completed runs only: Маша's playing run does not block the finish and stays as it was
        ScenarioAssert.Accepted(s);
        Assert.Equal(RunStatus.Playing, s.State.Runs[mashaRun].Status);
        var masha = s.State.Result!.Value.Single(r => r.PlayerId == s.PlayerId("Маша"));
        Assert.Equal(0, masha.Points);
    }
}

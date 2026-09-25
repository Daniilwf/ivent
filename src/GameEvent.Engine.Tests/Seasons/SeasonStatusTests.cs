using GameEvent.Engine.Kernel;
using GameEvent.Engine.Ranking;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Seasons;

/// <summary>
/// SE1: the season goes draft → active → closing → finished → archived, one step at a time; game actions
/// (roll, start, complete) only while it is active; the deadline is set at creation or by a command.
/// </summary>
public class SeasonStatusTests
{
    private static readonly Guid s_seasonId = SequentialIds.Make(0x30000000, 1);
    private static readonly DateTimeOffset s_deadline = FixedClock.SeasonStart.AddDays(30);

    private static readonly SeasonStatus[] s_order =
        [SeasonStatus.Draft, SeasonStatus.Active, SeasonStatus.Closing, SeasonStatus.Finished, SeasonStatus.Archived];

    /// <summary>A draft season with a small pool and the given players, then walked forward to <paramref name="status"/>.</summary>
    private static Scenario SeasonIn(SeasonStatus status, params string[] players)
    {
        var s = Scenario.New().AsDraft()
            .WithCategory("Horror")
            .WithGame("Silent Hill", 12, "Horror")
            .WithGame("Alan Wake", 15, "Horror")
            .WithGame("Doom", 4, "Horror")
            .WithPlayers(players);
        return WalkTo(s, status);
    }

    private static Scenario WalkTo(Scenario s, SeasonStatus status)
    {
        while (s.State.Status != status)
        {
            s.Act(new ChangeSeasonStatus(s.State.Status + 1));
            ScenarioAssert.Accepted(s);
        }

        return s;
    }

    // ---- Creation ----

    [Fact]
    public void Season_is_created_as_a_draft_with_its_name_and_deadline()
    {
        // Given no season yet
        var s = Scenario.New();

        // When the admin creates one with a deadline
        s.Act(new CreateSeason(s_seasonId, "Осень 2026", s.Ruleset, s_deadline));

        // Then the season is a draft with that name and deadline, both in the log
        ScenarioAssert.Accepted(s);
        var created = Assert.IsType<SeasonCreated>(Assert.Single(s.Last.Events));
        Assert.Equal("Осень 2026", created.Name);
        Assert.Equal(s_deadline, created.Deadline);
        Assert.Equal(SeasonStatus.Draft, s.State.Status);
        Assert.Equal("Осень 2026", s.State.Name);
        Assert.Equal(s_deadline, s.State.Deadline);
    }

    [Fact]
    public void Season_created_without_a_deadline_has_none()
    {
        var s = Scenario.New();

        s.Act(new CreateSeason(s_seasonId, "Осень 2026", s.Ruleset));

        ScenarioAssert.Accepted(s);
        Assert.Null(Assert.IsType<SeasonCreated>(Assert.Single(s.Last.Events)).Deadline);
        Assert.Null(s.State.Deadline);
        Assert.Equal(SeasonStatus.Draft, s.State.Status);
    }

    // ---- Transitions ----

    [Fact]
    public void Season_goes_through_every_status_in_order()
    {
        // Given a draft season with players
        var s = SeasonIn(SeasonStatus.Draft, "Вася", "Петя");

        // When the admin moves it forward step by step
        foreach (var (from, to) in s_order.Zip(s_order.Skip(1)))
        {
            var before = s.State;
            s.Act(new ChangeSeasonStatus(to));

            // Then each step is one event with the old and new status; the finish also records the result (D-101)
            ScenarioAssert.Accepted(s);
            IGameEvent[] expected = to == SeasonStatus.Finished
                ? [new SeasonStatusChanged(from, to), new SeasonResultRecorded(Leaderboard.Build(before))]
                : [new SeasonStatusChanged(from, to)];
            Assert.Equal(expected, s.Last.Events);
            Assert.Equal(to, s.State.Status);
        }
    }

    [Fact]
    public void Starting_a_draft_season_changes_nothing_but_the_status()
    {
        var s = SeasonIn(SeasonStatus.Draft, "Вася");
        var before = s.State;

        s.Act(new ChangeSeasonStatus(SeasonStatus.Active));

        ScenarioAssert.Accepted(s);
        Assert.Equal(before with { Status = SeasonStatus.Active }, s.State);
    }

    public static TheoryData<SeasonStatus, SeasonStatus> InvalidTransitions()
    {
        var data = new TheoryData<SeasonStatus, SeasonStatus>();
        foreach (var from in s_order)
        {
            foreach (var to in s_order.Where(to => to != from + 1))
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidTransitions))]
    public void Status_can_only_move_to_the_next_one(SeasonStatus from, SeasonStatus to)
    {
        // Given a season in «from» (same status, a skip, or a step back are all invalid)
        var s = SeasonIn(from, "Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ChangeSeasonStatus(to)), RejectionCodes.SeasonInvalidTransition);
    }

    [Fact]
    public void Undefined_status_value_is_an_invalid_transition()
    {
        var s = SeasonIn(SeasonStatus.Draft, "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ChangeSeasonStatus((SeasonStatus)99)), RejectionCodes.SeasonInvalidTransition);
    }

    // ---- Game actions only while active ----

    [Fact]
    public void Game_actions_are_rejected_in_a_draft_season()
    {
        var s = SeasonIn(SeasonStatus.Draft, "Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.SeasonNotActive);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Start("Вася"), RejectionCodes.SeasonNotActive);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Complete("Вася"), RejectionCodes.SeasonNotActive);
    }

    [Theory]
    [InlineData(SeasonStatus.Closing)]
    [InlineData(SeasonStatus.Finished)]
    [InlineData(SeasonStatus.Archived)]
    public void Game_actions_are_rejected_once_the_season_is_no_longer_active(SeasonStatus status)
    {
        // Given, while active: Вася idle, Петя with an offered game, Маша playing
        var s = SeasonIn(SeasonStatus.Active, "Вася", "Петя", "Маша")
            .Roll("Петя")
            .Roll("Маша").Start("Маша");

        // When the season moves on
        WalkTo(s, status);

        // Then each player's next game action is refused because of the season, not the turn phase
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.SeasonNotActive);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Start("Петя"), RejectionCodes.SeasonNotActive);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Complete("Маша", Difficulty.Normal, 6), RejectionCodes.SeasonNotActive);
    }

    [Fact]
    public void Game_actions_work_once_the_draft_season_is_started()
    {
        var s = SeasonIn(SeasonStatus.Draft, "Вася");

        WalkTo(s, SeasonStatus.Active).Roll("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<GameRolled>());
        Assert.Equal(TurnPhase.Rolling, s.Player("Вася").Phase);
    }

    // ---- Deadline ----

    [Theory]
    [InlineData(SeasonStatus.Draft)]
    [InlineData(SeasonStatus.Active)]
    public void Deadline_can_be_set_by_the_admin(SeasonStatus status)
    {
        var s = SeasonIn(status, "Вася");

        s.Act(new SetSeasonDeadline(s_deadline));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new SeasonDeadlineSet(s_deadline)], s.Last.Events);
        Assert.Equal(s_deadline, s.State.Deadline);
        Assert.Equal(status, s.State.Status);
    }

    [Fact]
    public void Deadline_set_at_creation_can_be_moved()
    {
        var s = Scenario.New();
        s.Act(new CreateSeason(s_seasonId, "Осень 2026", s.Ruleset, s_deadline));
        ScenarioAssert.Accepted(s);
        var later = s_deadline.AddDays(7);

        s.Act(new SetSeasonDeadline(later));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new SeasonDeadlineSet(later)], s.Last.Events);
        Assert.Equal(later, s.State.Deadline);
    }

    [Fact]
    public void Deadline_can_be_removed()
    {
        var s = SeasonIn(SeasonStatus.Active, "Вася");
        s.Act(new SetSeasonDeadline(s_deadline));
        ScenarioAssert.Accepted(s);

        s.Act(new SetSeasonDeadline(null));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new SeasonDeadlineSet(null)], s.Last.Events);
        Assert.Null(s.State.Deadline);
    }

    [Theory]
    [InlineData(SeasonStatus.Finished)]
    [InlineData(SeasonStatus.Archived)]
    public void Deadline_cannot_be_changed_once_the_season_is_finished(SeasonStatus status)
    {
        var s = SeasonIn(status, "Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new SetSeasonDeadline(s_deadline)), RejectionCodes.SeasonClosed);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new SetSeasonDeadline(null)), RejectionCodes.SeasonClosed);
    }

    // ---- Before the season exists ----

    public static TheoryData<string, ICommand> SeasonCommandsBeforeSeason() => new()
    {
        { "status", new ChangeSeasonStatus(SeasonStatus.Active) },
        { "deadline", new SetSeasonDeadline(s_deadline) },
    };

    [Theory]
    [MemberData(nameof(SeasonCommandsBeforeSeason))]
    public void Season_commands_before_the_season_exists_are_rejected(string what, ICommand command)
    {
        _ = what;
        var s = Scenario.New();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(command), RejectionCodes.SeasonNotCreated);
    }
}

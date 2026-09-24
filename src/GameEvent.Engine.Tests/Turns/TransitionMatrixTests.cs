using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Turns;

/// <summary>
/// The turn is a strict state machine (SPEC «Игровой цикл», K-5, D-91): every player turn command in every turn
/// state is either accepted and moves to its phase, or rejected without events with the code of the first failed
/// check. Check order (D-91): season running → player known → no pending choice (except MakeChoice,
/// DeclareAlreadyPlayed, D-92, and Reroll, D-93) → phase.
/// The active run limit is held by the phase and RulesetSupport, there is no separate check (D-91).
/// The expected table lives here, independent of the engine's own table.
/// </summary>
public class TransitionMatrixTests
{
    public enum TurnState
    {
        Idle,
        RollingWithOffer,
        RollingWithChoice,
        Playing,
    }

    public enum TurnCommand
    {
        Roll,
        Start,
        Complete,
        Choose,
        DeclareAlreadyPlayed,
        Reroll,
    }

    /// <summary>Expected outcome: a rejection code, or null, the phase the player ends in and whether a choice is pending.</summary>
    private sealed record Expected(string? Code, TurnPhase? Phase = null, bool ChoicePending = false);

    private static readonly Dictionary<(TurnState, TurnCommand), Expected> s_table = new()
    {
        [(TurnState.Idle, TurnCommand.Roll)] = new(null, TurnPhase.Rolling),
        [(TurnState.Idle, TurnCommand.Start)] = new(RejectionCodes.WrongPhase),
        [(TurnState.Idle, TurnCommand.Complete)] = new(RejectionCodes.WrongPhase),
        [(TurnState.Idle, TurnCommand.Choose)] = new(RejectionCodes.NoPendingChoice),
        [(TurnState.Idle, TurnCommand.DeclareAlreadyPlayed)] = new(RejectionCodes.WrongPhase),
        [(TurnState.Idle, TurnCommand.Reroll)] = new(RejectionCodes.WrongPhase),

        [(TurnState.RollingWithOffer, TurnCommand.Roll)] = new(RejectionCodes.WrongPhase),
        [(TurnState.RollingWithOffer, TurnCommand.Start)] = new(null, TurnPhase.Playing),
        [(TurnState.RollingWithOffer, TurnCommand.Complete)] = new(RejectionCodes.WrongPhase),
        [(TurnState.RollingWithOffer, TurnCommand.Choose)] = new(RejectionCodes.NoPendingChoice),
        // «Уже проходил»: the game is excluded and the wheel spins again at once (D-92); other games are left, one is offered
        [(TurnState.RollingWithOffer, TurnCommand.DeclareAlreadyPlayed)] = new(null, TurnPhase.Rolling),
        // Reroll: the offer is given up and another game is offered at once, the first reroll is free (D-93)
        [(TurnState.RollingWithOffer, TurnCommand.Reroll)] = new(null, TurnPhase.Rolling),

        // A pending choice blocks every other turn command, even those the phase alone would allow (Start)
        [(TurnState.RollingWithChoice, TurnCommand.Roll)] = new(RejectionCodes.ChoicePending),
        [(TurnState.RollingWithChoice, TurnCommand.Start)] = new(RejectionCodes.ChoicePending),
        [(TurnState.RollingWithChoice, TurnCommand.Complete)] = new(RejectionCodes.ChoicePending),
        // Choosing --> Playing (SPEC diagram): the chosen game starts at once (D-91)
        [(TurnState.RollingWithChoice, TurnCommand.Choose)] = new(null, TurnPhase.Playing),
        // A pending choice does not block «Уже проходил» on an option: the choice is rolled anew from the games left
        [(TurnState.RollingWithChoice, TurnCommand.DeclareAlreadyPlayed)] = new(null, TurnPhase.Rolling, ChoicePending: true),
        // Nor a reroll: the whole choice is given up and a new choice is rolled from the three other games (D-93)
        [(TurnState.RollingWithChoice, TurnCommand.Reroll)] = new(null, TurnPhase.Rolling, ChoicePending: true),

        [(TurnState.Playing, TurnCommand.Roll)] = new(RejectionCodes.WrongPhase),
        [(TurnState.Playing, TurnCommand.Start)] = new(RejectionCodes.WrongPhase),
        [(TurnState.Playing, TurnCommand.Complete)] = new(null, TurnPhase.Idle),
        [(TurnState.Playing, TurnCommand.Choose)] = new(RejectionCodes.NoPendingChoice),
        [(TurnState.Playing, TurnCommand.DeclareAlreadyPlayed)] = new(RejectionCodes.WrongPhase),
        [(TurnState.Playing, TurnCommand.Reroll)] = new(RejectionCodes.WrongPhase),
    };

    public static TheoryData<TurnState, TurnCommand> DisallowedPairs() =>
        Pairs(e => e.Code is not null);

    public static TheoryData<TurnState, TurnCommand> AllowedPairs() =>
        Pairs(e => e.Code is null);

    public static TheoryData<TurnState, TurnCommand> AllPairs() => Pairs(_ => true);

    private static TheoryData<TurnState, TurnCommand> Pairs(Func<Expected, bool> filter)
    {
        var data = new TheoryData<TurnState, TurnCommand>();
        foreach (var ((state, command), expected) in s_table.Where(p => filter(p.Value)))
        {
            data.Add(state, command);
        }

        return data;
    }

    /// <summary>
    /// A fresh scenario with Вася in <paramref name="state"/>. Every game has hours, so a completion is valid.
    /// Only the choice state plays with <c>choiceCount</c> 3; the others keep 1, so they do not depend on it.
    /// Six games: after a choice of three is given up by a reroll, three are still there for a new choice.
    /// </summary>
    private static Scenario In(TurnState state)
    {
        var s = Scenario.New();
        if (state == TurnState.RollingWithChoice)
        {
            s.WithRuleset(r => r with { Roll = r.Roll with { ChoiceCount = 3 } });
        }

        s.WithCategory("Horror")
            .WithGame("Silent Hill", 12, "Horror")
            .WithGame("Alan Wake", 15, "Horror")
            .WithGame("Dead Space", 9, "Horror")
            .WithGame("Outlast", 5, "Horror")
            .WithGame("Amnesia", 8, "Horror")
            .WithGame("Soma", 10, "Horror")
            .WithPlayers("Вася", "Петя");

        switch (state)
        {
            case TurnState.Idle:
                break;
            case TurnState.RollingWithOffer:
                s.Roll("Вася");
                Assert.NotNull(s.Player("Вася").Offer);
                break;
            case TurnState.RollingWithChoice:
                s.Roll("Вася");
                Assert.IsType<GameChoiceRolled>(Assert.Single(s.Last.Events));
                Assert.NotNull(s.Player("Вася").Choice);
                break;
            case TurnState.Playing:
                s.Roll("Вася").Start("Вася");
                break;
        }

        return s;
    }

    /// <summary>
    /// The command of <paramref name="player"/>; MakeChoice targets the player's pending choice if there is one;
    /// «Уже проходил» names the offered game, the first option, the game being played, or else a pool game.
    /// </summary>
    private static ICommand Command(Scenario s, TurnCommand command, Guid player) =>
        command switch
        {
            TurnCommand.Roll => new RollGame(player),
            TurnCommand.Start => new StartRun(player),
            TurnCommand.Complete => new CompleteRun(player, Difficulty.Normal),
            TurnCommand.Choose => s.State.Players.TryGetValue(player, out var p) && p.Choice is { } choice
                ? new MakeChoice(player, choice.ChoiceId, choice.Options[0].Id)
                : new MakeChoice(player, SequentialIds.Make(0x50000000, 1), "whatever"),
            TurnCommand.DeclareAlreadyPlayed => new DeclareAlreadyPlayed(player, GameOf(s, player)),
            TurnCommand.Reroll => new Reroll(player),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    private static Guid GameOf(Scenario s, Guid player)
    {
        if (!s.State.Players.TryGetValue(player, out var p))
        {
            return s.GameId("Silent Hill");
        }

        return p.Offer?.GameId
            ?? p.Choice?.Options[0].Game!.GameId
            ?? (p.ActiveRunId is { } run ? s.State.Runs[run].GameId : s.GameId("Silent Hill"));
    }

    [Fact]
    public void Table_covers_every_turn_command_in_every_turn_state()
    {
        foreach (var state in Enum.GetValues<TurnState>())
        {
            foreach (var command in Enum.GetValues<TurnCommand>())
            {
                Assert.True(s_table.ContainsKey((state, command)), $"No expectation for {command} in {state}.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(DisallowedPairs))]
    public void Every_disallowed_pair_is_rejected_without_events(TurnState state, TurnCommand command)
    {
        var s = In(state);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(Command(x, command, x.PlayerId("Вася"))), s_table[(state, command)].Code!);
    }

    [Theory]
    [MemberData(nameof(AllowedPairs))]
    public void Every_allowed_pair_is_accepted_and_moves_to_its_phase(TurnState state, TurnCommand command)
    {
        var s = In(state);

        s.Act(Command(s, command, s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.NotEmpty(s.Last.Events);
        var player = s.Player("Вася");
        Assert.Equal(s_table[(state, command)].Phase, player.Phase);
        Assert.Equal(s_table[(state, command)].ChoicePending, player.Choice is not null);
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Turn_command_of_one_player_does_not_touch_another(TurnState state, TurnCommand command)
    {
        var s = In(state);
        var petya = s.Player("Петя");

        s.Act(Command(s, command, s.PlayerId("Вася")));

        Assert.Equal(petya, s.Player("Петя"));
    }

    // ---- Check order (D-91): season running beats everything, unknown player next ----

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Every_turn_command_is_rejected_while_the_season_is_closing(TurnState state, TurnCommand command)
    {
        var s = In(state);
        var choice = s.Player("Вася").Choice;
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        ScenarioAssert.Accepted(s);

        // The pending choice survives the status change; the season check comes before it anyway
        Assert.Equal(choice, s.Player("Вася").Choice);
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(Command(x, command, x.PlayerId("Вася"))), RejectionCodes.SeasonNotActive);
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Every_turn_command_is_rejected_after_the_season_is_finished(TurnState state, TurnCommand command)
    {
        var s = In(state);
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing)).Act(new ChangeSeasonStatus(SeasonStatus.Finished));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(Command(x, command, x.PlayerId("Вася"))), RejectionCodes.SeasonNotActive);
    }

    [Theory]
    [InlineData(TurnCommand.Roll)]
    [InlineData(TurnCommand.Start)]
    [InlineData(TurnCommand.Complete)]
    [InlineData(TurnCommand.Choose)]
    [InlineData(TurnCommand.DeclareAlreadyPlayed)]
    [InlineData(TurnCommand.Reroll)]
    public void Turn_command_in_a_draft_season_is_rejected_as_not_active(TurnCommand command)
    {
        var s = Scenario.New().AsDraft()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
            .WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(Command(x, command, x.PlayerId("Вася"))), RejectionCodes.SeasonNotActive);
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Turn_command_of_an_unknown_player_is_rejected(TurnState state, TurnCommand command)
    {
        // Whatever state the known players are in, a stranger is refused before any phase or choice check
        var s = In(state);
        var stranger = SequentialIds.Make(0x10000000, 0x77);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Command(x, command, stranger)), RejectionCodes.PlayerUnknown);
    }

    [Theory]
    [InlineData(TurnCommand.Roll)]
    [InlineData(TurnCommand.Start)]
    [InlineData(TurnCommand.Complete)]
    [InlineData(TurnCommand.Choose)]
    [InlineData(TurnCommand.DeclareAlreadyPlayed)]
    [InlineData(TurnCommand.Reroll)]
    public void Unknown_player_in_a_closing_season_gets_season_not_active(TurnCommand command)
    {
        var s = In(TurnState.Idle);
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        var stranger = SequentialIds.Make(0x10000000, 0x77);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Command(x, command, stranger)), RejectionCodes.SeasonNotActive);
    }

    // ---- Active run limit (SPEC: one active run; the limit is in the ruleset, D-91) ----

    [Fact]
    public void Playing_player_cannot_roll_or_start_a_second_run()
    {
        var s = In(TurnState.Playing);
        Assert.Equal(1, s.Ruleset.Season.MaxActiveRunsPerPlayer);
        var run = s.Player("Вася").ActiveRunId;

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.WrongPhase);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Start("Вася"), RejectionCodes.WrongPhase);

        Assert.Single(s.State.Runs.Values, r => r.PlayerId == s.PlayerId("Вася") && r.Status == RunStatus.Playing);
        Assert.Equal(run, s.Player("Вася").ActiveRunId);
    }

    [Fact]
    public void Several_active_runs_are_still_not_playable()
    {
        // D-91 keeps maxActiveRunsPerPlayer at 1: a season cannot start with 2
        var s = Scenario.New().WithRuleset(r => r with { Season = r.Season with { MaxActiveRunsPerPlayer = 2 } });

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new CreateSeason(SequentialIds.Make(0x30000000, 1), "Тестовый сезон", x.Ruleset)),
            RejectionCodes.RulesetInvalid);
    }
}

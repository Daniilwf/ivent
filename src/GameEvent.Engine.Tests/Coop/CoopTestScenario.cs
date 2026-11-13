using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// Coop in scenarios (task 5.10, D-520…D-523): <c>features.coop</c> on with the numbers of the decisions (points and coins
/// split 0.5 with the rounding up for the roller, invitations live 24 hours, at most 3 pending sent), a linear map, one
/// category of 6-hour games — two coop games («Portal 2», «It Takes Two») first in the pool, then solo ones — and Вася,
/// Петя, Маша and Коля. A completion of a 6-hour game on the test ruleset throws two d4 (normal) and pays 6 coins.
/// </summary>
internal static class CoopTestScenario
{
    public const string CoopGame = "Portal 2";

    public const string SecondCoopGame = "It Takes Two";

    public const string SoloGame = "Silent Hill";

    public const string OtherSoloGame = "Alan Wake";

    public const string ThirdSoloGame = "Dead Space";

    public const int GameHours = 6;

    public const int CoinsPerRun = 6;

    public const string Comment = "Проверка пруфа";

    private static readonly string[] s_soloGames = [SoloGame, OtherSoloGame, ThirdSoloGame, "Outlast", "Doom", "Prey"];

    /// <summary>
    /// A running season; <paramref name="coop"/> false leaves <c>features.coop</c> off (the test ruleset as pinned).
    /// <paramref name="coopHours"/> — the hours of «Portal 2» (its dice and coins).
    /// </summary>
    public static Scenario New(Func<Ruleset, Ruleset>? more = null, int mapLength = 40, decimal? coopHours = GameHours, bool coop = true, int seed = 42)
    {
        var rules = TestRuleset.Create();
        if (coop)
        {
            rules = rules.WithCoop();
        }

        if (more is not null)
        {
            rules = more(rules);
        }

        var s = Scenario.New(rules, seed).WithMapLength(mapLength).WithCategory("Games");
        s.WithCoopGame(CoopGame, coopHours, "Games").WithCoopGame(SecondCoopGame, GameHours, "Games");
        foreach (var title in s_soloGames)
        {
            s.WithGame(title, GameHours, "Games");
        }

        return s.WithPlayers("Вася", "Петя", "Маша", "Коля");
    }

    /// <summary>
    /// The player rolls <paramref name="title"/> and starts it; the clock then moves two hours, past
    /// <c>roll.minPlayMinutesBeforeDrop</c>, so a drop is allowed. Returns the run.
    /// </summary>
    public static Guid PlayTitle(this Scenario s, string player, string title)
    {
        s.RollTitle(player, title).Start(player);
        s.Advance(TimeSpan.FromHours(2));
        return s.Player(player).ActiveRunId!.Value;
    }

    /// <summary>The player plays <paramref name="title"/> alone and completes it with <paramref name="dice"/>; returns the run.</summary>
    public static Guid CompleteTitle(this Scenario s, string player, string title, params int[] dice)
    {
        var run = s.PlayTitle(player, title);
        s.CompleteWith(player, dice);
        return run;
    }

    /// <summary>The player completes his running run on «normal» with the scripted <paramref name="dice"/>; must be accepted.</summary>
    public static Scenario CompleteWith(this Scenario s, string player, params int[] dice)
    {
        s.NextRandom(dice).Complete(player);
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(1));
        return s;
    }

    /// <summary><paramref name="roller"/> invites <paramref name="partner"/> to his running run; must be accepted. Returns the invitation.</summary>
    public static Guid Invite(this Scenario s, string roller, string partner)
    {
        s.Act(new InviteToCoop(s.PlayerId(roller), s.PlayerId(partner)));
        ScenarioAssert.Accepted(s);
        return Assert.Single(s.LastEvents<InteractionCreated>()).Interaction.InteractionId;
    }

    /// <summary>The player answers the invitation; the result is in <see cref="Scenario.Last"/>.</summary>
    public static Scenario Answer(this Scenario s, string player, Guid interaction, bool accept = true) =>
        s.Act(new AnswerInvitation(s.PlayerId(player), interaction, accept));

    /// <summary>The player accepts the invitation; must be accepted.</summary>
    public static Scenario Accept(this Scenario s, string player, Guid interaction)
    {
        s.Answer(player, interaction);
        ScenarioAssert.Accepted(s);
        return s;
    }

    /// <summary><paramref name="roller"/> plays <paramref name="title"/> with <paramref name="partner"/>; returns the coop run.</summary>
    public static Guid CoopRun(this Scenario s, string roller = "Вася", string partner = "Петя", string title = CoopGame)
    {
        var run = s.PlayTitle(roller, title);
        s.Accept(partner, s.Invite(roller, partner));
        return run;
    }

    /// <summary>Executes <paramref name="command"/> and checks it is refused with <paramref name="code"/>, changing nothing.</summary>
    public static void Rejects(this Scenario s, ICommand command, string code) =>
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(command), code);

    public static Guid ActiveRun(this Scenario s, string player) => s.Player(player).ActiveRunId!.Value;

    /// <summary>The invitation as the season holds it now.</summary>
    public static Interaction Invitation(this Scenario s, Guid interaction) => s.State.Interactions[interaction];

    /// <summary>
    /// The season as it matters to the game (as in the undo tests, D-104): the counters of finishes and points changes only
    /// grow, and the tick numbers in players follow them.
    /// </summary>
    public static SeasonState Comparable(SeasonState state) =>
        state with
        {
            FinishesSoFar = 0,
            PointsChanges = 0,
            Players = state.Players.SetItems(state.Players.Select(p => KeyValuePair.Create(p.Key, p.Value with { PointsTick = 0 }))),
        };
}

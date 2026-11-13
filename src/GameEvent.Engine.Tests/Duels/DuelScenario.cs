using GameEvent.Engine.Duels;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Duels;

/// <summary>
/// Duels in scenarios (task 5.12, D-525, D-540): the flag on with the expert panel's numbers (10 coins, 7 days, one open
/// duel, only runs rolled after the acceptance, a 14-day cooldown of the pair, 5 wins a season, a drop forfeits), a pool of
/// 6-hour horror games and Вася, Петя, Маша and Коля. A challenge waits <c>interactions.inviteTtlHours</c> (24 on the test
/// ruleset). A completion of a 6-hour game throws 2d4: dice 1, 1 move 2 cells.
/// </summary>
internal static class DuelScenario
{
    public const int RewardCoins = 10;
    public const int DurationDays = 7;
    public const int CooldownDays = 14;
    public const int InviteTtlHours = 24;

    /// <summary>The ruleset with <c>features.duels</c> on and the duel rules of the expert panel (D-540).</summary>
    public static Ruleset WithDuels(this Ruleset ruleset, Func<DuelRules, DuelRules>? change = null)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        var duels = new DuelRules
        {
            RewardCoins = RewardCoins,
            DurationDays = DurationDays,
            MaxOpenPerPlayer = 1,
            CountRunsStartedAfterAccept = true,
            SamePairCooldownDays = CooldownDays,
            MaxWinsPerSeason = 5,
            DropForfeits = true,
        };
        return ruleset with
        {
            Features = ruleset.Features with { Duels = true },
            Duels = change is null ? duels : change(duels),
            Interactions = ruleset.Interactions with { InviteTtlHours = InviteTtlHours },
        };
    }

    public static Scenario New(Func<DuelRules, DuelRules>? duels = null, Func<Ruleset, Ruleset>? more = null, int mapLength = 60, int seed = 42)
    {
        var rules = TestRuleset.Create().WithDuels(duels);
        if (more is not null)
        {
            rules = more(rules);
        }

        var s = Scenario.New(rules, seed).WithMapLength(mapLength).WithCategory("Horror");
        s.WithGame("Silent Hill", 6, "Horror")
            .WithGame("Doom 3", 6, "Horror")
            .WithGame("Alan Wake", 6, "Horror")
            .WithGame("Dead Space", 6, "Horror")
            .WithGame("Outlast", 6, "Horror")
            .WithGame("Thief", 6, "Horror")
            .WithGame("Gothic", 6, "Horror")
            .WithGame("Prey", 6, "Horror")
            .WithGame("Soma", 6, "Horror")
            .WithGame("Amnesia", 6, "Horror");
        return s.WithPlayers("Вася", "Петя", "Маша", "Коля");
    }

    public static Scenario Challenge(this Scenario s, string player, string opponent)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Act(new ChallengeToDuel(s.PlayerId(player), s.PlayerId(opponent)));
    }

    /// <summary>The player challenges the opponent; the challenge must be accepted. Returns the duel's id.</summary>
    public static Guid Challenged(this Scenario s, string player, string opponent)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.Challenge(player, opponent);
        return s.Last.IsAccepted
            ? Assert.Single(s.LastEvents<DuelChallenged>()).Duel.DuelId
            : throw new InvalidOperationException($"The challenge {player} → {opponent} was rejected: {s.Last.Rejection}");
    }

    public static Scenario Answer(this Scenario s, string player, Guid duel, bool accept)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Act(new AnswerDuel(s.PlayerId(player), duel, accept));
    }

    /// <summary>The opponent accepts the duel; the answer must be accepted by the engine.</summary>
    public static Scenario Accepted(this Scenario s, string player, Guid duel)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.Answer(player, duel, accept: true);
        return s.Last.IsAccepted
            ? s
            : throw new InvalidOperationException($"{player} accepting the duel was rejected: {s.Last.Rejection}");
    }

    /// <summary>The player challenges the opponent and the opponent accepts at once. Returns the duel's id.</summary>
    public static Guid StartDuel(this Scenario s, string player, string opponent)
    {
        var duel = s.Challenged(player, opponent);
        s.Accepted(opponent, duel);
        return duel;
    }

    public static Scenario Withdraw(this Scenario s, string player, Guid duel)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Act(new WithdrawDuel(s.PlayerId(player), duel));
    }

    public static Duel Duel(this Scenario s, Guid duel)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.State.Duels[duel];
    }

    /// <summary>The player rolls <paramref name="title"/> and starts it; returns the run.</summary>
    public static Guid Play(this Scenario s, string player, string title)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.RollTitle(player, title).Start(player);
        return s.Player(player).ActiveRunId!.Value;
    }

    /// <summary>The player completes the run he plays with dice 1, 1 (2 cells); returns the run.</summary>
    public static Guid CompleteActive(this Scenario s, string player)
    {
        ArgumentNullException.ThrowIfNull(s);
        var run = s.Player(player).ActiveRunId!.Value;
        s.NextRandom(1, 1).Complete(player);
        return run;
    }

    /// <summary>The player rolls, starts and completes <paramref name="title"/> with dice 1, 1; returns the run.</summary>
    public static Guid CompleteTitle(this Scenario s, string player, string title, Difficulty difficulty = Difficulty.Normal)
    {
        ArgumentNullException.ThrowIfNull(s);
        var run = s.Play(player, title);
        s.NextRandom(1, 1).Complete(player, difficulty);
        return run;
    }

    /// <summary>The coins the duels paid the player in the last command (their own changes, <see cref="CoinsReason.Duel"/>).</summary>
    public static IReadOnlyList<CoinsChanged> DuelCoins(this Scenario s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return [.. s.LastEvents<CoinsChanged>().Where(c => c.Reason is CoinsReason.Duel or CoinsReason.DuelRevoked)];
    }

    /// <summary>Every duel event of the last command.</summary>
    public static IReadOnlyList<IGameEvent> DuelEvents(this Scenario s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return [.. s.Last.Events.Where(e => e is DuelChallenged or DuelAnswered or DuelWithdrawn or DuelExpired or DuelSettled or DuelWinRevoked)];
    }
}

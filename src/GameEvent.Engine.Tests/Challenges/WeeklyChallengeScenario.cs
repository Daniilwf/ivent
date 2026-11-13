using GameEvent.Engine.Challenges;
using GameEvent.Engine.Content;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Tests.Content;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Challenges;

/// <summary>
/// The weekly challenge in scenarios (task 5.13, D-526): the flag on with a 7-day window, a pool of 6-hour horror games
/// with release years around the «Олдскул» border (before 2005), and Вася, Петя and Маша. A completion of a 6-hour game on
/// the test ruleset pays 6 coins and throws 2d4 (scripted 1, 1).
/// </summary>
internal static class WeeklyChallengeScenario
{
    public const int WindowDays = 7;

    /// <summary>The ruleset with <c>features.weeklyChallenge</c> on and a <see cref="WindowDays"/>-day window.</summary>
    public static Ruleset WithWeeklyChallenge(this Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        return ruleset with
        {
            Features = ruleset.Features with { WeeklyChallenge = true },
            WeeklyChallenge = ruleset.WeeklyChallenge with { DurationDays = WindowDays, DefaultRewardCoins = 10 },
        };
    }

    public static Scenario New(Func<Ruleset, Ruleset>? more = null, int mapLength = 60, int seed = 42)
    {
        var rules = TestRuleset.Create().WithWeeklyChallenge();
        if (more is not null)
        {
            rules = more(rules);
        }

        var s = Scenario.New(rules, seed).WithMapLength(mapLength).WithCategory("Horror");
        s.WithReleasedGame("Silent Hill 3", 6, 2003, "Horror")
            .WithReleasedGame("Doom 3", 6, 2004, "Horror")
            .WithReleasedGame("F.E.A.R.", 6, 2005, "Horror")
            .WithGame("Indie Horror", 6, "Horror")
            .WithReleasedGame("Alan Wake", 6, 2010, "Horror")
            .WithReleasedGame("Resident Evil 2", 6, 1998, "Horror")
            .WithReleasedGame("Thief", 6, 1998, "Horror")
            .WithReleasedGame("Dead Space", 6, 2008, "Horror")
            .WithReleasedGame("Outlast", 6, 2013, "Horror")
            .MarkCoop("Silent Hill 3");
        return s.WithPlayers("Вася", "Петя", "Маша");
    }

    /// <summary>The «Олдскул» example of docs/CONTENT.md, parsed as the content format reads it.</summary>
    public static WeeklyChallengeDefinition OldSchool() =>
        ContentJson.Parse<WeeklyChallengeDefinition>(ContentExamplesTests.Example("challenge"));

    /// <summary>A challenge checked by the admin: «пройди хоррор на стриме», paying <paramref name="coins"/>.</summary>
    public static WeeklyChallengeDefinition Manual(string id = "stream-horror", int coins = 15) =>
        new()
        {
            Id = id,
            Name = "Хоррор на стриме",
            Description = "Пройди хоррор на стриме, ссылку на запись — в пруф.",
            Condition = new ConditionSpec { Game = new GameFilterSpec { Tags = ["Horror"] } },
            Reward = new RewardSpec { Coins = coins },
            Check = ChallengeCheck.Manual,
        };

    /// <summary>An automatic challenge on the run's difficulty: «пройди на харде».</summary>
    public static WeeklyChallengeDefinition HardWeek(int coins = 5) =>
        new()
        {
            Id = "hard-week",
            Name = "Неделя харда",
            Description = "Пройди любую игру на сложной или выше.",
            Condition = new ConditionSpec { DifficultyAtLeast = Difficulty.Hard },
            Reward = new RewardSpec { Coins = coins },
            Check = ChallengeCheck.Auto,
        };

    /// <summary>An automatic challenge every horror game of the pool meets, paying <paramref name="coins"/>.</summary>
    public static WeeklyChallengeDefinition HorrorWeek(int coins = 5) =>
        new()
        {
            Id = "horror-week",
            Name = "Неделя хоррора",
            Description = "Пройди хоррор.",
            Condition = new ConditionSpec { Game = new GameFilterSpec { Tags = ["Horror"] } },
            Reward = new RewardSpec { Coins = coins },
            Check = ChallengeCheck.Auto,
        };

    /// <summary>The admin sets the challenge from <paramref name="startsAt"/> (now when null); returns its id.</summary>
    public static Guid SetChallenge(this Scenario s, WeeklyChallengeDefinition definition, DateTimeOffset? startsAt = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.Act(new SetWeeklyChallenge(definition, startsAt ?? s.Clock.UtcNow));
        return s.Last.IsAccepted
            ? Assert.Single(s.LastEvents<WeeklyChallengeSet>()).ChallengeId
            : throw new InvalidOperationException($"Setting the weekly challenge was rejected: {s.Last.Rejection}");
    }

    /// <summary>The player rolls <paramref name="title"/> and starts it; returns the run.</summary>
    public static Guid Play(this Scenario s, string player, string title)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.RollTitle(player, title).Start(player);
        return s.Player(player).ActiveRunId!.Value;
    }

    /// <summary>The player rolls, starts and completes <paramref name="title"/> with dice 1, 1; returns the run.</summary>
    public static Guid CompleteTitle(this Scenario s, string player, string title, Difficulty difficulty = Difficulty.Normal)
    {
        ArgumentNullException.ThrowIfNull(s);
        var run = s.Play(player, title);
        s.NextRandom(1, 1).Complete(player, difficulty);
        return run;
    }

    public static Scenario Mark(this Scenario s, Guid challenge, string player, Guid run)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Act(new MarkWeeklyChallenge(challenge, s.PlayerId(player), run));
    }

    /// <summary>The players holding a completion of the challenge now.</summary>
    public static IReadOnlyList<Guid> Completers(this Scenario s, Guid challenge)
    {
        ArgumentNullException.ThrowIfNull(s);
        return [.. s.State.WeeklyChallenges[challenge].Completions.Select(c => c.PlayerId)];
    }
}

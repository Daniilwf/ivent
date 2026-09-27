using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Simulator.Cli;
using GameEvent.Simulator.Play;
using GameEvent.Simulator.Setup;
using GameEvent.Simulator.Tests.Support;

namespace GameEvent.Simulator.Tests.Play;

/// <summary>
/// SM2: a command the engine refuses never makes a bot loop — it waits for the admin or gives up the session. The loop
/// guard never trips, and a bot's refusals stay within a few per day.
/// </summary>
public class RejectedCommandTests
{
    private const int Days = 14;

    [Fact]
    public void Bots_wait_for_an_admin_who_never_checks_instead_of_looping()
    {
        // The limit of one unchecked run and an admin who skips every check: after one completion each roll is refused
        var settings = new SimulationSettings { Admin = new AdminBehaviour { SkipCheckChance = 1 } };
        var inputs = SimulatorFixtures.Inputs(SimulatorFixtures.WithUncheckedLimit(1), settings: settings);

        var seasons = SimulationRunner.Play(inputs, 3, Days, 5);

        Assert.All(seasons, s => Assert.True(s.Rejections.GetValueOrDefault(RejectionCodes.TooManyUncheckedRuns) > 0));
        AssertNoLoops(seasons, settings);

        // Blocked: at most the one run before the admin's final review
        Assert.All(seasons.SelectMany(s => s.Bots), b => Assert.True(b.Completed <= 1, $"bot {b.Index} completed {b.Completed}"));
        Assert.Contains(seasons.SelectMany(s => s.Bots), b => b.BlockedHours > 0);
    }

    [Fact]
    public void Bots_give_up_the_session_when_the_pool_runs_out()
    {
        var pool = new PoolFile(
            [new PoolFileCategory("Puzzle", 1)],
            [new PoolFileGame("Portal", ["Puzzle"], 3), new PoolFileGame("Limbo", ["Puzzle"], 3), new PoolFileGame("Inside", ["Puzzle"], 4)]);
        var settings = new SimulationSettings();
        var inputs = SimulatorFixtures.Inputs(pool: pool, settings: settings);

        var seasons = SimulationRunner.Play(inputs, 3, Days, 9);

        Assert.All(seasons, s => Assert.True(s.Rejections.GetValueOrDefault(RejectionCodes.NoAvailableGames) > 0));
        AssertNoLoops(seasons, settings);
        Assert.All(seasons, s => Assert.True(s.Runs.Count(r => r.End == RunEnd.Completed) <= 3));
    }

    [Fact]
    public void Bots_start_the_game_when_the_engine_refuses_a_reroll()
    {
        // Every bot wants to reroll every game, but the pool has a single game: the engine refuses, the bot plays it
        var pool = new PoolFile([new PoolFileCategory("Puzzle", 1)], [new PoolFileGame("Portal", ["Puzzle"], 3)]);
        var settings = new SimulationSettings
        {
            Players = [new("active", 1)],
            Behaviour = new BotBehaviour { RerollAboveHours = 0, AlreadyPlayedChance = 0, TechRerollChance = 0, RandomDropChance = 0 },
        };
        var inputs = SimulatorFixtures.Inputs(pool: pool, settings: settings);

        var seasons = SimulationRunner.Play(inputs, 3, Days, 13);

        AssertNoLoops(seasons, settings);
        Assert.All(seasons, s => Assert.True(s.Rejections.GetValueOrDefault(RejectionCodes.NoAvailableGames) > 0));
        Assert.All(seasons.SelectMany(s => s.Bots), b => Assert.Equal((1, 0, 0), (b.Completed, b.FreeRerolls, b.PaidRerolls)));
    }

    [Fact]
    public void Bots_never_pay_for_a_reroll_they_cannot_afford()
    {
        // Every bot would pay for a reroll of every game; it checks its coins first, so the engine never refuses for coins
        var rules = RulesetJson.Default();
        rules = rules with { Roll = rules.Roll with { FreeRerollsPerRoll = 0 } };
        var settings = new SimulationSettings { Behaviour = new BotBehaviour { PaidRerollAboveHours = 0, RerollAboveHours = null } };
        var inputs = SimulatorFixtures.Inputs(rules, settings: settings);

        var seasons = SimulationRunner.Play(inputs, 3, Days, 13);

        AssertNoLoops(seasons, settings);
        Assert.All(seasons, s => Assert.False(s.Rejections.ContainsKey(RejectionCodes.NotEnoughCoins)));
        Assert.All(seasons, s => Assert.True(s.Bots.Sum(b => b.Completed) > 0));
    }

    private static void AssertNoLoops(IReadOnlyList<SeasonOutcome> seasons, SimulationSettings settings)
    {
        // A refused roll ends the session or waits for the next check: a few per day at most
        var perDay = settings.Admin.CheckHours.Count + 3;
        Assert.All(seasons, s => Assert.Equal(0, s.GuardTrips));
        Assert.All(seasons.SelectMany(s => s.Bots), b => Assert.True(
            b.Rejected <= Days * perDay, $"bot {b.Index} had {b.Rejected} refused commands in {Days} days"));
    }
}

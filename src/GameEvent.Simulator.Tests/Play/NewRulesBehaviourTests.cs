using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Simulator.Cli;
using GameEvent.Simulator.Play;
using GameEvent.Simulator.Setup;
using GameEvent.Simulator.Tests.Support;

namespace GameEvent.Simulator.Tests.Play;

/// <summary>
/// SM9…SM11: the bots live with the owner's rules of 2026-09-28 — drops in a row add penalty dice (D-324), so a dropper
/// keeps a long game once it has dropped in a row (D-362); a game tagged for a wish reroll (D-325) may be given up for free
/// (D-363); the admin bot's reject takes all the move gave (D-321, D-327) and, rarely, comes with the drop penalty (D-364).
/// </summary>
public class NewRulesBehaviourTests
{
    private const int Days = 21;

    // Droppers drop every game they start; nothing else ends a run early
    private static BotBehaviour DropEverything(int? streakLimit) => new()
    {
        DropAboveHours = 0,
        DropperShare = 1,
        RandomDropChance = 0,
        TechRerollChance = 0,
        AlreadyPlayedChance = 0,
        RerollAboveHours = null,
        DropStreakLimit = streakLimit,
    };

    [Fact]
    public void A_dropper_keeps_the_game_once_it_has_dropped_in_a_row()
    {
        // Rejects make a completion stop counting between the plan and the drop: the bot looks again at the drop
        var settings = new SimulationSettings { Behaviour = DropEverything(1), Admin = new AdminBehaviour { RejectChance = 0.2 } };

        var seasons = SimulationRunner.Play(SimulatorFixtures.Inputs(settings: settings), 4, Days, 61);

        var drops = seasons.SelectMany(s => s.Runs).Where(r => r.End == RunEnd.Dropped).ToList();
        Assert.NotEmpty(drops);
        Assert.All(drops, r => Assert.Equal(0, r.DropStreak));
        Assert.Contains(seasons.SelectMany(s => s.Bots), b => b.KeptForStreak > 0);
        Assert.Contains(seasons.SelectMany(s => s.Runs), r => r.End == RunEnd.Completed);
    }

    [Fact]
    public void A_higher_limit_lets_the_streak_grow_to_it_and_no_further()
    {
        var settings = new SimulationSettings { Behaviour = DropEverything(2) };

        var seasons = SimulationRunner.Play(SimulatorFixtures.Inputs(settings: settings), 4, Days, 62);

        var drops = seasons.SelectMany(s => s.Runs).Where(r => r.End == RunEnd.Dropped).ToList();
        Assert.Equal([0, 1], drops.Select(r => r.DropStreak!.Value).Distinct().Order());
    }

    [Fact]
    public void A_streak_that_adds_no_dice_does_not_hold_a_drop_back()
    {
        var rules = RulesetJson.Default();
        rules = rules with { Drop = rules.Drop with { ConsecutiveExtraDice = 0 } };
        var settings = new SimulationSettings { Behaviour = DropEverything(1) };

        var seasons = SimulationRunner.Play(SimulatorFixtures.Inputs(rules, settings: settings), 4, Days, 68);

        Assert.Contains(seasons.SelectMany(s => s.Runs), r => r.End == RunEnd.Dropped && r.DropStreak >= 1);
        Assert.All(seasons.SelectMany(s => s.Bots), b => Assert.Equal(0, b.KeptForStreak));
    }

    [Fact]
    public void A_bot_that_ignores_the_streak_pays_more_dice_for_each_drop_in_a_row()
    {
        var settings = new SimulationSettings { Behaviour = DropEverything(null) };
        var rules = RulesetJson.Default();

        var seasons = SimulationRunner.Play(SimulatorFixtures.Inputs(rules, settings: settings), 4, Days, 63);

        var drops = seasons.SelectMany(s => s.Runs).Where(r => r.End == RunEnd.Dropped).ToList();
        Assert.Contains(drops, r => r.DropStreak >= 2);
        Assert.All(seasons.SelectMany(s => s.Bots), b => Assert.Equal(0, b.KeptForStreak));

        // The engine's penalty: count + streak × consecutiveExtraDice dice of the same sides (D-324)
        var drop = rules.Drop;
        Assert.All(drops, r => Assert.InRange(r.Penalty, 0, (drop.PenaltyDice.Count + (r.DropStreak!.Value * drop.ConsecutiveExtraDice!.Value)) * drop.PenaltyDice.Sides));
        var first = drops.Where(r => r.DropStreak == 0).Average(r => r.Penalty);
        var later = drops.Where(r => r.DropStreak >= 2).Average(r => r.Penalty);
        Assert.True(later > first + 2, $"penalty of a first drop {first:F1}, of a third or later {later:F1}");
    }

    [Fact]
    public void Without_wish_tags_in_the_ruleset_no_bot_tries_a_wish_reroll()
    {
        var settings = new SimulationSettings { Behaviour = new BotBehaviour { WishRerollChance = 1 } };

        var seasons = SimulationRunner.Play(SimulatorFixtures.Inputs(settings: settings), 3, Days, 64);

        Assert.All(seasons.SelectMany(s => s.Bots), b => Assert.Equal(0, b.WishRerolls));
        Assert.DoesNotContain(seasons.SelectMany(s => s.Runs), r => r.End == RunEnd.WishRerolled);
        Assert.All(seasons, s => Assert.False(s.Rejections.ContainsKey(RejectionCodes.WishRerollNotListed)));
    }

    [Fact]
    public void Bots_give_up_games_of_the_wish_tags_for_free_and_only_those()
    {
        var rules = WithWishTags("Horror", "Visual Novel");
        var settings = new SimulationSettings { Behaviour = new BotBehaviour { WishRerollChance = 1 } };
        var inputs = SimulatorFixtures.Inputs(rules, settings: settings);

        var seasons = SimulationRunner.Play(inputs, 3, Days, 65);

        var wished = seasons.SelectMany(s => s.Runs).Where(r => r.End == RunEnd.WishRerolled).ToList();
        Assert.NotEmpty(wished);
        Assert.All(wished, r => Assert.Equal((0, 0), (r.Points, r.Penalty)));
        Assert.All(seasons, s => Assert.Equal(s.Runs.Count(r => r.End == RunEnd.WishRerolled), s.Bots.Sum(b => b.WishRerolls)));
        Assert.All(seasons, s => Assert.False(s.Rejections.ContainsKey(RejectionCodes.WishRerollNotListed)));
        Assert.All(seasons, s => Assert.False(s.Rejections.ContainsKey(RejectionCodes.WishRerollImposed)));

        // With every listed game given up, a completed game never carries a listed tag
        Assert.All(seasons.SelectMany(s => s.Runs).Where(r => r.End == RunEnd.Completed), r => Assert.False(r.WishListed));
        Assert.All(wished, r => Assert.True(r.WishListed));
    }

    [Fact]
    public void A_reject_takes_back_the_cells_its_move_gave_and_never_moves_the_token_forward()
    {
        var settings = new SimulationSettings { Admin = new AdminBehaviour { RejectChance = 0.3 } };

        var seasons = SimulationRunner.Play(SimulatorFixtures.Inputs(settings: settings), 4, Days, 66);

        var rejected = seasons.SelectMany(s => s.Runs).Where(r => r.End == RunEnd.Rejected).ToList();
        Assert.NotEmpty(rejected);
        Assert.All(rejected, r => Assert.Equal(0, r.Points));
        Assert.All(rejected, r => Assert.True(r.RejectCells >= 0, $"a reject moved the token {-r.RejectCells} cells forward"));

        // On the linear map the gain of a move is its cells (D-321): an unfinished player goes back by all of them
        Assert.All(rejected.Where(r => !r.RejectedAfterFinish), r => Assert.Equal(r.MoveCells, r.RejectCells));
        Assert.All(rejected, r => Assert.False(r.PenalizedReject));
        Assert.All(rejected, r => Assert.Equal(0, r.Penalty));
    }

    [Fact]
    public void A_penalized_reject_adds_the_drop_penalty_and_the_books_still_balance()
    {
        var settings = new SimulationSettings { Admin = new AdminBehaviour { RejectChance = 0.3, PenalizedRejectShare = 1 } };

        var seasons = SimulationRunner.Play(SimulatorFixtures.Inputs(settings: settings), 4, Days, 67);

        var rejected = seasons.SelectMany(s => s.Runs).Where(r => r.End == RunEnd.Rejected).ToList();
        Assert.NotEmpty(rejected);
        Assert.All(rejected, r => Assert.Equal(0, r.Points));

        // The frozen first loses nothing and takes no penalty (D-99); every other reject is penalized
        Assert.All(rejected.Where(r => r.PenalizedReject), r => Assert.True(r.Penalty > 0));
        Assert.True(rejected.Count(r => r.PenalizedReject) > rejected.Count / 2);
        Assert.All(seasons.SelectMany(s => s.Bots), b => Assert.True(b.PenalizedRejects <= seasons.SelectMany(s => s.Runs).Count(r => r.Bot == b.Index && r.End == RunEnd.Rejected)));
        foreach (var season in seasons)
        {
            foreach (var bot in season.Bots)
            {
                var runs = season.Runs.Where(r => r.Bot == bot.Index).ToList();
                Assert.Equal(bot.Points, runs.Sum(r => r.Points) - runs.Sum(r => r.Penalty) + bot.CellBonus + bot.FinishBonus + bot.OtherPoints);
                Assert.Equal(bot.PenalizedRejects, runs.Count(r => r.PenalizedReject));
            }
        }
    }

    [Fact]
    public void The_new_settings_are_checked()
    {
        var settings = new SimulationSettings
        {
            Behaviour = new BotBehaviour { WishRerollChance = 1.5, DropStreakLimit = -1 },
            Admin = new AdminBehaviour { PenalizedRejectShare = -0.1 },
        };

        var problems = settings.Problems();

        Assert.Contains(problems, p => p.StartsWith("behaviour.wishRerollChance", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("behaviour.dropStreakLimit", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("admin.penalizedRejectShare", StringComparison.Ordinal));
    }

    private static Ruleset WithWishTags(params string[] tags)
    {
        var rules = RulesetJson.Default();
        return rules with { Roll = rules.Roll with { WishRerollTags = [.. tags] } };
    }
}

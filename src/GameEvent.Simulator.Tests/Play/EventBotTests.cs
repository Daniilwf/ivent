using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Simulator.Cli;
using GameEvent.Simulator.Setup;
using GameEvent.Simulator.Tests.Support;

namespace GameEvent.Simulator.Tests.Play;

/// <summary>
/// Task 5.15: the bots of the simulator draw events (only «ивенты вкл/выкл», without coop, polls and the rest of stage 5,
/// docs/proposals/STAGE5_ANALYSIS.md «Баланс»). With events on and the repository's decks published into the season, a
/// completion above hard, a drop and an event cell draw through the engine as on the site (D-350), and every season still
/// reaches its result; with events off the content changes nothing. Written by <c>test-author</c> from the SPEC.
/// </summary>
public class EventBotTests
{
    private const int Days = 21;

    // Every bot plays on extreme now and then and drops sometimes: both draw (D-504)
    private static readonly SimulationSettings s_drawing = new()
    {
        Behaviour = new BotBehaviour
        {
            Difficulty = new Dictionary<string, double> { ["normal"] = 0.5, ["extreme"] = 0.5 },
            RandomDropChance = 0.2,
        },
    };

    [Fact]
    public void With_events_on_the_bots_draw_events_and_every_season_reaches_its_result()
    {
        var inputs = SimulatorFixtures.InputsWithContent(SimulatorFixtures.WithEvents(), SimulatorFixtures.RepositoryContent(), s_drawing);

        var seasons = SimulationRunner.Play(inputs, 4, Days, 71);

        Assert.Equal(4, seasons.Count);
        Assert.All(seasons, s => Assert.True(s.EventsDrawn > 0, $"Season {s.Index} drew no event."));
        Assert.All(seasons, s => Assert.Equal(0, s.GuardTrips));

        // What an event asks or leaves to do never blocks a bot: it answers, the admin bot resolves (D-603)
        string[] blocked = [RejectionCodes.ChoicePending, RejectionCodes.EffectChoicePending, RejectionCodes.WrongPhase, RejectionCodes.SeasonEffectsPending];
        Assert.All(seasons, s => Assert.DoesNotContain(s.Rejections.Keys, blocked.Contains));
    }

    [Fact]
    public void With_events_off_the_content_changes_nothing_and_nothing_is_drawn()
    {
        var without = SimulationRunner.Play(SimulatorFixtures.InputsWithContent(RulesetJson.Default(), null, s_drawing), 3, Days, 72);
        var with = SimulationRunner.Play(SimulatorFixtures.InputsWithContent(RulesetJson.Default(), SimulatorFixtures.RepositoryContent(), s_drawing), 3, Days, 72);

        Assert.All(with, s => Assert.Equal(0, s.EventsDrawn));
        Assert.Equal(without.SelectMany(s => s.Bots), with.SelectMany(s => s.Bots));
        Assert.Equal(without.Select(s => s.Commands), with.Select(s => s.Commands));
    }

    [Fact]
    public void Events_change_the_seasons()
    {
        // The draws act: a tailwind adds a die, a horror week forces the genre, a gamble moves — the seasons differ
        var off = SimulationRunner.Play(SimulatorFixtures.InputsWithContent(RulesetJson.Default(), SimulatorFixtures.RepositoryContent(), s_drawing), 3, Days, 73);
        var on = SimulationRunner.Play(SimulatorFixtures.InputsWithContent(SimulatorFixtures.WithEvents(), SimulatorFixtures.RepositoryContent(), s_drawing), 3, Days, 73);

        Assert.NotEqual(off.SelectMany(s => s.Bots.Select(b => b.Points)), on.SelectMany(s => s.Bots.Select(b => b.Points)));
    }

    [Fact]
    public void The_same_seed_draws_the_same_events()
    {
        var inputs = SimulatorFixtures.InputsWithContent(SimulatorFixtures.WithEvents(), SimulatorFixtures.RepositoryContent(), s_drawing);

        var first = SimulationRunner.Play(inputs, 3, Days, 74);
        var second = SimulationRunner.Play(inputs, 3, Days, 74, threads: 3);

        Assert.Equal(first.Select(s => s.EventsDrawn), second.Select(s => s.EventsDrawn));
        Assert.Equal(first.SelectMany(s => s.Bots), second.SelectMany(s => s.Bots));
    }

    [Fact]
    public void Events_on_without_content_are_refused_before_the_run()
    {
        // Without decks every draw would miss and leave a manual effect (D-502): not what «ивенты вкл» measures
        var refused = Assert.Throws<InvalidDataException>(() => SimulatorFixtures.InputsWithContent(SimulatorFixtures.WithEvents(), null));

        Assert.Contains("--content", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_that_does_not_load_is_refused_with_every_problem()
    {
        // A deck naming an event the pack does not have
        var content = SimulatorFixtures.RepositoryContent();
        var broken = content with
        {
            Wheels = [.. content.Wheels.Select(w => w.Deck is not null ? w with { Entries = [.. w.Entries, new Engine.Content.WheelEntrySpec { ObjectId = "no-such-event", Weight = 1 }] } : w)],
        };

        var refused = Assert.Throws<InvalidDataException>(() => SimulatorFixtures.InputsWithContent(SimulatorFixtures.WithEvents(), broken));

        Assert.Contains("no-such-event", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_content_folder_is_given_on_the_command_line()
    {
        var (given, error) = CliArguments.Parse(["--content", "content", "--runs", "10"]);

        Assert.Null(error);
        Assert.Equal("content", given!.Content);
        Assert.Contains("--content", CliArguments.Usage, StringComparison.Ordinal);
    }
}

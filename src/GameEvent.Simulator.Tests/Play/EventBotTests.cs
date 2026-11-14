using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
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

    [Fact]
    public void An_answer_on_the_final_review_that_asks_again_is_answered_until_nothing_waits()
    {
        // Given every cell of a graph map draws from the bad deck, and the bad deck's only event asks «шаг или монетки» with
        // the step first — the bots' answer (D-805). The admin bot checks nothing before the final review and rejects every
        // run there with the drop penalty, which draws the bad event for a bot done for the season: its answer steps onto
        // another event cell, which asks again, and so on
        var content = SimulatorFixtures.RepositoryContent();
        var stepFirst = ContentJson.Parse<ObjectDefinition>("""
            {
              "id": "step-or-coins", "kind": "event", "name": "Шаг или монетки", "description": "Выбери: шаг вперёд или монетку.",
              "rarity": "common", "price": null, "hostile": false,
              "effect": {
                "target": { "selector": "self" },
                "outcomes": {
                  "choice": { "prompt": "Что берёшь?", "options": { "list": ["шаг", "монетки"] } },
                  "cases": [
                    { "option": "шаг", "actions": [{ "type": "move", "steps": 1 }] },
                    { "option": "монетки", "actions": [{ "type": "changeResource", "resource": "coins", "amount": 1 }] }
                  ]
                }
              }
            }
            """);
        content = content with
        {
            Objects = [.. content.Objects.Append(stepFirst).OrderBy(o => o.Id, StringComparer.Ordinal)],
            Wheels = [.. content.Wheels.Select(w => w.Deck == EventKind.Bad ? w with { Entries = [new WheelEntrySpec { ObjectId = "step-or-coins", Weight = 1 }] } : w)],
        };
        var settings = s_drawing with
        {
            Admin = new AdminBehaviour { SkipCheckChance = 1, RejectChance = 1, PenalizedRejectShare = 1 },
        };
        var inputs = SimulationInputs.Create(
            SimulatorFixtures.WithEvents(), EventLine(40), SimulatorFixtures.DemoPool(), settings, "docs/ruleset.default.json", "map", "content/pool.demo.json", content);

        // When the seasons play out: each reaches its result, nothing left waiting for an answer
        var seasons = SimulationRunner.Play(inputs, 3, Days, 75);

        Assert.Equal(3, seasons.Count);
        Assert.All(seasons, s => Assert.True(s.EventsDrawn > 0, $"Season {s.Index} drew no event."));
    }

    [Fact]
    public void A_content_folder_that_does_not_exist_is_refused_with_its_name()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-content-{Environment.ProcessId}-{Environment.TickCount64}");

        var refused = Assert.Throws<DirectoryNotFoundException>(() => SimulationInputs.ReadContent(missing));

        // An IOException: Program prints «The inputs cannot be played» with the message and exits with 1
        Assert.IsAssignableFrom<IOException>(refused);
        Assert.Contains(missing, refused.Message, StringComparison.Ordinal);
        Assert.Throws<DirectoryNotFoundException>(() => SimulationInputs.Load(null, null, SimulatorFixtures.PathOf("content/pool.demo.json"), null, missing));
    }

    [Fact]
    public void A_missing_subfolder_of_the_content_is_empty()
    {
        // Only events: no items, no wheels, no decks
        var folder = Directory.CreateTempSubdirectory("content-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "events"));
            File.Copy(SimulatorFixtures.PathOf("content/events/tailwind.json"), Path.Combine(folder, "events", "tailwind.json"));
            Directory.CreateDirectory(Path.Combine(folder, "decks"));
            File.Copy(SimulatorFixtures.PathOf("content/decks/good.json"), Path.Combine(folder, "decks", "good.json"));

            var content = SimulationInputs.ReadContent(folder);

            Assert.Equal(["tailwind"], content.Objects.Select(o => o.Id));
            Assert.Equal(["good"], content.Wheels.Select(w => w.Id));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_repository_content_reads_every_subfolder_into_its_place()
    {
        // items and events are objects, wheels and decks are wheels (D-533)
        var content = SimulationInputs.ReadContent(SimulatorFixtures.PathOf("content"));

        string Ids(string dir) => string.Join(",", Directory.EnumerateFiles(SimulatorFixtures.PathOf(Path.Combine("content", dir)), "*.json")
            .Select(f => System.Text.Json.JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("id").GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(
            string.Join(",", Ids("items").Split(',').Concat(Ids("events").Split(',')).Order(StringComparer.Ordinal)),
            string.Join(",", content.Objects.Select(o => o.Id).Order(StringComparer.Ordinal)));
        Assert.Equal(
            string.Join(",", Ids("wheels").Split(',').Concat(Ids("decks").Split(',')).Order(StringComparer.Ordinal)),
            string.Join(",", content.Wheels.Select(w => w.Id).Order(StringComparer.Ordinal)));
        Assert.Contains(content.Wheels, w => w.Deck == EventKind.Bad);
        Assert.Contains(content.Objects, o => o.Id == "tailwind");
    }

    /// <summary>A graph line start → e1 … e{length} → finish where every cell draws from the bad deck.</summary>
    private static MapGraph EventLine(int length)
    {
        var ids = Enumerable.Range(1, length).Select(i => $"e{i}").ToList();
        var cells = new List<string> { """{ "id": "start", "type": "start", "x": 0, "y": 0 }""" };
        cells.AddRange(ids.Select((id, i) => $$"""{ "id": "{{id}}", "type": "event", "deck": "bad", "x": {{(i + 1) * 100}}, "y": 0 }"""));
        cells.Add($$"""{ "id": "finish", "type": "finish", "x": {{(length + 1) * 100}}, "y": 0 }""");
        var path = new List<string> { "start" };
        path.AddRange(ids);
        path.Add("finish");
        var edges = path.Zip(path.Skip(1), (from, to) => $$"""{ "from": "{{from}}", "to": "{{to}}", "isDefaultForward": true, "isPrimaryBackward": true }""");
        return ContentJson.Parse<MapGraph>($$"""{ "cells": [{{string.Join(",", cells)}}], "edges": [{{string.Join(",", edges)}}], "zones": [] }""");
    }
}

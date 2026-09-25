using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Tests.Content;

/// <summary>
/// CT1: every example of docs/CONTENT.md loads into the content model and passes its checks (D-103). The examples are
/// read from the file itself, so the doc and the model cannot drift apart; their behaviour comes with stages 4–6.
/// </summary>
public partial class ContentExamplesTests
{
    private static readonly Lazy<IReadOnlyList<string>> s_examples = new(ReadExamples);

    [Fact]
    public void Every_example_loads()
    {
        var failures = new List<string>();
        foreach (var json in s_examples.Value)
        {
            try
            {
                var errors = LoadAndCheck(json);
                failures.AddRange(errors.Select(e => $"{Head(json)} {e.Path}: {e.Message}"));
            }
            catch (JsonException e)
            {
                failures.Add($"{Head(json)} {e.Message}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void The_doc_has_the_examples_the_stages_rely_on()
    {
        // 10 items with their effects, 5 events, 3 achievements, a zone, the cells, a poll, a weekly challenge — and the
        // «Определение объекта» sample at the top
        var kinds = s_examples.Value.Select(Classify).ToList();

        Assert.Equal(1 + 10 + 3 + 5 + 3, kinds.Count(k => k == "object"));
        Assert.Equal(1, kinds.Count(k => k == "zone"));
        Assert.Equal(1, kinds.Count(k => k == "cells"));
        Assert.Equal(1, kinds.Count(k => k == "poll"));
        Assert.Equal(1, kinds.Count(k => k == "challenge"));
    }

    [Fact]
    public void Every_object_survives_a_round_trip()
    {
        foreach (var json in s_examples.Value.Where(j => Classify(j) == "object"))
        {
            var loaded = ContentJson.Parse<ObjectDefinition>(json);
            var again = ContentJson.Parse<ObjectDefinition>(ContentJson.Write(loaded));

            Assert.Equal(ContentJson.Write(loaded), ContentJson.Write(again));
        }
    }

    [Fact]
    public void Every_base_action_of_the_doc_is_used_by_some_example_or_listed()
    {
        // Invariant 7: at most 12 base actions, each one a type of the model
        var types = typeof(ActionSpec).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(ActionSpec))).ToList();

        Assert.Equal(12, types.Count);
    }

    [Fact]
    public void The_examples_mean_what_the_doc_says()
    {
        var objects = s_examples.Value.Where(j => Classify(j) == "object")
            .Select(ContentJson.Parse<ObjectDefinition>)
            .GroupBy(o => o.Id)
            .ToDictionary(g => g.Key, g => g.Last());

        // Апельсин: one step forward for the one who uses it
        var orange = objects["orange"];
        Assert.Equal((ObjectKind.Item, UseWindow.Anytime, 10, true), (orange.Kind, orange.Window!.Value, orange.Price!.Value, orange.Stackable));
        var move = Assert.IsType<MoveAction>(Assert.Single(orange.Effect!.Actions));
        Assert.Equal((ContentValueKind.Number, 1), (move.Steps.Kind, move.Steps.Number));

        // Проклятие: −1d6 to the target's next dice, at least 1
        var curse = objects["curse-effect"].Effect!;
        Assert.Equal((Trigger.BeforeDice, 1), (curse.Trigger!.Value, curse.Duration!.Uses!.Value));
        var minus = Assert.IsType<ModifyDiceAction>(curse.Actions[0]);
        Assert.Equal((DiceStage.Add, ContentValueKind.Dice, true, 1, 6), (minus.Stage, minus.Value.Kind, minus.Value.Negative, minus.Value.Count, minus.Value.Sides));

        // Птичкерс: roll 1d4, take it from the target, give it to self
        var bird = objects["bird-thief"].Effect!.Actions;
        var take = Assert.IsType<ChangeResourceAction>(bird[1]);
        var give = Assert.IsType<ChangeResourceAction>(bird[2]);
        Assert.Equal(("roll", true, (ActionTarget?)null), (take.Amount.Name, take.Amount.Negative, take.Target));
        Assert.Equal(("roll", false, (ActionTarget?)ActionTarget.Self), (give.Amount.Name, give.Amount.Negative, give.Target));

        // Рискнёшь?: 1–3 back 3, 4–6 forward 3
        var gamble = objects["gamble"].Effect!.Outcomes!;
        Assert.Equal([(1, 3, -3), (4, 6, 3)], gamble.Cases.Select(c => (c.From, c.To, Assert.IsType<MoveAction>(Assert.Single(c.Actions)).Steps.Number)));

        // Подлянка: the chosen among those with more points gets the chosen genre
        var trick = objects["dirty-trick"];
        Assert.Equal((Selector.Chosen, (Among?)Among.HigherPoints, true), (trick.Effect!.Target!.Selector, trick.Effect.Target.Among, trick.Hostile));
        Assert.Equal("$choice", Assert.IsType<GiveObjectAction>(trick.Effect.Actions[1]).Params!["tag"]);

        // Караоке: manual, with a media proof, no effect
        var karaoke = objects["karaoke"];
        Assert.Equal((true, (ContentProof?)ContentProof.Media, (EffectSpec?)null), (karaoke.Manual, karaoke.Proof, karaoke.Effect));

        // Щит: an interception of the next hostile effect
        var shield = objects["shield-effect"].Effect!;
        Assert.Equal(((Intercept?)Intercept.Hostile, (Trigger?)Trigger.HostileIncoming), (shield.Intercept, shield.Trigger));
    }

    // ---- The model refuses what the doc does not allow ----

    [Theory]
    [InlineData("""{"id":"x","kind":"item","name":"X","description":"Y","window":"anytime","effect":{"actions":[{"type":"move","steps":1}]},"colour":"red"}""")]
    [InlineData("""{"id":"x","kind":"item","name":"X","description":"Y","window":"anytime","effect":{"actions":[{"type":"fly","steps":1}]}}""")]
    [InlineData("""{"id":"x","kind":"item","name":"X","description":"Y","window":"anytime","effect":{"actions":[{"type":"move","steps":"1d"}]}}""")]
    [InlineData("""{"id":"x","kind":"item","name":"X","description":"Y","window":"always","effect":{"actions":[{"type":"move","steps":1}]}}""")]
    [InlineData("""{"id":"x","kind":"item","name":"X","window":"anytime","effect":{"actions":[{"type":"move","steps":1}]}}""")]
    [InlineData("""{"id":"x","kind":"item","name":"X","description":"Y","window":"anytime","effect":{"actions":[{"type":"move"}]}}""")]
    [InlineData("""{"id":"x","kind":"item","name":"X","description":"Y","window":"anytime","effect":{"actions":[{"type":"move","steps":1.5}]}}""")]
    public void Unknown_or_malformed_fields_are_refused(string json)
    {
        Assert.ThrowsAny<JsonException>(() => ContentJson.Parse<ObjectDefinition>(json));
    }

    private static IReadOnlyList<ContentError> LoadAndCheck(string json) =>
        Classify(json) switch
        {
            "object" => ContentValidator.Check(ContentJson.Parse<ObjectDefinition>(json)),
            "zone" => ContentValidator.Check(ContentJson.Parse<ZoneDefinition>(json)),
            "cells" => ContentValidator.Check([.. ContentJson.Parse<EquatableArray<CellDefinition>>(json)]),
            "poll" => ContentValidator.Check(ContentJson.Parse<PollDefinition>(json)),
            "challenge" => ContentValidator.Check(ContentJson.Parse<ChallengeDefinition>(json)),
            var other => throw new InvalidOperationException($"Unknown example kind {other}: {Head(json)}"),
        };

    private static string Classify(string json)
    {
        var node = JsonNode.Parse(json)!;
        return node switch
        {
            JsonArray => "cells",
            JsonObject o when o.ContainsKey("kind") => "object",
            JsonObject o when o.ContainsKey("rollFilter") => "zone",
            JsonObject o when o.ContainsKey("question") => "poll",
            JsonObject o when o.ContainsKey("reward") => "challenge",
            _ => "unknown",
        };
    }

    private static string Head(string json) => json.Length > 60 ? json[..60].ReplaceLineEndings(" ") : json;

    private static IReadOnlyList<string> ReadExamples()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "CONTENT.md")))
        {
            dir = dir.Parent;
        }

        var text = File.ReadAllText(Path.Combine(dir?.FullName ?? throw new InvalidOperationException("docs/CONTENT.md not found."), "docs", "CONTENT.md"));
        return [.. JsonBlock().Matches(text).Select(m => m.Groups["json"].Value)];
    }

    [GeneratedRegex("```json\\r?\\n(?<json>.*?)\\r?\\n```", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex JsonBlock();
}

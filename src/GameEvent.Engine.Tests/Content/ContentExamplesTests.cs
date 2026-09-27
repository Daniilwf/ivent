using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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

    /// <summary>The one example of CONTENT.md of <paramref name="kind"/> (<c>zone</c>, <c>cells</c>, …), for acceptance tests of other stages.</summary>
    internal static string Example(string kind) => s_examples.Value.Single(j => Classify(j) == kind);

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
        // 10 items with their effects, 5 events, 3 achievements, a zone, the cells, a map, a poll, a weekly challenge — and the
        // «Определение объекта» sample at the top
        var kinds = s_examples.Value.Select(Classify).ToList();

        Assert.Equal(1 + 10 + 3 + 5 + 3, kinds.Count(k => k == "object"));
        Assert.Equal(1, kinds.Count(k => k == "zone"));
        Assert.Equal(1, kinds.Count(k => k == "cells"));
        Assert.Equal(1, kinds.Count(k => k == "map"));
        Assert.Equal(1, kinds.Count(k => k == "poll"));
        Assert.Equal(1, kinds.Count(k => k == "challenge"));
    }

    [Fact]
    public void Every_object_survives_a_round_trip()
    {
        foreach (var json in s_examples.Value.Where(j => Classify(j) == "object"))
        {
            var loaded = ContentJson.Parse<ObjectDefinition>(json);
            var written = ContentJson.Write(loaded);

            // Equal by value after a round trip, and every field of the original is written back as it was (the engine
            // format adds explicit nulls and defaults, D-48)
            Assert.Equal(loaded, ContentJson.Parse<ObjectDefinition>(written));
            AssertCovers(JsonNode.Parse(json)!, JsonNode.Parse(written)!, "$");
        }
    }

    [Fact]
    public void Base_actions_are_the_twelve_of_the_doc_table()
    {
        // Invariant 7: at most 12 base actions; their «type» names are the ones in «Базовые действия»
        var names = typeof(ActionSpec).GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .Select(a => (string)a.TypeDiscriminator!)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(12, names.Count);
        Assert.Equal(TableColumn("## Базовые действия").Order(StringComparer.Ordinal), names);
    }

    [Theory]
    [InlineData("`kind`", typeof(ObjectKind))]
    [InlineData("`rarity`", typeof(Rarity))]
    [InlineData("`window`", typeof(UseWindow))]
    [InlineData("`trigger`", typeof(Trigger))]
    [InlineData("`duration`", null)]
    public void Every_value_the_doc_lists_parses(string field, Type? enumType)
    {
        var values = ListedValues(field);
        Assert.NotEmpty(values);
        if (enumType is null)
        {
            // «duration» lists shapes: each one parses as a duration
            foreach (var shape in values)
            {
                ContentJson.Parse<DurationSpec>(shape);
            }

            return;
        }

        foreach (var value in values)
        {
            Assert.NotNull(System.Text.Json.JsonSerializer.Deserialize(JsonValue.Create(value).ToJsonString(), enumType, ContentJson.Options));
        }

        Assert.Equal(values.Count, Enum.GetValues(enumType).Length);
    }

    [Fact]
    public void Every_selector_the_doc_lists_parses()
    {
        // «selector»: the list before «among»; «among»: its two values
        var row = TableRows("## Блок `effect`").Single(r => r.Field == "`target`").Values;
        var selectors = Backticked(row[..row.IndexOf("`among`", StringComparison.Ordinal)]).Skip(1).ToList();
        var amongPart = row[row.IndexOf("`among`", StringComparison.Ordinal)..row.IndexOf("`excludeSelf`", StringComparison.Ordinal)];
        var among = Backticked(amongPart[amongPart.LastIndexOf(':')..]).ToList();

        Assert.Equal(Enum.GetNames<TargetSelector>().Select(Camel).Order(StringComparer.Ordinal), selectors.Order(StringComparer.Ordinal));
        Assert.Equal(Enum.GetNames<Among>().Select(Camel).Order(StringComparer.Ordinal), among.Order(StringComparer.Ordinal));
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
        Assert.Equal((TargetSelector.Chosen, (Among?)Among.HigherPoints, true), (trick.Effect!.Target!.Selector, trick.Effect.Target.Among, trick.Hostile));
        Assert.Equal("$choice", Assert.IsType<GiveObjectAction>(trick.Effect.Actions[1]).Params!["tag"]);

        // Караоке: manual, with a media proof, no effect
        var karaoke = objects["karaoke"];
        Assert.Equal((true, (ContentProof?)ContentProof.Media, (EffectSpec?)null), (karaoke.Manual, karaoke.Proof, karaoke.Effect));

        // Щит: an interception of the next hostile effect
        var shield = objects["shield-effect"].Effect!;
        Assert.Equal(((Intercept?)Intercept.Hostile, (Trigger?)Trigger.HostileIncoming), (shield.Intercept, shield.Trigger));
        Assert.Equal(1, shield.Duration!.Uses);
        Assert.Equal("shield-effect", Assert.IsType<GiveObjectAction>(Assert.Single(objects["shield"].Effect!.Actions)).ObjectId);

        // Проклятие: the item gives the effect to a chosen other player; the effect ends with «not below 1»
        Assert.Equal((TargetSelector.Chosen, true), (objects["curse"].Effect!.Target!.Selector, objects["curse"].Effect!.Target!.ExcludeSelf));
        var floor = Assert.IsType<ModifyDiceAction>(curse.Actions[1]);
        Assert.Equal((DiceWhen.Current, DiceStage.Min, 1), (floor.When, floor.Stage, floor.Value.Number));

        // Счастливый кубик: +1d6 to the next dice; Переброс: reroll the current dice once
        var lucky = Assert.IsType<ModifyDiceAction>(Assert.Single(objects["lucky-die"].Effect!.Actions));
        Assert.Equal((UseWindow.BeforeDice, DiceWhen.Next, DiceStage.Add, 1, 6), (objects["lucky-die"].Window!.Value, lucky.When, lucky.Stage, lucky.Value.Count, lucky.Value.Sides));
        var reroll = Assert.IsType<ModifyDiceAction>(Assert.Single(objects["reroll-dice"].Effect!.Actions));
        Assert.Equal((UseWindow.AfterDice, DiceWhen.Current, DiceStage.Reroll, 1), (objects["reroll-dice"].Window!.Value, reroll.When, reroll.Stage, reroll.Value.Number));

        // Купон реролла: +1 free reroll; Выбор из трёх: a choice of 3; Короткая игра: under 10 hours
        var coupon = Assert.IsType<ChangeResourceAction>(Assert.Single(objects["reroll-coupon"].Effect!.Actions));
        Assert.Equal(("freeRerolls", 1), (coupon.Resource, coupon.Amount.Number));
        Assert.Equal(3, Assert.IsType<ModifyNextRollAction>(Assert.Single(objects["pick-of-three"].Effect!.Actions)).ChoiceCount);
        Assert.Equal(10m, Assert.IsType<ModifyNextRollAction>(Assert.Single(objects["short-game"].Effect!.Actions)).Filter!.MaxHours);

        // Навязанный жанр: before the next roll, once, the genre from its parameter; Хоррор-неделя gives it with «Horror»
        var forced = objects["forced-genre"];
        Assert.Equal((ObjectKind.SpecialRoll, (Trigger?)Trigger.BeforeRoll, 1, true), (forced.Kind, forced.Effect!.Trigger, forced.Effect.Duration!.Uses!.Value, forced.Hostile));
        Assert.Equal(["$tag"], Assert.IsType<ModifyNextRollAction>(Assert.Single(forced.Effect.Actions)).Filter!.Tags!.Value);
        Assert.Equal("Horror", Assert.IsType<GiveObjectAction>(Assert.Single(objects["horror-week"].Effect!.Actions)).Params!["tag"]);

        // Попутный ветер: one more die next time; Даже не вспотел: the next run on hard or above
        var tailwind = Assert.IsType<ModifyDiceAction>(Assert.Single(objects["tailwind"].Effect!.Actions));
        Assert.Equal((DiceWhen.Next, DiceStage.Count, 1), (tailwind.When, tailwind.Stage, tailwind.Value.Number));
        Assert.Equal(
            Engine.Runs.Difficulty.Hard,
            Assert.IsType<ModifyNextRollAction>(Assert.Single(objects["not-even-sweating"].Effect!.Actions)).RunCondition!.DifficultyAtLeast);

        // Achievements: their scope, trigger, condition and 5 or 10 coins
        var marathon = objects["horror-marathon"];
        Assert.Equal(
            (AchievementScope.Season, (Trigger?)Trigger.RunCompleted, (ContentStat?)ContentStat.CompletedStreakWithTag, "Horror", 3, 5),
            (marathon.Scope!.Value, marathon.Effect!.Trigger, marathon.Effect.Condition!.Stat, marathon.Effect.Condition.Tag, marathon.Effect.Condition.Gte!.Value,
             Assert.IsType<ChangeResourceAction>(Assert.Single(marathon.Effect.Actions)).Amount.Number));
        var jackpot = objects["jackpot"];
        Assert.Equal(
            (AchievementScope.AllTime, (Trigger?)Trigger.AfterDice, (ContentStat?)ContentStat.AllDiceMax, 3, 10),
            (jackpot.Scope!.Value, jackpot.Effect!.Trigger, jackpot.Effect.Condition!.Stat, jackpot.Effect.Condition.MinDice!.Value,
             Assert.IsType<ChangeResourceAction>(Assert.Single(jackpot.Effect.Actions)).Amount.Number));
        var unbreakable = objects["unbreakable"].Effect!;
        Assert.Equal(((Trigger?)Trigger.HostileIncoming, (ContentStat?)ContentStat.HostileReceived, 5), (unbreakable.Trigger, unbreakable.Condition!.Stat, unbreakable.Condition.Gte!.Value));
    }

    [Fact]
    public void The_zone_cells_poll_and_challenge_mean_what_the_doc_says()
    {
        var zone = ContentJson.Parse<ZoneDefinition>(s_examples.Value.Single(j => Classify(j) == "zone"));
        Assert.Equal(("horror-swamp", "Horror", DiceStage.Add, 1, 1.5m, "horror-bad", 1.0m),
            (zone.Id, zone.RollFilter!.Tags!.Value.Single(), zone.DiceModifier!.Stage, zone.DiceModifier.Value.Number, zone.DropPenaltyMultiplier!.Value, zone.Deck, zone.ShopPriceMultiplier!.Value));

        var cells = ContentJson.Parse<EquatableArray<CellDefinition>>(s_examples.Value.Single(j => Classify(j) == "cells"));
        Assert.Equal(
            [("c12", ContentCellType.Shop, "shop-coupon"), ("c17", ContentCellType.Event, "zone"), ("c23", ContentCellType.Teleport, "c41"), ("c30", ContentCellType.PointsBonus, "3"), ("c40", ContentCellType.Checkpoint, "")],
            cells.Select(c => (c.Id, c.Type, c.Grants ?? c.Deck ?? c.To ?? c.Amount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "")));

        var poll = ContentJson.Parse<PollDefinition>(s_examples.Value.Single(j => Classify(j) == "poll"));
        Assert.Equal((ChoiceSource.Categories, PollVoters.Players, true, 24, TargetSelector.Leader),
            (poll.Options.From!.Value, poll.Voters, poll.Anonymous, poll.ClosesInHours, poll.OnResult!.Target!.Selector));
        Assert.Equal("$result", Assert.IsType<GiveObjectAction>(Assert.Single(poll.OnResult.Actions)).Params!["tag"]);

        var challenge = ContentJson.Parse<WeeklyChallengeDefinition>(s_examples.Value.Single(j => Classify(j) == "challenge"));
        Assert.Equal(("old-school", 2005, 10, ChallengeCheck.Auto),
            (challenge.Id, challenge.Condition.Game!.ReleaseYearBefore!.Value, challenge.Reward.Coins!.Value, challenge.Check));
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
            "map" => MapChecked(json),
            "poll" => ContentValidator.Check(ContentJson.Parse<PollDefinition>(json)),
            "challenge" => ContentValidator.Check(ContentJson.Parse<WeeklyChallengeDefinition>(json)),
            var other => throw new InvalidOperationException($"Unknown example kind {other}: {Head(json)}"),
        };

    // Stage 2 (D-300): the map example is checked like a published map in the graph mode
    private static IReadOnlyList<ContentError> MapChecked(string json)
    {
        var rules = Support.TestRuleset.Create() with { Features = Support.TestRuleset.Create().Features with { MapMode = Engine.Rulesets.MapMode.Graph } };
        return [.. Engine.Map.MapValidator.Validate(ContentJson.Parse<Engine.Map.MapGraph>(json), rules).Select(e => new ContentError(e.Subject, $"{e.Code}: {e.Message}"))];
    }

    private static string Classify(string json)
    {
        var node = JsonNode.Parse(json)!;
        return node switch
        {
            JsonArray => "cells",
            JsonObject o when o.ContainsKey("edges") => "map",
            JsonObject o when o.ContainsKey("kind") => "object",
            JsonObject o when o.ContainsKey("rollFilter") => "zone",
            JsonObject o when o.ContainsKey("question") => "poll",
            JsonObject o when o.ContainsKey("reward") => "challenge",
            _ => "unknown",
        };
    }

    // Every field of the original JSON is in the written one with the same value.
    private static void AssertCovers(JsonNode original, JsonNode written, string path)
    {
        switch (original)
        {
            case JsonObject o:
                foreach (var (name, value) in o)
                {
                    var there = Assert.IsType<JsonObject>(written)[name];
                    Assert.True(value is null ? there is null : there is not null, $"{path}.{name} was lost.");
                    if (value is not null)
                    {
                        AssertCovers(value, there!, $"{path}.{name}");
                    }
                }

                break;
            case JsonArray a:
                var w = Assert.IsType<JsonArray>(written);
                Assert.Equal(a.Count, w.Count);
                for (var i = 0; i < a.Count; i++)
                {
                    AssertCovers(a[i]!, w[i]!, $"{path}[{i}]");
                }

                break;
            default:
                Assert.True(JsonNode.DeepEquals(original, written), $"{path}: {original.ToJsonString()} became {written.ToJsonString()}.");
                break;
        }
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static IEnumerable<string> Backticked(string text) =>
        Backtick().Matches(text).Select(m => m.Groups["word"].Value);

    // The first column of a doc table under a heading: the base action names
    private static IEnumerable<string> TableColumn(string heading) =>
        TableRows(heading).Select(r => r.Field.Trim('`'));

    // The values a field's row lists: `a`, `b` or `{ … }` shapes
    private static List<string> ListedValues(string field)
    {
        var row = TableRows("## Определение объекта").Concat(TableRows("## Блок `effect`")).Single(r => r.Field == field).Values;
        return field == "`duration`"
            ? [.. Backticked(row).Where(v => v.StartsWith('{')).Select(v => v.Replace("N", "2", StringComparison.Ordinal))]
            : [.. Backticked(row)];
    }

    private static IEnumerable<(string Field, string Values)> TableRows(string heading)
    {
        var text = s_docText.Value;
        var start = text.IndexOf(heading + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, heading);
        var end = text.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        var section = text[start..(end < 0 ? text.Length : end)];
        var lines = section.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            // A header row is the one right above «| --- |»
            var isHeader = i + 1 < lines.Length && lines[i + 1].StartsWith("| ---", StringComparison.Ordinal);
            if (lines[i].StartsWith("| `", StringComparison.Ordinal) && !isHeader)
            {
                var cells = lines[i].Split('|');
                yield return (cells[1].Trim(), cells[2].Trim());
            }
        }
    }

    private static readonly Lazy<string> s_docText = new(() => File.ReadAllText(DocPath()).ReplaceLineEndings("\n"));

    [GeneratedRegex("`(?<word>[^`]+)`", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Backtick();

    private static string Head(string json) => json.Length > 60 ? json[..60].ReplaceLineEndings(" ") : json;

    private static string DocPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "CONTENT.md")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("docs/CONTENT.md not found."), "docs", "CONTENT.md");
    }

    private static IReadOnlyList<string> ReadExamples() =>
        [.. JsonBlock().Matches(File.ReadAllText(DocPath())).Select(m => m.Groups["json"].Value)];

    [GeneratedRegex("```json\\r?\\n(?<json>.*?)\\r?\\n```", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex JsonBlock();
}

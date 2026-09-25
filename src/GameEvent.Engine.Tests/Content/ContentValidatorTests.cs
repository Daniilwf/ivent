using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Tests.Content;

/// <summary>
/// The rules of content beyond its types (CT1, D-103): what each kind needs, what an effect may combine, and that values
/// and references make sense where they are used. Every case names the field the error points at.
/// </summary>
public class ContentValidatorTests
{
    private const string Orange = """
        {"id":"orange","kind":"item","name":"Апельсин","description":"Иди на 1 клетку вперёд.","rarity":"common","price":10,
         "window":"anytime","hostile":false,"stackable":true,
         "effect":{"target":{"selector":"self"},"actions":[{"type":"move","steps":1}]}}
        """;

    private static IReadOnlyList<ContentError> Errors(string json) => ContentValidator.Check(ContentJson.Parse<ObjectDefinition>(json));

    private static void AssertError(string json, string path)
    {
        var errors = Errors(json);
        Assert.Contains(errors, e => e.Path == path);
    }

    [Fact]
    public void A_valid_item_has_no_errors()
    {
        Assert.Empty(Errors(Orange));
    }

    [Theory]
    [InlineData("Orange")]
    [InlineData("orange_juice")]
    [InlineData("-orange")]
    [InlineData("апельсин")]
    public void Ids_are_lowercase_words_with_hyphens(string id)
    {
        AssertError(Orange.Replace("\"id\":\"orange\"", $"\"id\":\"{id}\"", StringComparison.Ordinal), "$.id");
    }

    [Fact]
    public void An_id_longer_than_the_limit_is_an_error()
    {
        AssertError(Orange.Replace("\"id\":\"orange\"", $"\"id\":\"{new string('a', ContentValidator.MaxIdLength + 1)}\"", StringComparison.Ordinal), "$.id");
    }

    [Fact]
    public void A_blank_name_is_an_error()
    {
        AssertError(Orange.Replace("\"name\":\"Апельсин\"", "\"name\":\" \"", StringComparison.Ordinal), "$.name");
    }

    [Fact]
    public void A_negative_price_is_an_error()
    {
        AssertError(Orange.Replace("\"price\":10", "\"price\":-1", StringComparison.Ordinal), "$.price");
    }

    [Fact]
    public void An_item_needs_a_window()
    {
        AssertError(Orange.Replace("\"window\":\"anytime\",", "", StringComparison.Ordinal), "$.window");
    }

    [Fact]
    public void Only_items_have_a_window()
    {
        AssertError(Orange.Replace("\"kind\":\"item\"", "\"kind\":\"event\"", StringComparison.Ordinal), "$.window");
    }

    [Fact]
    public void An_achievement_needs_a_scope()
    {
        const string Json = """
            {"id":"a","kind":"achievement","name":"A","description":"B",
             "effect":{"trigger":"runCompleted","actions":[{"type":"changeResource","resource":"coins","amount":5}]}}
            """;
        AssertError(Json, "$.scope");
    }

    [Fact]
    public void Only_achievements_have_a_scope()
    {
        AssertError(Orange.Replace("\"hostile\":false", "\"hostile\":false,\"scope\":\"season\"", StringComparison.Ordinal), "$.scope");
    }

    [Fact]
    public void An_object_without_an_effect_must_be_manual()
    {
        const string Json = """{"id":"a","kind":"event","name":"A","description":"B"}""";
        AssertError(Json, "$.effect");
        Assert.Empty(Errors("""{"id":"a","kind":"event","name":"A","description":"B","manual":true}"""));
    }

    [Theory]
    [InlineData("effect")]
    [InlineData("specialRoll")]
    public void Effects_and_special_rolls_need_a_trigger(string kind)
    {
        var json = $$$"""{"id":"a","kind":"{{{kind}}}","name":"A","description":"B","effect":{"actions":[{"type":"move","steps":1}]}}""";
        AssertError(json, "$.effect.trigger");
    }

    [Fact]
    public void Items_act_without_a_trigger()
    {
        AssertError(Orange.Replace("\"target\":{\"selector\":\"self\"}", "\"trigger\":\"beforeRoll\"", StringComparison.Ordinal), "$.effect.trigger");
    }

    [Fact]
    public void An_interception_answers_hostile_incoming()
    {
        const string Json = """
            {"id":"a","kind":"effect","name":"A","description":"B","effect":{"trigger":"beforeRoll","intercept":"hostile","actions":[]}}
            """;
        AssertError(Json, "$.effect.intercept");
    }

    [Fact]
    public void An_effect_with_nothing_to_do_is_an_error()
    {
        AssertError(Orange.Replace("\"actions\":[{\"type\":\"move\",\"steps\":1}]", "\"actions\":[]", StringComparison.Ordinal), "$.effect");
    }

    [Fact]
    public void Actions_and_outcomes_do_not_mix()
    {
        var json = Orange.Replace(
            "\"actions\":[{\"type\":\"move\",\"steps\":1}]",
            "\"actions\":[{\"type\":\"move\",\"steps\":1}],\"outcomes\":{\"roll\":\"1d2\",\"cases\":[{\"from\":1,\"to\":2,\"actions\":[]}]}",
            StringComparison.Ordinal);
        AssertError(json, "$.effect");
    }

    [Fact]
    public void Among_narrows_only_chosen_and_random_active()
    {
        AssertError(Orange.Replace("{\"selector\":\"self\"}", "{\"selector\":\"all\",\"among\":\"higherPoints\"}", StringComparison.Ordinal), "$.effect.target.among");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"uses\":1,\"hours\":2}")]
    [InlineData("{\"uses\":0}")]
    [InlineData("{\"untilTriggered\":false}")]
    public void A_duration_is_exactly_one_positive_field(string duration)
    {
        var json = $$$"""{"id":"a","kind":"effect","name":"A","description":"B","effect":{"trigger":"beforeRoll","duration":{{{duration}}},"actions":[{"type":"move","steps":1}]}}""";
        AssertError(json, "$.effect.duration");
    }

    [Fact]
    public void Roll_reference_needs_a_roll_before_it()
    {
        AssertError(Orange.Replace("\"steps\":1", "\"steps\":\"$roll\"", StringComparison.Ordinal), "$.effect.actions[0].steps");
    }

    [Fact]
    public void Roll_reference_after_a_roll_is_fine()
    {
        var json = Orange.Replace(
            "[{\"type\":\"move\",\"steps\":1}]",
            "[{\"type\":\"roll\",\"dice\":\"1d4\"},{\"type\":\"move\",\"steps\":\"-$roll\"}]",
            StringComparison.Ordinal);
        Assert.Empty(Errors(json));
    }

    [Fact]
    public void Choice_reference_needs_a_request_before_it()
    {
        var json = Orange.Replace(
            "[{\"type\":\"move\",\"steps\":1}]",
            "[{\"type\":\"giveObject\",\"objectId\":\"forced-genre\",\"params\":{\"tag\":\"$choice\"}}]",
            StringComparison.Ordinal);
        AssertError(json, "$.effect.actions[0].params.tag");
    }

    [Fact]
    public void A_roll_needs_dice()
    {
        AssertError(Orange.Replace("[{\"type\":\"move\",\"steps\":1}]", "[{\"type\":\"roll\",\"dice\":3}]", StringComparison.Ordinal), "$.effect.actions[0].dice");
    }

    [Theory]
    [InlineData("count", "\"1d6\"")]
    [InlineData("min", "\"$roll\"")]
    [InlineData("multiply", "0")]
    [InlineData("sides", "0")]
    public void Dice_stages_other_than_add_take_numbers(string stage, string value)
    {
        var json = Orange.Replace(
            "[{\"type\":\"move\",\"steps\":1}]",
            $"[{{\"type\":\"roll\",\"dice\":\"1d4\"}},{{\"type\":\"modifyDice\",\"when\":\"next\",\"stage\":\"{stage}\",\"value\":{value}}}]",
            StringComparison.Ordinal);
        AssertError(json, "$.effect.actions[1].value");
    }

    [Fact]
    public void Modify_next_roll_changes_something()
    {
        AssertError(Orange.Replace("[{\"type\":\"move\",\"steps\":1}]", "[{\"type\":\"modifyNextRoll\"}]", StringComparison.Ordinal), "$.effect.actions[0]");
    }

    [Fact]
    public void A_choice_of_games_is_among_at_least_two()
    {
        AssertError(Orange.Replace("[{\"type\":\"move\",\"steps\":1}]", "[{\"type\":\"modifyNextRoll\",\"choiceCount\":1}]", StringComparison.Ordinal), "$.effect.actions[0].choiceCount");
    }

    [Theory]
    [InlineData("{\"type\":\"transformObject\",\"mode\":\"transform\"}", "$.effect.actions[0].into")]
    [InlineData("{\"type\":\"transformObject\",\"mode\":\"annotate\"}", "$.effect.actions[0].note")]
    [InlineData("{\"type\":\"drawEvent\",\"deck\":\" \"}", "$.effect.actions[0].deck")]
    [InlineData("{\"type\":\"spinWheel\",\"wheel\":\"\"}", "$.effect.actions[0].wheel")]
    [InlineData("{\"type\":\"teleport\",\"cell\":\"\"}", "$.effect.actions[0].cell")]
    [InlineData("{\"type\":\"changeResource\",\"resource\":\" \",\"amount\":1}", "$.effect.actions[0].resource")]
    [InlineData("{\"type\":\"giveObject\",\"objectId\":\"Bad Id\"}", "$.effect.actions[0].objectId")]
    [InlineData("{\"type\":\"requestChoice\",\"prompt\":\"?\",\"options\":{}}", "$.effect.actions[0].options")]
    [InlineData("{\"type\":\"requestChoice\",\"prompt\":\"?\",\"options\":{\"from\":\"players\",\"list\":[\"a\"]}}", "$.effect.actions[0].options")]
    public void Actions_need_their_parameters(string action, string path)
    {
        AssertError(Orange.Replace("{\"type\":\"move\",\"steps\":1}", action, StringComparison.Ordinal), path);
    }

    [Theory]
    [InlineData("\"3\"", "$.effect.outcomes.roll")]
    [InlineData("\"-1d6\"", "$.effect.outcomes.roll")]
    public void Outcomes_are_picked_by_dice(string roll, string path)
    {
        var json = Orange.Replace(
            "\"actions\":[{\"type\":\"move\",\"steps\":1}]",
            $"\"outcomes\":{{\"roll\":{roll},\"cases\":[{{\"from\":1,\"to\":6,\"actions\":[]}}]}}",
            StringComparison.Ordinal);
        if (roll == "\"3\"")
        {
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => Errors(json));
            return;
        }

        AssertError(json, path);
    }

    [Theory]
    [InlineData("[{\"from\":1,\"to\":3,\"actions\":[]},{\"from\":3,\"to\":6,\"actions\":[]}]", "$.effect.outcomes.cases[1]")]
    [InlineData("[{\"from\":1,\"to\":3,\"actions\":[]},{\"from\":5,\"to\":6,\"actions\":[]}]", "$.effect.outcomes.cases")]
    [InlineData("[{\"from\":0,\"to\":6,\"actions\":[]}]", "$.effect.outcomes.cases[0]")]
    [InlineData("[{\"from\":4,\"to\":2,\"actions\":[]}]", "$.effect.outcomes.cases[0]")]
    public void Cases_cover_every_result_once(string cases, string path)
    {
        var json = Orange.Replace(
            "\"actions\":[{\"type\":\"move\",\"steps\":1}]",
            $"\"outcomes\":{{\"roll\":\"1d6\",\"cases\":{cases}}}",
            StringComparison.Ordinal);
        AssertError(json, path);
    }

    [Fact]
    public void Roll_reference_inside_a_case_is_the_outcome_roll()
    {
        var json = Orange.Replace(
            "\"actions\":[{\"type\":\"move\",\"steps\":1}]",
            "\"outcomes\":{\"roll\":\"2d2\",\"cases\":[{\"from\":2,\"to\":4,\"actions\":[{\"type\":\"move\",\"steps\":\"$roll\"}]}]}",
            StringComparison.Ordinal);
        Assert.Empty(Errors(json));
    }

    // ---- Zones, cells, polls, challenges ----

    [Fact]
    public void Cells_need_what_their_type_does()
    {
        CellDefinition[] cells =
        [
            new() { Id = "c1", Type = ContentCellType.Shop },
            new() { Id = "c2", Type = ContentCellType.Event },
            new() { Id = "c3", Type = ContentCellType.Teleport },
            new() { Id = "c4", Type = ContentCellType.PointsBonus },
            new() { Id = "c5", Type = ContentCellType.Checkpoint },
        ];

        var errors = ContentValidator.Check(cells);

        Assert.Equal(["$[0].grants", "$[1].deck", "$[2].to", "$[3].amount"], errors.Select(e => e.Path));
    }

    [Fact]
    public void A_cell_is_listed_once()
    {
        CellDefinition[] cells = [new() { Id = "c1", Type = ContentCellType.Checkpoint }, new() { Id = "c1", Type = ContentCellType.Checkpoint }];

        Assert.Contains(ContentValidator.Check(cells), e => e.Path == "$");
    }

    [Fact]
    public void A_zone_multiplier_is_above_zero()
    {
        var zone = new ZoneDefinition { Id = "swamp", Name = "Болото", DropPenaltyMultiplier = 0 };

        Assert.Contains(ContentValidator.Check(zone), e => e.Path == "$");
    }

    [Fact]
    public void A_zone_dice_modifier_follows_the_stage_rules()
    {
        var zone = new ZoneDefinition
        {
            Id = "swamp",
            Name = "Болото",
            DiceModifier = new DiceModifierSpec { Stage = DiceStage.Min, Value = ContentValue.TryParse("1d6")! },
        };

        Assert.Contains(ContentValidator.Check(zone), e => e.Path == "$.diceModifier.value");
    }

    [Fact]
    public void A_poll_closes_after_some_hours_and_checks_its_result()
    {
        var poll = new PollDefinition
        {
            Question = "Какой жанр?",
            Options = new ChoiceOptionsSpec { From = ChoiceSource.Categories },
            Voters = PollVoters.Players,
            ClosesInHours = 0,
            OnResult = new EffectSpec(),
        };

        var paths = ContentValidator.Check(poll).Select(e => e.Path).ToList();

        Assert.Contains("$.closesInHours", paths);
        Assert.Contains("$.onResult", paths);
    }

    [Fact]
    public void A_challenge_pays_coins()
    {
        var challenge = new ChallengeDefinition
        {
            Id = "old-school",
            Name = "Олдскул",
            Description = "Пройди старую игру.",
            Condition = new ConditionSpec(),
            Reward = new RewardSpec(),
            Check = ChallengeCheck.Auto,
        };

        Assert.Contains(ContentValidator.Check(challenge), e => e.Path == "$.reward.coins");
    }

    // ---- Values ----

    [Theory]
    [InlineData("1d6", ContentValueKind.Dice, 1, 6, false, null)]
    [InlineData("d20", ContentValueKind.Dice, 1, 20, false, null)]
    [InlineData("-2d4", ContentValueKind.Dice, 2, 4, true, null)]
    [InlineData("$roll", ContentValueKind.Reference, 0, 0, false, "roll")]
    [InlineData("-$roll", ContentValueKind.Reference, 0, 0, true, "roll")]
    [InlineData("$tag", ContentValueKind.Reference, 0, 0, false, "tag")]
    public void Values_are_dice_or_references(string text, ContentValueKind kind, int count, int sides, bool negative, string? name)
    {
        var value = ContentValue.TryParse(text)!;

        Assert.Equal((kind, count, sides, negative, name, text), (value.Kind, value.Count, value.Sides, value.Negative, value.Name, value.Text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("6")]
    [InlineData("1d1")]
    [InlineData("0d6")]
    [InlineData("21d6")]
    [InlineData("1d101")]
    [InlineData("1d6+1")]
    [InlineData("$")]
    [InlineData("$1roll")]
    [InlineData("roll")]
    [InlineData("--1d6")]
    public void Anything_else_is_not_a_value(string text)
    {
        Assert.Null(ContentValue.TryParse(text));
    }

    [Fact]
    public void Values_round_trip_as_written()
    {
        var json = """[{"type":"move","steps":-3},{"type":"move","steps":"-1d6"},{"type":"move","steps":"$roll"}]""";

        var actions = ContentJson.Parse<EquatableArray<ActionSpec>>(json);
        var written = ContentJson.Write(actions);

        // Numbers stay numbers, dice and references stay the strings they were (the engine format writes nulls, D-48)
        Assert.Contains("\"steps\":-3", written, StringComparison.Ordinal);
        Assert.Contains("\"steps\":\"-1d6\"", written, StringComparison.Ordinal);
        Assert.Contains("\"steps\":\"$roll\"", written, StringComparison.Ordinal);
        Assert.Equal(actions, ContentJson.Parse<EquatableArray<ActionSpec>>(written));
    }
}

using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Tests.Kernel;

/// <summary>
/// The one JSON format of the engine (D-48): camelCase names, enums as camelCase strings and never numbers, nulls
/// written, Cyrillic readable, HTML-sensitive characters escaped; fixed once built. Found by mutation testing (D-282).
/// </summary>
public class EngineJsonTests
{
    private sealed record Sample(string PlayerName, SeasonStatus Status, string? Comment);

    [Fact]
    public void Names_are_camel_case_enums_are_strings_nulls_are_written()
    {
        Assert.Equal(
            """{"playerName":"Вася \u003Cb\u003E","status":"active","comment":null}""",
            JsonSerializer.Serialize(new Sample("Вася <b>", SeasonStatus.Active, null), EngineJson.Options));
    }

    [Fact]
    public void An_enum_as_a_number_is_refused()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Sample>("""{"playerName":"Вася","status":1,"comment":null}""", EngineJson.Options));
    }

    [Fact]
    public void A_null_where_the_type_says_not_null_is_refused()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Sample>("""{"playerName":null,"status":"active","comment":null}""", EngineJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Sample>("""{"status":"active","comment":null}""", EngineJson.Options));
    }

    [Fact]
    public void The_formats_are_fixed()
    {
        Assert.True(EngineJson.Options.IsReadOnly);
        Assert.True(RulesetJson.Options.IsReadOnly);
        Assert.True(Engine.Content.ContentJson.Options.IsReadOnly);
    }

    [Fact]
    public void Ruleset_errors_name_the_field_without_engine_type_names()
    {
        Assert.Equal("Ruleset JSON is null.", Assert.Throws<JsonException>(() => RulesetJson.Parse("null")).Message);

        var json = RulesetJson.DefaultJson().Replace("\"tiebreakers\": [", "\"tiebreakers\": [7, ", StringComparison.Ordinal);
        var error = Assert.ThrowsAny<JsonException>(() => RulesetJson.Parse(json));

        Assert.DoesNotContain("GameEvent.Engine.", error.Message, StringComparison.Ordinal);
        Assert.Contains("tiebreakers", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lists_hash_by_their_items()
    {
        Assert.NotEqual(new EquatableArray<int>([1, 2, 3]).GetHashCode(), new EquatableArray<int>([4, 5, 6]).GetHashCode());
    }

    [Fact]
    public void The_list_converter_is_for_lists_only()
    {
        var factory = new EquatableArrayJsonConverterFactory();

        Assert.True(factory.CanConvert(typeof(EquatableArray<int>)));
        Assert.False(factory.CanConvert(typeof(int)));
        Assert.False(factory.CanConvert(typeof(List<int>)));
    }
}

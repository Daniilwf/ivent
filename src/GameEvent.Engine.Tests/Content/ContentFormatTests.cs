using System.Text.Json;
using GameEvent.Engine.Content;

namespace GameEvent.Engine.Tests.Content;

/// <summary>
/// Reading content (CONTENT.md, D-103): strict JSON that names the offending field, values that are numbers, dice or
/// references within their limits, parameters that are text. Found by mutation testing (D-282).
/// </summary>
public class ContentFormatTests
{
    private const string Orange = """
        {"id":"orange","kind":"item","name":"Апельсин","description":"Иди на 1 клетку вперёд.","rarity":"common","price":10,
         "window":"anytime","hostile":false,"stackable":true,
         "effect":{"target":{"selector":"self"},"actions":[{"type":"move","steps":1}]}}
        """;

    private static JsonException ParseError(string json) =>
        Assert.ThrowsAny<JsonException>(() => ContentJson.Parse<ObjectDefinition>(json));

    [Fact]
    public void A_misspelt_field_names_its_path()
    {
        var error = ParseError(Orange.Replace("\"hostile\"", "\"hostlie\"", StringComparison.Ordinal));

        Assert.StartsWith("$", error.Message, StringComparison.Ordinal);
        Assert.Contains("hostlie", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_given_twice_is_an_error()
    {
        ParseError(Orange.Replace("\"price\":10", "\"price\":10,\"price\":20", StringComparison.Ordinal));
    }

    [Fact]
    public void The_type_of_an_action_may_come_after_its_fields()
    {
        var parsed = ContentJson.Parse<ObjectDefinition>(Orange.Replace("{\"type\":\"move\",\"steps\":1}", "{\"steps\":2,\"type\":\"move\"}", StringComparison.Ordinal));

        Assert.Equal(2, Assert.IsType<MoveAction>(Assert.Single(parsed.Effect!.Actions)).Steps.Number);
    }

    [Fact]
    public void Null_content_and_an_action_without_a_type_say_so()
    {
        Assert.Equal("Content JSON is null.", Assert.ThrowsAny<JsonException>(() => ContentJson.Parse<ObjectDefinition>("null")).Message);
        Assert.StartsWith(
            "An action needs «type»",
            ParseError(Orange.Replace("{\"type\":\"move\",\"steps\":1}", "{\"steps\":1}", StringComparison.Ordinal)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_is_not_a_value_says_what_a_value_is()
    {
        Assert.Contains("«x» is neither dice", ParseError(Orange.Replace("\"steps\":1", "\"steps\":\"x\"", StringComparison.Ordinal)).Message, StringComparison.Ordinal);
        Assert.Contains("A value is a whole number", ParseError(Orange.Replace("\"steps\":1", "\"steps\":true", StringComparison.Ordinal)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parameter_is_text()
    {
        var give = Orange.Replace("{\"type\":\"move\",\"steps\":1}", "{\"type\":\"giveObject\",\"objectId\":\"forced-genre\",\"params\":PARAMS}", StringComparison.Ordinal);

        Assert.Contains("Parameter «tag» is text", ParseError(give.Replace("PARAMS", "{\"tag\":null}", StringComparison.Ordinal)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void A_number_is_negative_only_below_zero(int number, bool negative)
    {
        Assert.Equal(negative, ContentValue.Of(number).Negative);
    }

    [Fact]
    public void Dice_go_up_to_their_limits()
    {
        Assert.Equal((ContentValue.MaxDice, ContentValue.MaxSides), (ContentValue.TryParse($"{ContentValue.MaxDice}d{ContentValue.MaxSides}")!.Count, ContentValue.TryParse($"{ContentValue.MaxDice}d{ContentValue.MaxSides}")!.Sides));
        Assert.Null(ContentValue.TryParse($"{ContentValue.MaxDice + 1}d6"));
        Assert.Null(ContentValue.TryParse($"1d{ContentValue.MaxSides + 1}"));
        Assert.Throws<ArgumentNullException>(() => ContentValue.TryParse(null!));
    }

    [Fact]
    public void Parameters_compare_by_content_and_never_equal_nothing()
    {
        var a = new ContentParamDictionary([new("tag", "Horror")]);

        Assert.True(a.Equals(new ContentParamDictionary([new("tag", "Horror")])));
        Assert.False(a.Equals(new ContentParamDictionary([new("tag", "RPG")])));
        Assert.False(a.Equals((ContentParamDictionary?)null));
        Assert.False(a.Equals((object?)"tag"));
    }

    [Fact]
    public void Writers_refuse_missing_input()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        Assert.Throws<ArgumentNullException>(() => new ContentValueConverter().Write(null!, ContentValue.Of(1), ContentJson.Options));
        Assert.Throws<ArgumentNullException>(() => new ContentValueConverter().Write(writer, null!, ContentJson.Options));
        Assert.Throws<ArgumentNullException>(() => new ContentParamDictionaryConverter().Write(null!, new ContentParamDictionary([]), ContentJson.Options));
        Assert.Throws<ArgumentNullException>(() => new ContentParamDictionaryConverter().Write(writer, null!, ContentJson.Options));
    }
}

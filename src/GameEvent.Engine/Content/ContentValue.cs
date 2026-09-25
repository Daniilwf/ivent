using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GameEvent.Engine.Content;

/// <summary>What a <see cref="ContentValue"/> holds (CONTENT.md «Значения»).</summary>
public enum ContentValueKind
{
    /// <summary>A whole number: <c>3</c>, <c>-1</c>.</summary>
    Number,

    /// <summary>Dice: <c>"1d6"</c>, <c>"2d4"</c>, <c>"-1d6"</c> (a missing count is 1).</summary>
    Dice,

    /// <summary>A reference: <c>"$roll"</c>, <c>"$choice"</c>, <c>"$result"</c> or a parameter <c>"$tag"</c>; <c>"-$roll"</c> negated.</summary>
    Reference,
}

/// <summary>
/// A value in an action (CONTENT.md «Значения»): a number, dice or a reference. Written back as it was read, so
/// content survives a round trip unchanged.
/// </summary>
[JsonConverter(typeof(ContentValueConverter))]
public sealed partial record ContentValue
{
    private ContentValue(ContentValueKind kind, string text, int number, int count, int sides, string? name, bool negative)
    {
        Kind = kind;
        Text = text;
        Number = number;
        Count = count;
        Sides = sides;
        Name = name;
        Negative = negative;
    }

    public ContentValueKind Kind { get; }

    /// <summary>The value as written: a number's digits or the string.</summary>
    public string Text { get; }

    /// <summary>The number of a <see cref="ContentValueKind.Number"/>.</summary>
    public int Number { get; }

    /// <summary>How many dice of a <see cref="ContentValueKind.Dice"/>.</summary>
    public int Count { get; }

    /// <summary>The sides of a <see cref="ContentValueKind.Dice"/>.</summary>
    public int Sides { get; }

    /// <summary>The reference name without <c>$</c>: <c>roll</c>, <c>choice</c>, <c>result</c> or a parameter.</summary>
    public string? Name { get; }

    /// <summary>A minus before dice or a reference.</summary>
    public bool Negative { get; }

    public static ContentValue Of(int number) =>
        new(ContentValueKind.Number, number.ToString(CultureInfo.InvariantCulture), number, 0, 0, null, number < 0);

    /// <summary>Reads dice or a reference; null when <paramref name="text"/> is neither.</summary>
    public static ContentValue? TryParse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (DicePattern().Match(text) is { Success: true } dice)
        {
            var count = dice.Groups["count"].Success ? int.Parse(dice.Groups["count"].Value, CultureInfo.InvariantCulture) : 1;
            var sides = int.Parse(dice.Groups["sides"].Value, CultureInfo.InvariantCulture);
            return count is >= 1 and <= MaxDice && sides is >= 2 and <= MaxSides
                ? new ContentValue(ContentValueKind.Dice, text, 0, count, sides, null, dice.Groups["minus"].Success)
                : null;
        }

        return ReferencePattern().Match(text) is { Success: true } reference
            ? new ContentValue(ContentValueKind.Reference, text, 0, 0, 0, reference.Groups["name"].Value, reference.Groups["minus"].Success)
            : null;
    }

    public const int MaxDice = 20;
    public const int MaxSides = 100;

    [GeneratedRegex(@"^(?<minus>-)?(?<count>\d{1,2})?d(?<sides>\d{1,3})$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DicePattern();

    [GeneratedRegex(@"^(?<minus>-)?\$(?<name>[a-zA-Z][a-zA-Z0-9]{0,39})$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ReferencePattern();

    public override string ToString() => Text;
}

internal sealed class ContentValueConverter : JsonConverter<ContentValue>
{
    public override ContentValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt32(out var number) => ContentValue.Of(number),
            JsonTokenType.String => ContentValue.TryParse(reader.GetString()!)
                ?? throw new JsonException($"«{reader.GetString()}» is neither dice («1d6», «-1d6») nor a reference («$roll»)."),
            _ => throw new JsonException("A value is a whole number, dice («1d6») or a reference («$roll»)."),
        };

    public override void Write(Utf8JsonWriter writer, ContentValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Kind == ContentValueKind.Number)
        {
            writer.WriteNumberValue(value.Number);
        }
        else
        {
            writer.WriteStringValue(value.Text);
        }
    }
}

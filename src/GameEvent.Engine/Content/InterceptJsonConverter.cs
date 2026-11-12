using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameEvent.Engine.Content;

/// <summary>
/// <c>intercept</c> as a set (D-484): a string (<c>"hostile"</c>) or a list of distinct values (<c>["badEvent", "hostile"]</c>).
/// One value is written as a string, as before stage 5, so a published pack of the Shield writes the same bytes; several as a list.
/// </summary>
internal sealed class InterceptJsonConverter : JsonConverter<Intercept?>
{
    private static readonly (Intercept Value, string Name)[] s_names = [(Intercept.Hostile, "hostile"), (Intercept.BadEvent, "badEvent")];

    public override bool HandleNull => true;

    /// <summary>The names of the values, as the schema lists them.</summary>
    public static IEnumerable<string> Names => s_names.Select(n => n.Name);

    public override Intercept? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return One(reader.GetString());
            case JsonTokenType.StartArray:
                Intercept set = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var value = reader.TokenType == JsonTokenType.String
                        ? One(reader.GetString())
                        : throw new JsonException("«intercept» lists names of what is intercepted.");
                    if ((set & value) != 0)
                    {
                        throw new JsonException($"«intercept» names «{Name(value)}» twice.");
                    }

                    set |= value;
                }

                return set == 0 ? throw new JsonException("«intercept» names at least one thing to intercept.") : set;
            default:
                throw new JsonException("«intercept» is a name or a list of names.");
        }
    }

    public override void Write(Utf8JsonWriter writer, Intercept? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is not { } set)
        {
            writer.WriteNullValue();
            return;
        }

        var names = s_names.Where(n => set.HasFlag(n.Value)).Select(n => n.Name).ToList();
        if (names.Count == 1)
        {
            writer.WriteStringValue(names[0]);
            return;
        }

        writer.WriteStartArray();
        foreach (var name in names)
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
    }

    private static Intercept One(string? name) =>
        s_names.FirstOrDefault(n => n.Name == name) is { Name: not null } found
            ? found.Value
            : throw new JsonException($"«{name}» is not something an effect intercepts: {string.Join(", ", s_names.Select(n => n.Name))}.");

    private static string Name(Intercept value) => s_names.First(n => n.Value == value).Name;
}

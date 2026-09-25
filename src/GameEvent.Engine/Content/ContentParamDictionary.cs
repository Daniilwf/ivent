using System.Collections;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameEvent.Engine.Content;

/// <summary>
/// The <c>params</c> of <c>giveObject</c>: names to plain text or a reference, compared by value, so content with
/// parameters is equal to itself after a round trip (D-103). Written as a JSON object in name order.
/// </summary>
[JsonConverter(typeof(ContentParamDictionaryConverter))]
public sealed class ContentParamDictionary : IReadOnlyDictionary<string, string>, IEquatable<ContentParamDictionary>
{
    private readonly ImmutableSortedDictionary<string, string> _values;

    public ContentParamDictionary(IEnumerable<KeyValuePair<string, string>> values)
    {
        _values = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, values);
    }

    public string this[string key] => _values[key];

    public IEnumerable<string> Keys => _values.Keys;

    public IEnumerable<string> Values => _values.Values;

    public int Count => _values.Count;

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string value) =>
        _values.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(ContentParamDictionary? other) => other is not null && _values.SequenceEqual(other._values);

    public override bool Equals(object? obj) => Equals(obj as ContentParamDictionary);

    public override int GetHashCode() => _values.Count;
}

internal sealed class ContentParamDictionaryConverter : JsonConverter<ContentParamDictionary>
{
    public override ContentParamDictionary Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new((JsonSerializer.Deserialize<Dictionary<string, string?>>(ref reader, options) ?? throw new JsonException("«params» is an object."))
            .Select(p => KeyValuePair.Create(p.Key, p.Value ?? throw new JsonException($"Parameter «{p.Key}» is text or a reference, not null."))));

    public override void Write(Utf8JsonWriter writer, ContentParamDictionary value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        foreach (var (name, text) in value)
        {
            writer.WriteString(name, text);
        }

        writer.WriteEndObject();
    }
}

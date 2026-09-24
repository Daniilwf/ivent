using System.Collections;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameEvent.Engine.Kernel;

/// <summary>
/// A player's resources other than points and coins (SPEC «Ресурсы игрока»: a dictionary, so a new currency or
/// counter needs no schema change). Keys are resource names from the ruleset; zero amounts are not stored.
/// Compares by content and serializes as a JSON object.
/// </summary>
[JsonConverter(typeof(ResourceBagJsonConverter))]
public readonly struct ResourceBag : IEquatable<ResourceBag>, IEnumerable<KeyValuePair<string, int>>
{
    private readonly ImmutableSortedDictionary<string, int>? _amounts;

    private ResourceBag(ImmutableSortedDictionary<string, int> amounts) => _amounts = amounts;

    public static ResourceBag Empty => new(ImmutableSortedDictionary.Create<string, int>(StringComparer.Ordinal));

    private ImmutableSortedDictionary<string, int> Amounts => _amounts ?? Empty._amounts!;

    public int this[string resource] => Amounts.GetValueOrDefault(resource);

    public int Count => Amounts.Count;

    public ResourceBag Add(string resource, int delta)
    {
        var amount = this[resource] + delta;
        return new(amount == 0 ? Amounts.Remove(resource) : Amounts.SetItem(resource, amount));
    }

    public static ResourceBag From(IEnumerable<KeyValuePair<string, int>> amounts) =>
        amounts.Aggregate(Empty, (bag, pair) => bag.Add(pair.Key, pair.Value));

    public bool Equals(ResourceBag other) => Amounts.SequenceEqual(other.Amounts);

    public override bool Equals(object? obj) => obj is ResourceBag other && Equals(other);

    public override int GetHashCode() => Amounts.Aggregate(0, (hash, pair) => HashCode.Combine(hash, pair.Key, pair.Value));

    public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => Amounts.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"{{{string.Join(", ", Amounts.Select(p => $"{p.Key}: {p.Value}"))}}}";

    public static bool operator ==(ResourceBag left, ResourceBag right) => left.Equals(right);

    public static bool operator !=(ResourceBag left, ResourceBag right) => !left.Equals(right);
}

internal sealed class ResourceBagJsonConverter : JsonConverter<ResourceBag>
{
    public override ResourceBag Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ResourceBag.From(JsonSerializer.Deserialize<Dictionary<string, int>>(ref reader, options) ?? []);

    public override void Write(Utf8JsonWriter writer, ResourceBag value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), options);
}

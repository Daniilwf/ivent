using System.Collections.Immutable;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Content;

/// <summary>One entry of a loot wheel: an object and its weight (SPEC «Колёса и колоды — одна сущность»).</summary>
public sealed record WheelEntrySpec
{
    public required string ObjectId { get; init; }

    public required int Weight { get; init; }
}

/// <summary>
/// A wheel of objects (a loot box): <c>spinWheel</c> picks one entry by weight and gives its object (D-401). Wheels of
/// categories and decks of events stay with their own mechanics.
/// </summary>
public sealed record WheelDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required EquatableArray<WheelEntrySpec> Entries { get; init; }
}

/// <summary>
/// The season's content as the admin loads it (SPEC «Контент как файлы», D-400): object definitions and wheels. The
/// files of <c>/content/items</c> and <c>/content/wheels</c> make one pack.
/// </summary>
public sealed record ContentPack
{
    public EquatableArray<ObjectDefinition> Objects { get; init; } = [];

    public EquatableArray<WheelDefinition> Wheels { get; init; } = [];
}

/// <summary>Builds a pack from content files (SPEC «Контент как файлы», D-400): one object or wheel per JSON text.</summary>
public static class ContentFiles
{
    /// <summary>
    /// The pack of <paramref name="objects"/> and <paramref name="wheels"/> (file name, JSON text), in the order of the
    /// file names; a file that does not parse is a <see cref="System.Text.Json.JsonException"/> naming it.
    /// </summary>
    public static ContentPack Pack(IEnumerable<(string Name, string Json)> objects, IEnumerable<(string Name, string Json)> wheels)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(wheels);
        return new ContentPack
        {
            Objects = [.. objects.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => Parse<ObjectDefinition>(f))],
            Wheels = [.. wheels.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => Parse<WheelDefinition>(f))],
        };
    }

    private static T Parse<T>((string Name, string Json) file)
        where T : notnull
    {
        try
        {
            return ContentJson.Parse<T>(file.Json);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new System.Text.Json.JsonException($"{file.Name}: {e.Message}", e);
        }
    }
}

/// <summary>
/// A definition as the season keeps it: <see cref="Deleted"/> once a later pack no longer has it (invariant 11, soft
/// deletion). A deleted definition is not sold or given, but the objects players hold keep working.
/// </summary>
public sealed record CatalogEntry(ObjectDefinition Definition, bool Deleted = false);

/// <summary>
/// The content in force in a season (D-400): every definition ever published, the current wheels and the number of
/// publications. Empty until the admin publishes content.
/// </summary>
public sealed record ContentCatalog(
    int Version,
    ImmutableSortedDictionary<string, CatalogEntry> Objects,
    ImmutableSortedDictionary<string, WheelDefinition> Wheels)
{
    public static ContentCatalog Empty { get; } = new(
        0,
        ImmutableSortedDictionary.Create<string, CatalogEntry>(StringComparer.Ordinal),
        ImmutableSortedDictionary.Create<string, WheelDefinition>(StringComparer.Ordinal));

    /// <summary>The definition of <paramref name="objectId"/>, deleted or not; null when it was never published.</summary>
    public ObjectDefinition? Find(string objectId) => Objects.GetValueOrDefault(objectId)?.Definition;

    /// <summary>The definition of <paramref name="objectId"/> when it is in the current pack.</summary>
    public ObjectDefinition? Live(string objectId) =>
        Objects.GetValueOrDefault(objectId) is { Deleted: false } entry ? entry.Definition : null;

    /// <summary>A definition every object held in the season has: a missing one is a bug upstream.</summary>
    public ObjectDefinition Get(string objectId) =>
        Find(objectId) ?? throw new KeyNotFoundException($"Object definition '{objectId}' was never published in this season.");

    public bool Equals(ContentCatalog? other) =>
        other is not null && Version == other.Version && Objects.SequenceEqual(other.Objects) && Wheels.SequenceEqual(other.Wheels);

    public override int GetHashCode() => HashCode.Combine(Version, Objects.Count, Wheels.Count);

    /// <summary>The catalog after <paramref name="pack"/> is published: new and changed definitions replace, missing ones are deleted.</summary>
    public ContentCatalog Publish(ContentPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var ids = pack.Objects.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var objects = Objects.ToImmutableSortedDictionary(
            p => p.Key, p => ids.Contains(p.Key) ? p.Value : p.Value with { Deleted = true }, StringComparer.Ordinal);
        foreach (var definition in pack.Objects)
        {
            objects = objects.SetItem(definition.Id, new CatalogEntry(definition));
        }

        return new ContentCatalog(
            Version + 1,
            objects,
            pack.Wheels.ToImmutableSortedDictionary(w => w.Id, w => w, StringComparer.Ordinal));
    }
}

/// <summary>
/// The admin loads a content pack into the season (D-400): checked as a whole, all problems at once. Needs
/// <c>features.items</c>; allowed while the season is a draft or running.
/// </summary>
public sealed record PublishContent(ContentPack Pack, string Comment) : ICommand;

/// <summary>A new version of the season's content, stored whole, like a map (D-300, D-400).</summary>
[EventType("content-published")]
public sealed record ContentPublished(int Version, ContentPack Pack, string Comment) : IGameEvent;

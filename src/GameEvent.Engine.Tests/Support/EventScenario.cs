using GameEvent.Engine.Content;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Tests.Support;

/// <summary>
/// Events in scenarios (stage 5): the flag on with items (events are content, D-530), decks as wheels with a kind (D-501).
/// A draw takes one random value: a ticket in [0, the sum of the live weights), entries in the deck's order.
/// </summary>
public static class EventScenario
{
    /// <summary>The ruleset with items and events on and <paramref name="defaultDeck"/> as <c>events.defaultDeck</c>.</summary>
    public static Ruleset WithEvents(this Ruleset ruleset, EventKind defaultDeck = EventKind.Risky)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        return ruleset with
        {
            Features = ruleset.Features with { Items = true, Events = true },
            Events = new EventRules { DefaultDeck = defaultDeck },
        };
    }

    /// <summary>A scenario on the pinned ruleset with items and events on.</summary>
    public static Scenario New(int seed = 42) => Scenario.New(TestRuleset.Create().WithEvents(), seed);

    /// <summary>A deck of <paramref name="kind"/> named <paramref name="id"/> (a standard deck when null) of the events, weight 1 each.</summary>
    public static WheelDefinition Deck(EventKind kind, string? id = null, params string[] events) =>
        Deck(kind, id, [.. events.Select(e => (e, 1))]);

    /// <summary>A deck with weights.</summary>
    public static WheelDefinition Deck(EventKind kind, string? id, params (string Event, int Weight)[] entries) =>
        new()
        {
            Id = id ?? kind.ToString().ToLowerInvariant(),
            Name = $"Колода {id ?? kind.ToString()}",
            Deck = kind,
            Entries = [.. entries.Select(e => new WheelEntrySpec { ObjectId = e.Event, Weight = e.Weight })],
        };

    /// <summary>A pack of CONTENT.md examples <paramref name="ids"/> and <paramref name="decks"/>.</summary>
    public static ContentPack Pack(string[] ids, params WheelDefinition[] decks)
    {
        var examples = EconomyScenario.Examples();
        return new ContentPack
        {
            Objects = [.. ids.Select(id => examples[id]).OrderBy(o => o.Id, StringComparer.Ordinal)],
            Wheels = [.. decks.OrderBy(d => d.Id, StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// The content of a season with events (D-533): /content/items and /content/events as objects, /content/wheels and
    /// /content/decks as wheels.
    /// </summary>
    public static ContentPack RepositoryPack()
    {
        var root = Path.Combine(RepositoryPaths.Root(), "content");
        IEnumerable<(string, string)> Read(params string[] dirs) =>
            dirs.SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.json")).Select(f => (Path.GetFileName(f), File.ReadAllText(f)));
        return ContentFiles.Pack(Read("items", "events"), Read("wheels", "decks"));
    }

    /// <summary>A map cell of type <c>event</c> drawing from <paramref name="deck"/>.</summary>
    public static MapBuilder EventCell(this MapBuilder map, string id, string deck = "zone")
    {
        ArgumentNullException.ThrowIfNull(map);
        return map.Cell(id, Engine.Map.CellType.Event, c => c with { Deck = deck });
    }
}

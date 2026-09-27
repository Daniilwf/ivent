using System.Text.Json.Serialization;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Map;

/// <summary>
/// What a cell does (SPEC «Карта»). The linear map of stage 1 uses <see cref="Start"/>, <see cref="Empty"/> and
/// <see cref="Finish"/>; the graph map adds the rest (D-302). New values are appended: the log stores them by name.
/// </summary>
public enum CellType
{
    Start,
    Empty,
    Finish,

    /// <summary>Several exits, one of them the default branch: the player's own move stops here to choose (D-304).</summary>
    Fork,

    /// <summary>A shortcut or a snake: a stop here transfers the player to <see cref="Cell.To"/> (D-303).</summary>
    Teleport,

    /// <summary>A push back never leaves this cell (D-306).</summary>
    Checkpoint,

    /// <summary>A stop here gives <see cref="Cell.Amount"/> points (D-303).</summary>
    PointsBonus,

    /// <summary>A stop here draws from <see cref="Cell.Deck"/>; needs <c>features.events</c> (D-302).</summary>
    Event,

    /// <summary>A stop or a pass here grants <see cref="Cell.Grants"/>; needs <c>features.shop</c> (D-302).</summary>
    Shop,
}

/// <summary>
/// A node of the map. Ids are stable strings: positions survive map edits. The parameters are those of CONTENT.md
/// «клетки», plus the zone and the editor's coordinates; each is written only when set, so a map of stage 1 reads and
/// writes as before (D-300).
/// </summary>
public sealed record Cell(string Id, CellType Type)
{
    /// <summary>The zone the cell belongs to (<see cref="MapGraph.Zones"/>), if any.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Zone { get; init; }

    /// <summary>A teleport's destination.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? To { get; init; }

    /// <summary>A points bonus.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Amount { get; init; }

    /// <summary>The deck an event cell draws from: <c>zone</c> or a deck id.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Deck { get; init; }

    /// <summary>What a shop cell grants, such as <c>shop-coupon</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Grants { get; init; }

    /// <summary>Where the editor draws the cell over the background; the rules never read it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? X { get; init; }

    /// <inheritdoc cref="X"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Y { get; init; }
}

/// <summary>A directed arrow between cells.</summary>
public sealed record Edge(string From, string To, bool IsDefaultForward, bool IsPrimaryBackward);

/// <summary>
/// The map is a graph from day one; the stage 1 linear map is a generated chain. <see cref="Zones"/> are the rules of
/// the cells' zones (CONTENT.md «Зона»); written only when there are any (D-300).
/// </summary>
public sealed record MapGraph(EquatableArray<Cell> Cells, EquatableArray<Edge> Edges)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EquatableArray<ZoneDefinition> Zones { get; init; }

    [JsonIgnore]
    public Cell Start => Cells.Single(c => c.Type == CellType.Start);

    public Cell CellById(string id) =>
        Cells.FirstOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException($"Cell '{id}' is not on the map.");

    public bool HasCell(string id) => Cells.Any(c => c.Id == id);

    /// <summary>The arrows out of <paramref name="cellId"/>: the default branch first, then by target id (the order of a branch choice).</summary>
    public IReadOnlyList<Edge> Exits(string cellId) =>
        [.. Edges.Where(e => e.From == cellId).OrderByDescending(e => e.IsDefaultForward).ThenBy(e => e.To, StringComparer.Ordinal)];

    /// <summary>The zone rules of <paramref name="cellId"/>, or null when the cell is in no zone or not on the map.</summary>
    public ZoneDefinition? ZoneOf(string cellId) =>
        Cells.FirstOrDefault(c => c.Id == cellId)?.Zone is { } zone ? Zones.FirstOrDefault(z => z.Id == zone) : null;
}

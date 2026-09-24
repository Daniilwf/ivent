using System.Text.Json.Serialization;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Map;

public enum CellType
{
    Start,
    Empty,
    Finish,
}

/// <summary>A node of the map. Ids are stable strings: positions survive map edits.</summary>
public sealed record Cell(string Id, CellType Type);

/// <summary>A directed arrow between cells.</summary>
public sealed record Edge(string From, string To, bool IsDefaultForward, bool IsPrimaryBackward);

/// <summary>The map is a graph from day one; the stage 1 linear map is a generated chain.</summary>
public sealed record MapGraph(EquatableArray<Cell> Cells, EquatableArray<Edge> Edges)
{
    [JsonIgnore]
    public Cell Start => Cells.Single(c => c.Type == CellType.Start);

    public Cell CellById(string id) =>
        Cells.FirstOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException($"Cell '{id}' is not on the map.");
}

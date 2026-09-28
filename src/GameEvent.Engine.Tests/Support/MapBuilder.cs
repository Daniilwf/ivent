using GameEvent.Engine.Content;
using GameEvent.Engine.Map;

namespace GameEvent.Engine.Tests.Support;

/// <summary>
/// Draws a graph map for a test the way the editor would:
/// <code>
/// MapBuilder.New()
///     .Path("start", "a", "f", "b1", "j", "finish")   // the main line: default exits, primary entries
///     .Path("f", "c1", "j")                            // a second branch: not default out of f, not primary into j
///     .Teleport("a", to: "j")
///     .Build();
/// </code>
/// A <see cref="Path"/> edge is the default exit of its source when the source has no exit yet, and the primary entry of
/// its target when the target has no entry yet. Cells named but not declared are created: <c>start</c> and <c>finish</c>
/// by name, others empty; on <see cref="Build"/> an empty cell with several exits becomes a fork (unless
/// <see cref="KeepTypes"/>), so wrong maps can still be drawn on purpose.
/// </summary>
public sealed class MapBuilder
{
    private readonly List<Cell> _cells = [];
    private readonly List<Edge> _edges = [];
    private readonly List<ZoneDefinition> _zones = [];
    private bool _keepTypes;

    public static MapBuilder New() => new();

    /// <summary>Declares (or redeclares) a cell with its type and parameters.</summary>
    public MapBuilder Cell(string id, CellType type, Func<Cell, Cell>? with = null)
    {
        var cell = new Cell(id, type);
        cell = with?.Invoke(cell) ?? cell;
        var index = _cells.FindIndex(c => c.Id == id);
        if (index >= 0)
        {
            _cells[index] = cell;
        }
        else
        {
            _cells.Add(cell);
        }

        return this;
    }

    public MapBuilder Path(params string[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var id in ids)
        {
            Ensure(id);
        }

        for (var i = 0; i + 1 < ids.Length; i++)
        {
            if (_edges.Any(e => e.From == ids[i] && e.To == ids[i + 1]))
            {
                continue;
            }

            _edges.Add(new Edge(
                ids[i], ids[i + 1],
                IsDefaultForward: _edges.All(e => e.From != ids[i]),
                IsPrimaryBackward: _edges.All(e => e.To != ids[i + 1])));
        }

        return this;
    }

    /// <summary>An arrow with explicit marks.</summary>
    public MapBuilder Edge(string from, string to, bool isDefault, bool isPrimary)
    {
        Ensure(from);
        Ensure(to);
        _edges.Add(new Edge(from, to, isDefault, isPrimary));
        return this;
    }

    /// <summary>Removes the arrow <paramref name="from"/> → <paramref name="to"/>.</summary>
    public MapBuilder WithoutEdge(string from, string to)
    {
        _edges.RemoveAll(e => e.From == from && e.To == to);
        return this;
    }

    /// <summary>Turns a cell drawn by a path into a teleport to <paramref name="to"/>.</summary>
    public MapBuilder Teleport(string id, string to) => Retype(id, CellType.Teleport, c => c with { To = to });

    public MapBuilder Checkpoint(string id) => Retype(id, CellType.Checkpoint);

    public MapBuilder Bonus(string id, int amount) => Retype(id, CellType.PointsBonus, c => c with { Amount = amount });

    /// <summary>Adds a zone and puts <paramref name="cellIds"/> in it.</summary>
    public MapBuilder Zone(ZoneDefinition zone, params string[] cellIds)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(cellIds);
        _zones.Add(zone);
        foreach (var id in cellIds)
        {
            Ensure(id);
            var index = _cells.FindIndex(c => c.Id == id);
            _cells[index] = _cells[index] with { Zone = zone.Id };
        }

        return this;
    }

    /// <summary>Build keeps the declared types even where a cell has several exits.</summary>
    public MapBuilder KeepTypes()
    {
        _keepTypes = true;
        return this;
    }

    public MapGraph Build()
    {
        var cells = _cells.Select(c =>
            !_keepTypes && c.Type == CellType.Empty && _edges.Count(e => e.From == c.Id) > 1 ? c with { Type = CellType.Fork } : c);
        return new MapGraph([.. cells], [.. _edges]) { Zones = [.. _zones] };
    }

    private MapBuilder Retype(string id, CellType type, Func<Cell, Cell>? with = null)
    {
        Ensure(id);
        var index = _cells.FindIndex(c => c.Id == id);
        var cell = _cells[index] with { Type = type };
        _cells[index] = with?.Invoke(cell) ?? cell;
        return this;
    }

    private void Ensure(string id)
    {
        if (_cells.All(c => c.Id != id))
        {
            _cells.Add(new Cell(id, id switch
            {
                "start" => CellType.Start,
                "finish" => CellType.Finish,
                _ => CellType.Empty,
            }));
        }
    }
}

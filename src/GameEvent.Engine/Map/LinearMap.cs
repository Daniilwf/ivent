namespace GameEvent.Engine.Map;

/// <summary>
/// Generates the stage 1 map: <c>start</c>, <c>c1</c>…<c>c{N-1}</c>, <c>finish</c>, where N is
/// <c>map.linearLength</c>, the number of steps from start to finish (D-03).
/// </summary>
public static class LinearMap
{
    public const string StartId = "start";
    public const string FinishId = "finish";

    public static MapGraph Generate(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);

        var cells = new List<Cell> { new(StartId, CellType.Start) };
        for (var i = 1; i < length; i++)
        {
            cells.Add(new Cell($"c{i}", CellType.Empty));
        }

        cells.Add(new Cell(FinishId, CellType.Finish));

        var edges = cells.Zip(cells.Skip(1), (from, to) => new Edge(from.Id, to.Id, IsDefaultForward: true, IsPrimaryBackward: true));
        return new MapGraph([.. cells], [.. edges]);
    }
}

namespace GameEvent.Engine.Map;

/// <summary>How a move touched a cell; each kind is an effect trigger (CONTENT.md: <c>moveStep</c>, <c>pass</c>, <c>stop</c>).</summary>
public enum CellVisitKind
{
    /// <summary>Every cell entered by a step, the last one included.</summary>
    MoveStep,

    /// <summary>A cell stepped through without stopping.</summary>
    Pass,

    /// <summary>The cell the move ended on. A transfer's destination is not a stop (SPEC «Движение»).</summary>
    Stop,
}

/// <summary>One trigger point of a move. Stage 1 has no subscribers; C11 dispatches cell effects on these.</summary>
public sealed record CellVisit(string CellId, CellVisitKind Kind);

using GameEvent.Engine.Content;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Map;

/// <summary>
/// A problem of a map: a stable code for the editor's dictionary, what it is about (a cell id, an arrow <c>from→to</c>,
/// a zone id or <c>map</c>) and a message in English for logs.
/// </summary>
public sealed record MapError(string Code, string Subject, string Message);

/// <summary>Stable codes of <see cref="MapError"/>.</summary>
public static class MapErrorCodes
{
    public const string TooLarge = "map.tooLarge";
    public const string StartCount = "map.startCount";
    public const string NoFinish = "map.noFinish";
    public const string CellIdInvalid = "map.cellIdInvalid";
    public const string CellDuplicate = "map.cellDuplicate";
    public const string EdgeUnknownCell = "map.edgeUnknownCell";
    public const string EdgeSelfLoop = "map.edgeSelfLoop";
    public const string EdgeDuplicate = "map.edgeDuplicate";
    public const string Unreachable = "map.unreachable";
    public const string FinishUnreachable = "map.finishUnreachable";
    public const string FinishHasExits = "map.finishHasExits";
    public const string DeadEnd = "map.deadEnd";
    public const string ForkExits = "map.forkExits";
    public const string NotAFork = "map.notAFork";
    public const string DefaultBranch = "map.defaultBranch";
    public const string PrimaryBackward = "map.primaryBackward";
    public const string TeleportTarget = "map.teleportTarget";
    public const string TeleportCycle = "map.teleportCycle";
    public const string CellParameter = "map.cellParameter";
    public const string FeatureDisabled = "map.featureDisabled";
    public const string ZoneUnknown = "map.zoneUnknown";
    public const string ZoneDuplicate = "map.zoneDuplicate";
    public const string ZoneInvalid = "map.zoneInvalid";
    public const string ZoneUnsupported = "map.zoneUnsupported";
    public const string OccupiedCellRemoved = "map.occupiedCellRemoved";
}

/// <summary>
/// The checks before a map is published (SPEC «Редактор», D-302): cells and arrows well formed, one start and a finish,
/// every cell reachable from the start and the finish reachable from every cell (arrows and teleports), exits (a fork
/// has two or more and one default branch, the finish none, any other cell exactly one, the default), a primary incoming
/// edge on cells with several entries, teleports to another cell but not the finish and without cycles, parameters of
/// the cell's type, mechanics switched on, zones known and valid. All problems are reported at once.
/// </summary>
public static class MapValidator
{
    /// <summary>Safety ceilings, not balance: a pasted typo must not make the queue walk a gigantic graph.</summary>
    public const int MaxCells = 2_000;

    public const int MaxEdges = 10_000;
    public const int MaxZones = 100;
    public const int MaxIdLength = 64;

    public static IReadOnlyList<MapError> Validate(MapGraph map, Ruleset rules)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(rules);

        if (map.Cells.Count > MaxCells || map.Edges.Count > MaxEdges || map.Zones.Count > MaxZones)
        {
            return [new(MapErrorCodes.TooLarge, "map", $"A map has at most {MaxCells} cells, {MaxEdges} arrows and {MaxZones} zones.")];
        }

        var errors = new List<MapError>();
        void Error(string code, string subject, string message) => errors.Add(new MapError(code, subject, message));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in map.Cells)
        {
            if (string.IsNullOrWhiteSpace(cell.Id) || cell.Id.Length > MaxIdLength || cell.Id.Trim() != cell.Id)
            {
                Error(MapErrorCodes.CellIdInvalid, cell.Id, $"A cell id is 1–{MaxIdLength} characters without surrounding spaces.");
            }

            if (!ids.Add(cell.Id))
            {
                Error(MapErrorCodes.CellDuplicate, cell.Id, $"Cell '{cell.Id}' is listed twice.");
            }
        }

        var starts = map.Cells.Count(c => c.Type == CellType.Start);
        if (starts != 1)
        {
            Error(MapErrorCodes.StartCount, "map", $"A map has exactly one start, this one has {starts}.");
        }

        if (!map.Cells.Any(c => c.Type == CellType.Finish))
        {
            Error(MapErrorCodes.NoFinish, "map", "A map needs a finish.");
        }

        var edges = new List<Edge>();
        var pairs = new HashSet<(string, string)>();
        foreach (var edge in map.Edges)
        {
            var subject = $"{edge.From}→{edge.To}";
            if (!ids.Contains(edge.From) || !ids.Contains(edge.To))
            {
                Error(MapErrorCodes.EdgeUnknownCell, subject, "An arrow joins cells of the map.");
            }
            else if (edge.From == edge.To)
            {
                Error(MapErrorCodes.EdgeSelfLoop, subject, "An arrow leads to another cell.");
            }
            else if (!pairs.Add((edge.From, edge.To)))
            {
                Error(MapErrorCodes.EdgeDuplicate, subject, "Two cells are joined by one arrow at most.");
            }
            else
            {
                edges.Add(edge);
            }
        }

        var cells = map.Cells.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var exits = edges.ToLookup(e => e.From, StringComparer.Ordinal);
        var entries = edges.ToLookup(e => e.To, StringComparer.Ordinal);
        foreach (var cell in cells.Values)
        {
            CheckExits(cell, [.. exits[cell.Id]], Error);
            var incoming = entries[cell.Id].ToList();
            if (incoming.Count > 1 && incoming.Count(e => e.IsPrimaryBackward) != 1)
            {
                Error(MapErrorCodes.PrimaryBackward, cell.Id, "A cell with several entries marks exactly one of them as the primary incoming edge.");
            }

            CheckParameters(cell, cells, rules.Features, Error);
        }

        CheckTeleportCycles(cells, Error);
        CheckReach(map, cells, exits, Error);
        CheckZones(map, cells, Error);
        return errors;
    }

    /// <summary>The rejection for an invalid map, or null when it is valid.</summary>
    internal static Kernel.Decision? Check(MapGraph map, Ruleset rules)
    {
        var errors = Validate(map, rules);
        return errors.Count == 0
            ? null
            : Kernel.Decision.Reject(Kernel.RejectionCodes.MapInvalid, string.Join("; ", errors.Select(e => $"{e.Code} {e.Subject}: {e.Message}")));
    }

    private static void CheckExits(Cell cell, List<Edge> exits, Action<string, string, string> error)
    {
        var defaults = exits.Count(e => e.IsDefaultForward);
        switch (cell.Type)
        {
            case CellType.Finish when exits.Count > 0:
                error(MapErrorCodes.FinishHasExits, cell.Id, "The finish is a stop cell: no arrows lead out of it.");
                break;
            case CellType.Finish:
                break;
            case CellType.Fork when exits.Count < 2:
                error(MapErrorCodes.ForkExits, cell.Id, "A fork has at least two exits.");
                break;
            case not CellType.Fork when exits.Count > 1:
                error(MapErrorCodes.NotAFork, cell.Id, "Only a fork has several exits.");
                break;
            case not CellType.Fork when exits.Count == 0:
                error(MapErrorCodes.DeadEnd, cell.Id, "An arrow leads out of every cell but the finish.");
                break;
            default:
                if (defaults != 1)
                {
                    error(MapErrorCodes.DefaultBranch, cell.Id, "Exactly one exit is the default branch.");
                }

                break;
        }
    }

    private static void CheckParameters(Cell cell, Dictionary<string, Cell> cells, Features features, Action<string, string, string> error)
    {
        // Each parameter belongs to one type; the type's own parameter is required.
        var misplaced = new[]
        {
            (Set: cell.To is not null, Type: CellType.Teleport, Name: "to"),
            (Set: cell.Amount is not null, Type: CellType.PointsBonus, Name: "amount"),
            (Set: cell.Deck is not null, Type: CellType.Event, Name: "deck"),
            (Set: cell.Grants is not null, Type: CellType.Shop, Name: "grants"),
        };
        foreach (var (set, type, name) in misplaced)
        {
            if (set && cell.Type != type)
            {
                error(MapErrorCodes.CellParameter, cell.Id, $"«{name}» belongs to a {type} cell.");
            }
        }

        switch (cell.Type)
        {
            case CellType.Teleport when cell.To is null || !cells.TryGetValue(cell.To, out var target) || target.Id == cell.Id || target.Type == CellType.Finish:
                // D-303: the finish is reached by playing, with places and bonuses
                error(MapErrorCodes.TeleportTarget, cell.Id, "A teleport leads to another cell of the map, not the finish.");
                break;
            case CellType.PointsBonus when cell.Amount is null or 0:
                error(MapErrorCodes.CellParameter, cell.Id, "A points bonus needs a non-zero «amount».");
                break;
            case CellType.Event when string.IsNullOrWhiteSpace(cell.Deck):
                error(MapErrorCodes.CellParameter, cell.Id, "An event cell needs «deck».");
                break;
            case CellType.Shop when string.IsNullOrWhiteSpace(cell.Grants):
                error(MapErrorCodes.CellParameter, cell.Id, "A shop cell needs «grants».");
                break;
        }

        // D-302: a disabled mechanic does not appear on the map
        if ((cell.Type == CellType.Event && !features.Events) || (cell.Type == CellType.Shop && !features.Shop))
        {
            error(MapErrorCodes.FeatureDisabled, cell.Id, $"A {cell.Type} cell needs its mechanic switched on.");
        }
    }

    private static void CheckTeleportCycles(Dictionary<string, Cell> cells, Action<string, string, string> error)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in cells.Values.Where(c => c.Type == CellType.Teleport).OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            var seen = new List<string>();
            var current = cell;
            while (current is { Type: CellType.Teleport, To: { } to } && !seen.Contains(current.Id))
            {
                seen.Add(current.Id);
                current = cells.GetValueOrDefault(to);
            }

            if (current is not null && current.Id == cell.Id && reported.Add(cell.Id))
            {
                foreach (var id in seen)
                {
                    reported.Add(id);
                }

                error(MapErrorCodes.TeleportCycle, cell.Id, $"Teleports lead in a circle: {string.Join(" → ", seen.Append(cell.Id))}.");
            }
        }
    }

    // Reach goes along arrows and teleports: a teleport's destination is where a stop there leads.
    private static void CheckReach(MapGraph map, Dictionary<string, Cell> cells, ILookup<string, Edge> exits, Action<string, string, string> error)
    {
        IEnumerable<string> Next(string id)
        {
            foreach (var edge in exits[id])
            {
                yield return edge.To;
            }

            if (cells[id] is { Type: CellType.Teleport, To: { } to } && cells.ContainsKey(to))
            {
                yield return to;
            }
        }

        var starts = map.Cells.Where(c => c.Type == CellType.Start).Select(c => c.Id).ToList();
        var fromStart = Closure(starts, Next);
        if (starts.Count > 0)
        {
            foreach (var id in cells.Keys.Where(id => !fromStart.Contains(id)).Order(StringComparer.Ordinal))
            {
                error(MapErrorCodes.Unreachable, id, "No way leads to this cell from the start.");
            }
        }

        var back = cells.Keys.SelectMany(id => Next(id).Select(to => (From: id, To: to))).ToLookup(p => p.To, p => p.From, StringComparer.Ordinal);
        var finishes = map.Cells.Where(c => c.Type == CellType.Finish).Select(c => c.Id).ToList();
        var toFinish = Closure(finishes, id => back[id]);
        if (finishes.Count > 0)
        {
            foreach (var id in cells.Keys.Where(id => !toFinish.Contains(id)).Order(StringComparer.Ordinal))
            {
                error(MapErrorCodes.FinishUnreachable, id, "No way leads from this cell to the finish.");
            }
        }
    }

    private static HashSet<string> Closure(IEnumerable<string> from, Func<string, IEnumerable<string>> next)
    {
        var seen = new HashSet<string>(from, StringComparer.Ordinal);
        var queue = new Queue<string>(seen);
        while (queue.TryDequeue(out var id))
        {
            foreach (var to in next(id))
            {
                if (seen.Add(to))
                {
                    queue.Enqueue(to);
                }
            }
        }

        return seen;
    }

    private static void CheckZones(MapGraph map, Dictionary<string, Cell> cells, Action<string, string, string> error)
    {
        var zones = new HashSet<string>(StringComparer.Ordinal);
        foreach (var zone in map.Zones)
        {
            if (!zones.Add(zone.Id))
            {
                error(MapErrorCodes.ZoneDuplicate, zone.Id, $"Zone '{zone.Id}' is listed twice.");
                continue;
            }

            foreach (var problem in ContentValidator.Check(zone))
            {
                error(MapErrorCodes.ZoneInvalid, zone.Id, $"{problem.Path}: {problem.Message}");
            }

            // D-307: zones change the dice by a number of dice or points; the rest of the pipeline comes with items (stage 4)
            if (zone.DiceModifier is { } modifier
                && (modifier.Stage is not (DiceStage.Count or DiceStage.Add) || modifier.Value.Kind != ContentValueKind.Number))
            {
                error(MapErrorCodes.ZoneUnsupported, zone.Id, "A zone changes the dice by «count» or «add» with a number in this build.");
            }
        }

        foreach (var cell in cells.Values.Where(c => c.Zone is not null && !zones.Contains(c.Zone)))
        {
            error(MapErrorCodes.ZoneUnknown, cell.Id, $"Zone '{cell.Zone}' is not on the map.");
        }
    }
}

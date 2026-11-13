using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Map;

/// <summary>
/// A player's own walk forward (D-304): the cells entered, and — when it reached a fork with steps left — the steps still
/// to walk after the branch is chosen (<see cref="Remaining"/> &gt; 0, then the walk stands on the fork).
/// </summary>
public sealed record Walk(IReadOnlyList<string> Path, int Remaining)
{
    public bool Paused => Remaining > 0;
}

/// <summary>Movement along the map graph.</summary>
public static class Movement
{
    /// <summary>
    /// The player's own walk of <paramref name="steps"/> from <paramref name="from"/> (D-304): along the only exit of each
    /// cell; at a fork with steps left it stops and waits for the branch, unless <paramref name="branch"/> — the cell the
    /// player chose — is the first step out of it. The finish is a stop cell: extra steps burn.
    /// </summary>
    public static Walk WalkOwn(MapGraph map, string from, int steps, string? branch = null)
    {
        ArgumentNullException.ThrowIfNull(map);

        var path = new List<string>();
        var current = map.CellById(from);
        for (var i = 0; i < steps && current.Type != CellType.Finish; i++)
        {
            var exits = map.Exits(current.Id);
            var chosen = i == 0 && branch is not null;
            if (!chosen && exits.Count > 1)
            {
                return new Walk(path, steps - i);
            }

            if (!chosen && exits.Count == 0)
            {
                break;
            }

            // One expression, no local assigned in branches: mutation testing can mutate this method (D-281)
            var next = chosen
                ? exits.FirstOrDefault(e => e.To == branch)?.To
                    ?? throw new ArgumentException($"'{branch}' is not a branch out of '{current.Id}'.", nameof(branch))
                : exits[0].To;

            current = map.CellById(next);
            path.Add(current.Id);
        }

        return new Walk(path, 0);
    }

    /// <summary>
    /// Cells entered, in order, on <paramref name="steps"/> forced forward steps from <paramref name="from"/>
    /// along default forward edges: a fork is passed by its default branch (SPEC «Чужой толчок через развилку», D-304).
    /// The finish is a stop cell: extra steps burn.
    /// </summary>
    public static IReadOnlyList<string> Forward(MapGraph map, string from, int steps)
    {
        ArgumentNullException.ThrowIfNull(map);

        var path = new List<string>();
        var current = map.CellById(from);
        for (var i = 0; i < steps && current.Type != CellType.Finish; i++)
        {
            var edge = map.Edges.FirstOrDefault(e => e.From == current.Id && e.IsDefaultForward);
            if (edge is null)
            {
                break;
            }

            current = map.CellById(edge.To);
            path.Add(current.Id);
        }

        return path;
    }

    /// <summary>
    /// Cells entered, in order, on a forced move of <paramref name="steps"/> forward from <paramref name="from"/> that is not
    /// a run's own move — a correction now, pushes of effects and items later. Only a run's own move reaches the finish
    /// (SPEC «Движение», D-322): a push that would enter it stops on the cell it passed just before, the previous cell of
    /// its own path; from the cell before the finish it enters nothing. Forks are passed by their default branch.
    /// </summary>
    public static IReadOnlyList<string> Push(MapGraph map, string from, int steps)
    {
        var path = Forward(map, from, steps);
        return path.Count > 0 && map.CellById(path[^1]).Type == CellType.Finish ? [.. path.Take(path.Count - 1)] : path;
    }

    /// <summary>
    /// Cells entered, in order, on <paramref name="steps"/> steps back from where <paramref name="path"/> stands:
    /// first back along the walked edges of the last segment, then along primary backward edges (the edge a cell
    /// is entered by when history runs out). Never past the start: missing steps are lost (M2, RR3). A push
    /// (<paramref name="checkpoints"/>) never leaves a checkpoint either: it stops on the first one (D-306).
    /// </summary>
    public static IReadOnlyList<string> Backward(MapGraph map, PlayerPath path, int steps, bool checkpoints = false)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(path);

        var entered = new List<string>();
        var walked = path.Segments[^1].Cells.ToList();
        var current = map.CellById(path.Current);
        for (var i = 0; i < steps && current.Type != CellType.Start && !(checkpoints && current.Type == CellType.Checkpoint); i++)
        {
            string? previous;
            if (walked.Count >= 2)
            {
                walked.RemoveAt(walked.Count - 1);
                previous = walked[^1];
            }
            else
            {
                previous = PrimaryBackward(map, current.Id)?.From;
            }

            if (previous is null)
            {
                break;
            }

            current = map.CellById(previous);
            entered.Add(current.Id);
        }

        return entered;
    }

    // The editor requires a primary edge on cells with several inputs; a single input needs no mark.
    private static Edge? PrimaryBackward(MapGraph map, string cellId)
    {
        var incoming = map.Edges.Where(e => e.To == cellId).ToList();

        // Stryker disable once NullCoalescing : equivalent — a single incoming edge is the answer whether or not it is marked primary
        return incoming.FirstOrDefault(e => e.IsPrimaryBackward) ?? (incoming.Count == 1 ? incoming[0] : null);
    }

    /// <summary>
    /// The trigger points of a move, in path order: for each entered cell a <see cref="CellVisitKind.MoveStep"/>,
    /// then <see cref="CellVisitKind.Pass"/>, or <see cref="CellVisitKind.Stop"/> for the last cell — unless the move is
    /// paused at a fork, where the fork is passed (D-304). A transfer (<c>Steps == 0</c>) has none, and a move that entered
    /// no cell (blocked at the finish or the start) stops nowhere.
    /// </summary>
    public static IReadOnlyList<CellVisit> Visits(PlayerMoved moved)
    {
        ArgumentNullException.ThrowIfNull(moved);

        if (moved.Steps == 0)
        {
            return [];
        }

        var visits = new List<CellVisit>(moved.Path.Count * 2);
        for (var i = 0; i < moved.Path.Count; i++)
        {
            visits.Add(new CellVisit(moved.Path[i], CellVisitKind.MoveStep));
            visits.Add(new CellVisit(moved.Path[i], i == moved.Path.Count - 1 && !moved.Paused ? CellVisitKind.Stop : CellVisitKind.Pass));
        }

        return visits;
    }

    internal static SeasonState Apply(SeasonState state, PlayerMoved e)
    {
        var player = state.Players[e.PlayerId];

        // D-321: the run whose own move last placed the player — kept by the teleport that move stopped on and by the
        // run's own corrections right after it (items changing its throw after it was made among them, D-421), lost on
        // any other move
        var lastMoveRunId = e.Reason switch
        {
            MoveReason.CompletionRoll => e.RunId,
            MoveReason.Teleport => player.LastMoveRunId,
            MoveReason.RunCorrection or MoveReason.DiceModified when e.RunId == player.LastMoveRunId => e.RunId,
            _ => null,
        };
        state = state with
        {
            Players = state.Players.SetItem(e.PlayerId, player with { CellId = e.To, Path = player.Path.After(e), LastMoveRunId = lastMoveRunId }),
        };

        // What the run's moves brought the player closer to the finish, its teleport included (D-321)
        var gainRunId = e.Reason switch
        {
            MoveReason.CompletionRoll or MoveReason.RunCorrection or MoveReason.DiceModified => e.RunId,
            MoveReason.Teleport => player.LastMoveRunId,
            _ => null,
        };
        // A coop partner's moves of the run are his side of it (D-545)
        if (gainRunId is { } gaining && state.Runs.TryGetValue(gaining, out var whole))
        {
            var gainer = whole.Side(e.PlayerId);
            gainer = gainer with
            {
                Gain = gainer.Gain + MapDistances.Closer(state.Map, e),
                MoveFrom = gainer.MoveFrom ?? (e.Reason == MoveReason.CompletionRoll ? e.From : null),
            };
            state = state with { Runs = state.Runs.SetItem(gaining, whole.WithSide(e.PlayerId, gainer)) };
        }

        if (e.RunId is not { } runId)
        {
            return state;
        }

        // The run keeps the cells it really moved the token (steps past the finish burn, D-47), which a reject or a
        // correction takes back, and whether its latest move stands on the finish (its proof goes on top).
        var run = state.Runs[runId];
        var side = run.Side(e.PlayerId);
        var moved = side.Moved + (Math.Sign(e.Steps) * e.Path.Count);
        var reached = state.Map.CellById(e.To).Type == CellType.Finish;
        return state with { Runs = state.Runs.SetItem(runId, run.WithSide(e.PlayerId, side with { Moved = moved, ReachedFinish = reached })) };
    }
}

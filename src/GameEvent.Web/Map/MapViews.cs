using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Map;

/// <summary>
/// A cell of the season's map (D-300, D-302): its type and the parameters of that type (<c>to</c> of a teleport,
/// <c>amount</c> of a points bonus, <c>deck</c> of an event, <c>grants</c> of a shop), its zone and where the editor
/// placed it (<c>x</c>, <c>y</c>; none on a linear map). The same shape goes back to the editor's check and publication.
/// </summary>
public sealed record CellView(
    string Id,
    CellType Type,
    string? Zone = null,
    string? To = null,
    int? Amount = null,
    string? Deck = null,
    string? Grants = null,
    decimal? X = null,
    decimal? Y = null);

/// <summary>
/// An arrow of the map: <c>isDefaultForward</c> — the default branch (every exit of a plain cell, one exit of a fork);
/// <c>isPrimaryBackward</c> — the main incoming edge a move back takes into a cell with several entries.
/// </summary>
public sealed record EdgeView(string From, string To, bool IsDefaultForward, bool IsPrimaryBackward);

/// <summary>A zone's roll filter (CONTENT.md «Зона»): any of the tags, hours between, released before the year.</summary>
public sealed record ZoneFilterView(IReadOnlyList<string>? Tags = null, decimal? MinHours = null, decimal? MaxHours = null, int? ReleaseYearBefore = null);

/// <summary>A zone's change of the completion dice (D-307): <c>count</c> — more dice (less when negative), <c>add</c> — to the sum.</summary>
public sealed record ZoneDiceView(DiceStage Stage, int Value);

/// <summary>
/// A zone of the map and its rules (CONTENT.md «Зона», D-307): the roll filter while a player stands in it, the dice
/// change and the drop penalty multiplier fixed at the roll; the deck and the shop prices wait for their mechanics.
/// </summary>
public sealed record ZoneView(
    string Id,
    string Name,
    ZoneFilterView? RollFilter = null,
    ZoneDiceView? DiceModifier = null,
    decimal? DropPenaltyMultiplier = null,
    string? Deck = null,
    decimal? ShopPriceMultiplier = null);

/// <summary>A whole map: what the editor loads, checks and publishes.</summary>
public sealed record MapGraphView(IReadOnlyList<CellView>? Cells, IReadOnlyList<EdgeView>? Edges, IReadOnlyList<ZoneView>? Zones);

/// <summary>
/// One leg of my latest move: the cells entered from <c>from</c> in order. <c>reason</c> <c>teleport</c> is a transfer,
/// not a walk (D-303); a leg that stopped at a fork with steps left is followed by a branch choice (D-304).
/// </summary>
public sealed record MoveLegView(string From, IReadOnlyList<string> Path, MoveReason Reason);

/// <summary>
/// My latest move, from the log: the legs of the one command that moved my token last, so the page walks the token
/// along the real branch and teleport. <c>sequence</c> tells one move from the next.
/// </summary>
public sealed record MoveView(long Sequence, IReadOnlyList<MoveLegView> Legs);

/// <summary>The season's map as the log has it now, and conversions between the engine's map and the API's.</summary>
public static class SeasonMaps
{
    private static readonly string s_published = EventCatalog.Describe(typeof(MapPublished)).Name;
    private static readonly string s_moved = EventCatalog.Describe(typeof(PlayerMoved)).Name;

    /// <summary>How many recent moves of the season are read to find my latest one.</summary>
    private const int RecentMoves = 64;

    /// <summary>The latest published map (D-300: publication is never undone), or the one the season was created with.</summary>
    public static async Task<MapGraph> CurrentAsync(GameEventDbContext db, Guid seasonId, MapGraph created, CancellationToken ct)
    {
        var row = await db.Events.AsNoTracking()
            .Where(e => e.SeasonId == seasonId && e.Type == s_published)
            .OrderByDescending(e => e.Sequence)
            .FirstOrDefaultAsync(ct);
        return row is not null && EventCodec.Decode(new StoredEvent(row.Type, row.Version, row.Data)) is MapPublished published
            ? published.Map
            : created;
    }

    /// <summary>
    /// The legs of the latest command that moved <paramref name="playerId"/>, among the season's recent moves (found by
    /// the type, never by the JSON data — invariant 9); null when the player has not moved lately.
    /// </summary>
    public static async Task<MoveView?> LastMoveAsync(GameEventDbContext db, Guid seasonId, Guid playerId, CancellationToken ct)
    {
        var rows = await db.Events.AsNoTracking()
            .Where(e => e.SeasonId == seasonId && e.Type == s_moved && e.UndoneByEventId == null)
            .OrderByDescending(e => e.Sequence)
            .Take(RecentMoves)
            .ToListAsync(ct);
        var moves = rows
            .Select(r => (Row: r, Move: EventCodec.Decode(new StoredEvent(r.Type, r.Version, r.Data)) as PlayerMoved))
            .Where(x => x.Move?.PlayerId == playerId)
            .ToList();
        if (moves.Count == 0)
        {
            return null;
        }

        var command = moves[0].Row.CommandId;
        var legs = moves.Where(x => x.Row.CommandId == command).OrderBy(x => x.Row.Sequence).ToList();
        return new MoveView(
            legs[^1].Row.Sequence,
            [.. legs.Select(x => new MoveLegView(x.Move!.From, [.. x.Move.Path], x.Move.Reason))]);
    }

    public static CellView ToView(Cell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return new(cell.Id, cell.Type, cell.Zone, cell.To, cell.Amount, cell.Deck, cell.Grants, cell.X, cell.Y);
    }

    public static EdgeView ToView(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return new(edge.From, edge.To, edge.IsDefaultForward, edge.IsPrimaryBackward);
    }

    public static ZoneView ToView(ZoneDefinition zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return new(
            zone.Id,
            zone.Name,
            zone.RollFilter is { } f ? new ZoneFilterView(f.Tags is { } tags ? [.. tags] : null, f.MinHours, f.MaxHours, f.ReleaseYearBefore) : null,
            // A published zone changes the dice by a number only (MapValidator, D-307)
            zone.DiceModifier is { Value.Kind: ContentValueKind.Number } d ? new ZoneDiceView(d.Stage, d.Value.Number) : null,
            zone.DropPenaltyMultiplier,
            zone.Deck,
            zone.ShopPriceMultiplier);
    }

    public static MapGraphView ToView(MapGraph map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return new([.. map.Cells.Select(ToView)], [.. map.Edges.Select(ToView)], [.. map.Zones.Select(ToView)]);
    }

    /// <summary>The engine's map of a request; null when a list or an entry is missing (the request is malformed).</summary>
    public static MapGraph? FromView(MapGraphView? view)
    {
        if (view?.Cells is not { } cells || view.Edges is not { } edges || cells.Any(c => c is null) || edges.Any(e => e is null)
            || view.Zones?.Any(z => z is null || z.RollFilter?.Tags?.Any(t => t is null) == true) == true)
        {
            return null;
        }

        return new MapGraph(
            [.. cells.Select(c => new Cell(c.Id ?? "", c.Type)
            {
                Zone = c.Zone,
                To = c.To,
                Amount = c.Amount,
                Deck = c.Deck,
                Grants = c.Grants,
                X = c.X,
                Y = c.Y,
            })],
            [.. edges.Select(e => new Edge(e.From ?? "", e.To ?? "", e.IsDefaultForward, e.IsPrimaryBackward))])
        {
            Zones = [.. (view.Zones ?? []).Select(z => new ZoneDefinition
            {
                Id = z.Id ?? "",
                Name = z.Name ?? "",
                RollFilter = z.RollFilter is { } f
                    ? new GameFilterSpec
                    {
                        Tags = f.Tags is { } tags ? [.. tags] : null,
                        MinHours = f.MinHours,
                        MaxHours = f.MaxHours,
                        ReleaseYearBefore = f.ReleaseYearBefore,
                    }
                    : null,
                DiceModifier = z.DiceModifier is { } d ? new DiceModifierSpec { Stage = d.Stage, Value = ContentValue.Of(d.Value) } : null,
                DropPenaltyMultiplier = z.DropPenaltyMultiplier,
                Deck = z.Deck,
                ShopPriceMultiplier = z.ShopPriceMultiplier,
            })],
        };
    }
}

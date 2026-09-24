using System.Collections.Immutable;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;

namespace GameEvent.Engine.Seasons;

/// <summary>
/// State of one season: a pure fold of its event log. Compares by content,
/// so replaying the log can be checked against the stored state with a plain equality.
/// </summary>
public sealed record SeasonState(
    Guid SeasonId,
    MapGraph Map,
    ImmutableSortedDictionary<Guid, PlayerState> Players,
    ImmutableSortedDictionary<Guid, RunState> Runs)
{
    public static SeasonState Empty { get; } =
        new(Guid.Empty, new MapGraph([], []), ImmutableSortedDictionary<Guid, PlayerState>.Empty, ImmutableSortedDictionary<Guid, RunState>.Empty);

    public bool IsCreated => SeasonId != Guid.Empty;

    public bool Equals(SeasonState? other) =>
        other is not null
        && SeasonId == other.SeasonId
        && Map == other.Map
        && Players.SequenceEqual(other.Players)
        && Runs.SequenceEqual(other.Runs);

    public override int GetHashCode() => HashCode.Combine(SeasonId, Players.Count, Runs.Count);
}

/// <summary>Where the player is in the turn cycle. Moving and resolving happen inside one command.</summary>
public enum TurnPhase
{
    Idle,
    Rolling,
    Playing,
}

/// <summary>A player's standing in the season. Points and position are independent measures.</summary>
public sealed record PlayerState(
    Guid PlayerId,
    string Name,
    string CellId,
    int Points,
    TurnPhase Phase,
    RollOffer? Offer,
    Guid? ActiveRunId);

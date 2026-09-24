using System.Collections.Immutable;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;

namespace GameEvent.Engine.Seasons;

/// <summary>
/// State of one season: a pure fold of its event log. Compares by content,
/// so replaying the log can be checked against the stored state with a plain equality.
/// </summary>
public sealed record SeasonState(
    Guid SeasonId,
    string Name,
    SeasonStatus Status,
    DateTimeOffset? Deadline,
    Ruleset? Ruleset,
    int RulesetVersion,
    MapGraph Map,
    ImmutableSortedDictionary<Guid, SeasonPlayer> Players,
    ImmutableSortedDictionary<Guid, RunState> Runs)
{
    public static SeasonState Empty { get; } =
        new(Guid.Empty, Name: "", SeasonStatus.Draft, Deadline: null, Ruleset: null, RulesetVersion: 0, new MapGraph([], []), ImmutableSortedDictionary<Guid, SeasonPlayer>.Empty, ImmutableSortedDictionary<Guid, RunState>.Empty);

    public bool IsCreated => SeasonId != Guid.Empty;

    /// <summary>The rules in force; only valid on a created season.</summary>
    public Ruleset Rules => Ruleset ?? throw new InvalidOperationException("The season is not created yet.");

    public bool Equals(SeasonState? other) =>
        other is not null
        && SeasonId == other.SeasonId
        && Name == other.Name
        && Status == other.Status
        && Deadline == other.Deadline
        && Ruleset == other.Ruleset
        && RulesetVersion == other.RulesetVersion
        && Map == other.Map
        && Players.SequenceEqual(other.Players)
        && Runs.SequenceEqual(other.Runs);

    public override int GetHashCode() => HashCode.Combine(SeasonId, Players.Count, Runs.Count);
}

/// <summary>Season lifecycle (GLOSSARY «Статус сезона»): draft → active → closing → finished → archived.</summary>
public enum SeasonStatus
{
    Draft,
    Active,
    Closing,
    Finished,
    Archived,
}

/// <summary>Where the player is in the turn cycle. Moving and resolving happen inside one command.</summary>
public enum TurnPhase
{
    Idle,
    Rolling,
    Playing,
}

/// <summary>
/// A player's standing in the season. Points and position are independent measures. Points and coins are
/// fields (the leaderboard sorts by them); any other resource lives in <see cref="Resources"/> (invariant 9).
/// </summary>
public sealed record SeasonPlayer(
    Guid PlayerId,
    Guid UserId,
    string Name,
    string CellId,
    int Points,
    int Coins,
    ResourceBag Resources,
    bool IsInactive,
    TurnPhase Phase,
    RollOffer? Offer,
    Guid? ActiveRunId);

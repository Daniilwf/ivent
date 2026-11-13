using System.Collections.Immutable;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Turns;

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
    ImmutableSortedDictionary<Guid, RunState> Runs,
    ImmutableSortedDictionary<Guid, PendingManualEffect> ManualEffects,
    int FinishesSoFar = 0,
    long PointsChanges = 0,
    EquatableArray<Ranking.LeaderboardRow>? Result = null,
    GameEvent.Engine.Content.ContentCatalog? Content = null,
    int MapVersion = 0)
{
    // MapVersion: how many times the admin published the map (0 — the map the season was created with); a draft names
    // the version it was made from, so a publication over a newer one is refused (K1A, D-616)

    /// <summary>The season's content (D-400): empty until the admin publishes a pack.</summary>
    public GameEvent.Engine.Content.ContentCatalog Catalog => Content ?? GameEvent.Engine.Content.ContentCatalog.Empty;

    /// <summary>The weekly challenges set in the season (D-526), by id.</summary>
    public ImmutableSortedDictionary<Guid, Challenges.WeeklyChallengeState> WeeklyChallenges { get; init; } =
        ImmutableSortedDictionary<Guid, Challenges.WeeklyChallengeState>.Empty;

    /// <summary>The suggestions sent in the season (D-527), by id.</summary>
    public ImmutableSortedDictionary<Guid, Suggestions.Suggestion> Suggestions { get; init; } =
        ImmutableSortedDictionary<Guid, Suggestions.Suggestion>.Empty;

    /// <summary>The settlements for everyone waiting for their batches (D-537), by id; one leaves when its last batch is done.</summary>
    public ImmutableSortedDictionary<Guid, ForAllSettlement> ForAll { get; init; } =
        ImmutableSortedDictionary<Guid, ForAllSettlement>.Empty;

    public static SeasonState Empty { get; } =
        new(
            Guid.Empty,
            Name: "",
            SeasonStatus.Draft,
            Deadline: null,
            Ruleset: null,
            RulesetVersion: 0,
            new MapGraph([], []),
            ImmutableSortedDictionary<Guid, SeasonPlayer>.Empty,
            ImmutableSortedDictionary<Guid, RunState>.Empty,
            ImmutableSortedDictionary<Guid, PendingManualEffect>.Empty);

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
        && Runs.SequenceEqual(other.Runs)
        && ManualEffects.SequenceEqual(other.ManualEffects)
        && FinishesSoFar == other.FinishesSoFar
        && PointsChanges == other.PointsChanges
        && Result == other.Result
        && Catalog == other.Catalog
        && WeeklyChallenges.SequenceEqual(other.WeeklyChallenges)
        && Suggestions.SequenceEqual(other.Suggestions)
        && ForAll.SequenceEqual(other.ForAll)
        && MapVersion == other.MapVersion;

    // Explicit type arguments: a mutated count still compiles, so mutation testing keeps this method (D-281)
    public override int GetHashCode() => HashCode.Combine<Guid, int, int>(SeasonId, Players.Count, Runs.Count);
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
/// <see cref="RerollsThisRoll"/> counts rerolls since the last roll from Idle: 0 whenever the player is not Rolling.
/// <see cref="PointsTick"/> is when the points last changed, as the number of the season's points change (1, 2, 3…;
/// 0 — never): the «earliest final score» tiebreaker (D-100). <see cref="LastMoveRunId"/> — the run whose own move (with
/// the teleport it stopped on and its own corrections right after) last changed the position; null after any other move
/// (D-321: a reject returns a player who has not moved since to the cell before that move).
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
    PlayerPath Path,
    TurnPhase Phase,
    RollOffer? Offer,
    PendingChoice? Choice,
    EquatableArray<GameExclusion> Exclusions,
    int RerollsThisRoll,
    Finish.FinishState? Finish,
    Guid? ActiveRunId,
    long PointsTick = 0,

    // Written only when set: undo snapshots of players made before it keep their format (D-321)
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? LastMoveRunId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Inventory.PlayerEconomy? Economy = null,

    // Written only when an effect waits: snapshots of players made before stage 5 keep their format (D-511)
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    EquatableArray<Effects.SuspendedEffect> Suspended = default)
{
    /// <summary>The player's economy (D-401), empty when they have none.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Inventory.PlayerEconomy Wallet => Economy ?? Inventory.PlayerEconomy.Empty;
}

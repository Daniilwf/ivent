using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Players;

/// <summary>
/// Add a user to the season as a player (while the season is a draft or running). <c>PlayerId</c> is the
/// participation id; a user takes part in a season at most once. A player joining mid-season gets the cell,
/// points and coins the admin sets (SE4); by default the start cell and zeros. Starting points and coins are
/// logged as changes of their own, so points always equal the sum of logged changes (invariant 2).
/// </summary>
public sealed record AddSeasonPlayer(
    Guid PlayerId,
    Guid UserId,
    string Name,
    string? CellId = null,
    int Points = 0,
    int Coins = 0) : ICommand;

/// <summary>The admin marks a player inactive (out of the active game) or back (SE5). Random targets skip inactive players.</summary>
public sealed record SetPlayerInactive(Guid PlayerId, bool IsInactive) : ICommand;

/// <summary>
/// The admin corrects a player (D-21): position, points, coins, other resources, and dropping an offered game
/// (Rolling → Idle). Every change is a logged event with a reason; <see cref="Comment"/> explains it in the log.
/// A player playing a run is not reset here: that is a drop or tech reroll (C6).
/// </summary>
public sealed record AdjustPlayer(
    Guid PlayerId,
    string Comment,
    string? CellId = null,
    int PointsDelta = 0,
    int CoinsDelta = 0,
    EquatableArray<ResourceDelta> ResourceDeltas = default,
    bool DiscardOffer = false) : ICommand;

public sealed record ResourceDelta(string Resource, int Delta);

[EventType("season-player-added")]
public sealed record SeasonPlayerAdded(Guid PlayerId, Guid UserId, string Name, string CellId) : IGameEvent;

[EventType("player-inactivity-set")]
public sealed record PlayerInactivitySet(Guid PlayerId, bool IsInactive) : IGameEvent;

/// <summary>Why the admin changed a player: shown in the public log next to the changes it caused.</summary>
[EventType("player-adjusted")]
public sealed record PlayerAdjusted(Guid PlayerId, string Comment) : IGameEvent;

/// <summary>An offered game was taken back without a reroll (admin reset): the player is Idle, the game free again.</summary>
[EventType("offer-discarded")]
public sealed record OfferDiscarded(Guid PlayerId, Guid GameId) : IGameEvent;

internal static class PlayerAdministration
{
    public static Decision Decide(SeasonState state, AddSeasonPlayer command) =>
        throw new NotImplementedException("C2");

    public static Decision Decide(SeasonState state, SetPlayerInactive command) =>
        throw new NotImplementedException("C2");

    public static Decision Decide(SeasonState state, AdjustPlayer command) =>
        throw new NotImplementedException("C2");

    public static SeasonState Apply(SeasonState state, SeasonPlayerAdded e) =>
        state with
        {
            Players = state.Players.Add(
                e.PlayerId,
                new SeasonPlayer(
                    e.PlayerId, e.UserId, e.Name, e.CellId, Points: 0, Coins: 0, ResourceBag.Empty, IsInactive: false,
                    TurnPhase.Idle, Offer: null, ActiveRunId: null)),
        };

    public static SeasonState Apply(SeasonState state, PlayerInactivitySet e) =>
        state with { Players = state.Players.SetItem(e.PlayerId, state.Players[e.PlayerId] with { IsInactive = e.IsInactive }) };

    public static SeasonState Apply(SeasonState state, PlayerAdjusted e) => state;

    public static SeasonState Apply(SeasonState state, OfferDiscarded e) =>
        state with
        {
            Players = state.Players.SetItem(e.PlayerId, state.Players[e.PlayerId] with { Phase = TurnPhase.Idle, Offer = null }),
        };
}

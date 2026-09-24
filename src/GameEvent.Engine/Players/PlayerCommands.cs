using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;

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
    // Safety ceilings, not balance (like D-86): a typo must not overflow a balance.
    public const int MaxDelta = 1_000_000;
    public const int MaxCommentLength = 500;

    // Points and coins are fields, not dictionary entries (invariant 9).
    private static readonly HashSet<string> s_reservedResources = new(StringComparer.OrdinalIgnoreCase) { "points", "coins" };

    public static Decision Decide(SeasonState state, AddSeasonPlayer command)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (state.Status is not (SeasonStatus.Draft or SeasonStatus.Active))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"Players join a draft or running season; it is {state.Status}.");
        }

        if (state.Players.ContainsKey(command.PlayerId) || state.Players.Values.Any(p => p.UserId == command.UserId))
        {
            return Decision.Reject(RejectionCodes.PlayerAlreadyAdded, $"Player {command.PlayerId} or user {command.UserId} is already in the season.");
        }

        var start = state.Map.Start.Id;
        var cell = command.CellId ?? start;
        if (TransferTarget(state, cell) is { } badCell)
        {
            return badCell;
        }

        if (Math.Abs((long)command.Points) > MaxDelta || Math.Abs((long)command.Coins) > MaxDelta)
        {
            return Decision.Reject(RejectionCodes.DeltaTooLarge, $"Starting balances are limited to ±{MaxDelta}.");
        }

        // Everyone enters on the start cell; the admin's starting cell and balances are logged changes of their own.
        var events = new List<IGameEvent> { new SeasonPlayerAdded(command.PlayerId, command.UserId, command.Name, start) };
        if (cell != start)
        {
            events.Add(Transfer(command.PlayerId, start, cell, MoveReason.StartingCell));
        }

        if (command.Points != 0)
        {
            events.Add(new PointsChanged(command.PlayerId, command.Points, PointsReason.StartingBalance, RunId: null));
        }

        if (command.Coins != 0)
        {
            events.Add(new CoinsChanged(command.PlayerId, command.Coins, CoinsReason.StartingBalance, RunId: null));
        }

        return Decision.Accept(events);
    }

    public static Decision Decide(SeasonState state, SetPlayerInactive command)
    {
        if (Find(state, command.PlayerId) is not { } player)
        {
            return Unknown(state, command.PlayerId);
        }

        if (SeasonSetup.IsOver(state))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: results are fixed.");
        }

        return player.IsInactive == command.IsInactive
            ? Decision.Reject(RejectionCodes.NothingToChange, $"The player is already {(command.IsInactive ? "inactive" : "active")}.")
            : Decision.Accept(new PlayerInactivitySet(player.PlayerId, command.IsInactive));
    }

    public static Decision Decide(SeasonState state, AdjustPlayer command)
    {
        if (Find(state, command.PlayerId) is not { } player)
        {
            return Unknown(state, command.PlayerId);
        }

        if (SeasonSetup.IsOver(state))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: results are fixed.");
        }

        if (string.IsNullOrWhiteSpace(command.Comment))
        {
            return Decision.Reject(RejectionCodes.CommentRequired, "Every admin adjustment explains itself in the public log.");
        }

        if (command.Comment.Length > MaxCommentLength)
        {
            return Decision.Reject(RejectionCodes.CommentTooLong, $"The comment is limited to {MaxCommentLength} characters.");
        }

        var resources = command.ResourceDeltas.ToList();
        if (resources.Any(r => r is null || string.IsNullOrWhiteSpace(r.Resource) || s_reservedResources.Contains(r.Resource.Trim()))
            || resources.Select(r => r.Resource).Distinct(StringComparer.Ordinal).Count() != resources.Count)
        {
            return Decision.Reject(RejectionCodes.InvalidResource, "Resource names must be non-empty, unique, and not points or coins.");
        }

        if (new[] { command.PointsDelta, command.CoinsDelta }.Concat(resources.Select(r => r.Delta)).Any(d => Math.Abs((long)d) > MaxDelta))
        {
            return Decision.Reject(RejectionCodes.DeltaTooLarge, $"One adjustment changes a balance by at most ±{MaxDelta}.");
        }

        if (command.DiscardOffer && player.Phase == TurnPhase.Playing)
        {
            return Decision.Reject(RejectionCodes.PlayerBusy, "The player is playing a run: that is a drop or a tech reroll (C6).");
        }

        if (command.CellId is { } target && TransferTarget(state, target) is { } badCell)
        {
            return badCell;
        }

        var changes = new List<IGameEvent>();
        if (command.CellId is { } cell && cell != player.CellId)
        {
            changes.Add(Transfer(player.PlayerId, player.CellId, cell, MoveReason.AdminAdjustment));
        }

        if (command.PointsDelta != 0)
        {
            changes.Add(new PointsChanged(player.PlayerId, command.PointsDelta, PointsReason.AdminAdjustment, RunId: null));
        }

        if (command.CoinsDelta != 0)
        {
            changes.Add(new CoinsChanged(player.PlayerId, command.CoinsDelta, CoinsReason.AdminAdjustment, RunId: null));
        }

        changes.AddRange(command.ResourceDeltas
            .Where(r => r.Delta != 0)
            .Select(r => new ResourceChanged(player.PlayerId, r.Resource, r.Delta, ResourceReason.AdminAdjustment)));

        if (command.DiscardOffer && player is { Phase: TurnPhase.Rolling, Offer: { } offer })
        {
            changes.Add(new OfferDiscarded(player.PlayerId, offer.GameId));
        }

        if (command.DiscardOffer && player is { Phase: TurnPhase.Rolling, Choice: { } choice })
        {
            changes.Add(new ChoiceDiscarded(player.PlayerId, choice.ChoiceId));
        }

        return changes.Count == 0
            ? Decision.Reject(RejectionCodes.NothingToChange, "The adjustment changes nothing.")
            : Decision.Accept([new PlayerAdjusted(player.PlayerId, command.Comment), .. changes]);
    }

    /// <summary>
    /// A transfer by the admin lands on an existing cell and never on the finish: reaching the finish is a game event
    /// with places and bonuses (C9), not an administrative move. A transfer does not trigger the cell (D-89).
    /// </summary>
    private static Decision? TransferTarget(SeasonState state, string cellId)
    {
        var cell = state.Map.Cells.FirstOrDefault(c => c.Id == cellId);
        if (cell is null)
        {
            return Decision.Reject(RejectionCodes.CellUnknown, $"Cell '{cellId}' is not on the map.");
        }

        return cell.Type == CellType.Finish
            ? Decision.Reject(RejectionCodes.TransferToFinish, "Players reach the finish by playing, not by a transfer.")
            : null;
    }

    /// <summary>A move by transfer, not by steps: the path is the destination only.</summary>
    private static PlayerMoved Transfer(Guid playerId, string from, string to, MoveReason reason) =>
        new(playerId, from, to, Steps: 0, [to], reason, RunId: null);

    private static SeasonPlayer? Find(SeasonState state, Guid playerId) =>
        state.IsCreated && state.Players.TryGetValue(playerId, out var player) ? player : null;

    private static Decision Unknown(SeasonState state, Guid playerId) =>
        state.IsCreated
            ? Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {playerId} is not in the season.")
            : Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");

    public static SeasonState Apply(SeasonState state, SeasonPlayerAdded e) =>
        state with
        {
            Players = state.Players.Add(
                e.PlayerId,
                new SeasonPlayer(
                    e.PlayerId, e.UserId, e.Name, e.CellId, Points: 0, Coins: 0, ResourceBag.Empty, IsInactive: false,
                    PlayerPath.At(e.CellId), TurnPhase.Idle, Offer: null, Choice: null, ActiveRunId: null)),
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

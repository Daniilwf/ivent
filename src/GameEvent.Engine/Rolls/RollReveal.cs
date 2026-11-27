using System.Buffers.Binary;
using System.Security.Cryptography;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Rolls;

/// <summary>The stable identity and disclosure deadline of the saved roll result.</summary>
public sealed record RollReveal(Guid Id, DateTimeOffset ExpiresAt)
{
    /// <summary>The maximum time a new roll stays hidden without its owner's acknowledgement.</summary>
    public static TimeSpan DisclosureDelay { get; } = TimeSpan.FromMinutes(2);
}

/// <summary>The owner opens the game selected by the server in their current roll.</summary>
public sealed record ShowRolledGame(Guid PlayerId, Guid RevealId) : ICommand;

/// <summary>The owner or timer opened this roll. The original roll event keeps the saved result.</summary>
[EventType("roll-game-shown")]
public sealed record RollGameShown(Guid PlayerId, Guid RevealId) : IGameEvent;

internal static class RollReveals
{
    private const int MaxGameSectors = 12;

    public static RollReveal Create(Guid rollId, DateTimeOffset rolledAt) => new(rollId, rolledAt + RollReveal.DisclosureDelay);

    public static RollReveal? PendingOf(SeasonPlayer player) => player.Offer?.Reveal ?? player.Choice?.Reveal;

    public static bool IsUnrevealedReservation(SeasonState state, RollMiss miss, DateTimeOffset now) =>
        miss.Reason == RollMissReason.BeingPlayed
        && state.Players.TryGetValue(miss.ByPlayerId, out var holder)
        && ((holder.Offer is { Reveal.ExpiresAt: var offerExpiry } offer && offer.GameId == miss.GameId && offerExpiry > now)
            || (holder.Choice is { Reveal.ExpiresAt: var choiceExpiry } choice && choiceExpiry > now
                && choice.Options.Any(option => option.Game?.GameId == miss.GameId)));

    public static Decision Decide(SeasonState state, ShowRolledGame command)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (state.Status is not (SeasonStatus.Active or SeasonStatus.Closing))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: no roll to show.");
        }

        if (!state.Players.TryGetValue(command.PlayerId, out var player))
        {
            return Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {command.PlayerId} is not in the season.");
        }

        return PendingOf(player)?.Id == command.RevealId
            ? Decision.Accept(new RollGameShown(command.PlayerId, command.RevealId))
            : Decision.Reject(RejectionCodes.TurnStale, "The roll is no longer pending disclosure.");
    }

    public static SeasonState Apply(SeasonState state, RollGameShown e)
    {
        var player = state.Players[e.PlayerId];
        var shown = player switch
        {
            { Offer.Reveal.Id: var id } when id == e.RevealId => player with { Offer = player.Offer with { Reveal = null } },
            { Choice.Reveal.Id: var id } when id == e.RevealId => player with { Choice = player.Choice with { Reveal = null } },
            _ => throw new InvalidOperationException($"Roll {e.RevealId} is not pending disclosure for player {e.PlayerId}."),
        };
        return state with { Players = state.Players.SetItem(e.PlayerId, shown) };
    }

    /// <summary>The saved display wheel. Offer and miss selection has already used the gameplay RNG.</summary>
    public static EquatableArray<Guid> GameSectors(
        Guid rollId, IEnumerable<RollOffer> offers, IEnumerable<RollMiss> misses, IEnumerable<Game> available)
    {
        var drawn = offers.Select(o => o.GameId).Concat(misses.Select(m => m.GameId)).Take(MaxGameSectors).ToArray();
        var extra = available.Select(g => g.Id).OrderBy(id => SortKey(rollId, id)).ThenBy(id => id)
            .Take(MaxGameSectors - drawn.Length);
        return [.. drawn.Concat(extra).OrderBy(id => SortKey(rollId, id)).ThenBy(id => id)];
    }

    private static ulong SortKey(Guid rollId, Guid gameId)
    {
        Span<byte> ids = stackalloc byte[32];
        rollId.TryWriteBytes(ids[..16]);
        gameId.TryWriteBytes(ids[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(ids, hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }
}

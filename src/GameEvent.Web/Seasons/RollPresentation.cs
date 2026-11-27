using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Turns;
using GameEvent.Infrastructure.Seasons;

namespace GameEvent.Web.Seasons;

internal sealed record UnshownRoll(Guid RevealId, DateTimeOffset ExpiresAt, IReadOnlySet<Guid> GameIds);

/// <summary>Uses the current reservation for public presentation while the player is still showing the roll.</summary>
internal static class RollPresentation
{
    internal static UnshownRoll? Pending(RollOffer? offer, PendingChoice? choice)
    {
        if (offer?.Reveal is { } offerReveal)
        {
            return new UnshownRoll(offerReveal.Id, offerReveal.ExpiresAt, new HashSet<Guid> { offer.GameId });
        }

        return choice?.Reveal is { } choiceReveal
            ? new UnshownRoll(choiceReveal.Id, choiceReveal.ExpiresAt, choice.Options.Where(option => option.Game is not null).Select(option => option.Game!.GameId).ToHashSet())
            : null;
    }

    internal static Dictionary<Guid, UnshownRoll> PendingByPlayer(IEnumerable<SeasonPlayerRecord> players)
    {
        var pending = new Dictionary<Guid, UnshownRoll>();
        foreach (var player in players)
        {
            var offer = player.OfferJson is { } offerJson ? JsonSerializer.Deserialize<RollOffer>(offerJson, EngineJson.Options) : null;
            var choice = player.ChoiceJson is { } choiceJson ? JsonSerializer.Deserialize<PendingChoice>(choiceJson, EngineJson.Options) : null;
            if (Pending(offer, choice) is { } roll)
            {
                pending.Add(player.Id, roll);
            }
        }

        return pending;
    }

    internal static bool HidesMiss(IReadOnlyDictionary<Guid, UnshownRoll> pending, RollMiss miss, DateTimeOffset now) =>
        miss.Reason == RollMissReason.BeingPlayed
        && pending.TryGetValue(miss.ByPlayerId, out var roll)
        && roll.ExpiresAt > now
        && roll.GameIds.Contains(miss.GameId);

    internal static bool NamesMiss(EquatableArray<Guid>? wheel, IReadOnlyDictionary<Guid, UnshownRoll> pending, RollMiss miss, DateTimeOffset now) =>
        (wheel is null || wheel.Value.Contains(miss.GameId)) && !HidesMiss(pending, miss, now);
}

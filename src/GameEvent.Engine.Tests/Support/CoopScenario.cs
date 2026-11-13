using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Tests.Support;

/// <summary>Coop in scenarios (task 5.10, D-520…D-523): the flag on with the numbers of the decisions.</summary>
public static class CoopScenario
{
    /// <summary>
    /// The ruleset with <c>features.coop</c> on: points and coins split by <paramref name="pointsShare"/> and
    /// <paramref name="coinsShare"/> (the roller takes the rounding up), invitations live <paramref name="inviteTtlHours"/>
    /// hours, at most <paramref name="maxOpenInvites"/> pending ones sent by a player.
    /// </summary>
    public static Ruleset WithCoop(
        this Ruleset ruleset, decimal coinsShare = 0.5m, int maxOpenInvites = 3, int inviteTtlHours = 24, decimal pointsShare = 0.5m)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        return ruleset with
        {
            Features = ruleset.Features with { Coop = true },
            Reward = ruleset.Reward with { Coop = ruleset.Reward.Coop with { PointsShare = pointsShare, CoinsShare = coinsShare } },
            Interactions = ruleset.Interactions with { InviteTtlHours = inviteTtlHours, MaxOpenInvitesPerPlayer = maxOpenInvites },
        };
    }
}

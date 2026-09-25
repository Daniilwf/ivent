using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Finish;

/// <summary>
/// A player's finish (D-99): <see cref="Order"/> among the finishers (1, 2, 3…, never reused), the run whose move
/// brought them there, and whether they are frozen (the first finisher, once the finish is approved).
/// </summary>
public sealed record FinishState(int Order, Guid RunId, DateTimeOffset FinishedAt, bool Frozen);

/// <summary>The player's token reached the finish; a finish bonus for a not-first finisher follows in the same command.</summary>
[EventType("player-finished")]
public sealed record PlayerFinished(Guid PlayerId, Guid RunId, int Order, DateTimeOffset FinishedAt) : IGameEvent;

/// <summary>The first finisher is frozen: points and coins no longer change (D-99, the freeze amendment).</summary>
[EventType("player-frozen")]
public sealed record PlayerFrozen(Guid PlayerId) : IGameEvent;

/// <summary>The run that brought the player to the finish was rejected: the finish is revoked (D-15, D-99).</summary>
[EventType("player-finish-revoked")]
public sealed record PlayerFinishRevoked(Guid PlayerId, Guid RunId) : IGameEvent;

/// <summary>Who finished and who is first (D-99).</summary>
public static class FinishLine
{
    /// <summary>The first finisher: the lowest order among players whose finish stands; null when nobody finished.</summary>
    public static Guid? First(SeasonState state) =>
        throw new NotImplementedException("C9");

    /// <summary>The finish bonus of the <paramref name="order"/>-th finisher when <paramref name="firstOrder"/> is first.</summary>
    public static int Bonus(Rulesets.FinishRules rules, int order, int firstOrder) =>
        throw new NotImplementedException("C9");
}

internal static class Finishing
{
    public static SeasonState Apply(SeasonState state, PlayerFinished e) =>
        throw new NotImplementedException("C9");

    public static SeasonState Apply(SeasonState state, PlayerFrozen e) =>
        throw new NotImplementedException("C9");

    public static SeasonState Apply(SeasonState state, PlayerFinishRevoked e) =>
        throw new NotImplementedException("C9");
}

using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Duels;

/// <summary>Duels (D-525): not implemented yet — the contract for the tests written first.</summary>
internal static class Dueling
{
    private static Decision NotYet() => Decision.Reject(RejectionCodes.FeatureDisabled, "Duels are not implemented yet.");

    public static Decision Decide(SeasonState state, ChallengeToDuel command, EngineContext context) => NotYet();

    public static Decision Decide(SeasonState state, AnswerDuel command, EngineContext context) => NotYet();

    public static Decision Decide(SeasonState state, WithdrawDuel command, EngineContext context) => NotYet();

    public static IEnumerable<IGameEvent> Settle(SeasonState state, IGameEvent e) => [];

    public static IEnumerable<IGameEvent> Timeouts(SeasonState state, DateTimeOffset now) => [];

    public static bool InPlay(SeasonState state) => false;

    public static SeasonState Apply(SeasonState state, IGameEvent e) => state;
}

/// <summary>When a duel waits for the timer (D-523, D-525): the challenge's or the race's end, on the challenger's timer column.</summary>
public static class DuelTimers
{
    /// <summary>The earliest end of an open duel <paramref name="playerId"/> challenged to; null when none, or duels are off (D-420).</summary>
    public static DateTimeOffset? Next(SeasonState state, Guid playerId)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Ruleset is { Features.Duels: true }
            ? state.Duels.Values.Where(d => d.IsOpen && d.ChallengerId == playerId).Select(d => (DateTimeOffset?)d.ExpiresAt).Min()
            : null;
    }
}

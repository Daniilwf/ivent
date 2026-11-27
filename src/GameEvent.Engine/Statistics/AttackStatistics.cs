using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Statistics;

public sealed record PlayerAttackStatistics(Guid PlayerId, int AttacksMade, int AttacksReceived);

public sealed record SeasonAttackStatistics(
    EquatableArray<PlayerAttackStatistics> Players,
    EquatableArray<Guid> MostAttacksCandidates);

public static class AttackStatistics
{
    /// <summary>Counts landed attacks per target in the effective season log and returns all positive ties.</summary>
    public static SeasonAttackStatistics Build(SeasonState state, IEnumerable<IGameEvent> effectiveLog)
    {
        ArgumentNullException.ThrowIfNull(state);
        var (made, received) = Count(effectiveLog);
        var players = state.Players.Keys.Order()
            .Select(id => new PlayerAttackStatistics(id, made.GetValueOrDefault(id), received.GetValueOrDefault(id)))
            .ToEquatableArray();

        EquatableArray<Guid> candidates = [];
        if (state.IsCreated && state.Rules.Nominations.Auto.Contains(AutoNomination.MostAttacks)
            && players.MaxBy(player => player.AttacksMade) is { AttacksMade: > 0 } leader)
        {
            candidates = players.Where(player => player.AttacksMade == leader.AttacksMade)
                .Select(player => player.PlayerId).ToEquatableArray();
        }

        return new(players, candidates);
    }

    public static PlayerAttackStatistics ForPlayer(Guid playerId, IEnumerable<IGameEvent> effectiveLog)
    {
        var (made, received) = Count(effectiveLog);
        return new(playerId, made.GetValueOrDefault(playerId), received.GetValueOrDefault(playerId));
    }

    private static (Dictionary<Guid, int> Made, Dictionary<Guid, int> Received) Count(IEnumerable<IGameEvent> effectiveLog)
    {
        ArgumentNullException.ThrowIfNull(effectiveLog);
        var made = new Dictionary<Guid, int>();
        var received = new Dictionary<Guid, int>();
        foreach (var attack in effectiveLog.OfType<HostileReceived>())
        {
            made[attack.FromPlayerId] = made.GetValueOrDefault(attack.FromPlayerId) + 1;
            received[attack.PlayerId] = received.GetValueOrDefault(attack.PlayerId) + 1;
        }

        return (made, received);
    }
}

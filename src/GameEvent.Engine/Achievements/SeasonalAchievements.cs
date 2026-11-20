using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Achievements;

/// <summary>A badge as awarded. A later content publication can rename or remove its definition.</summary>
public sealed record PlayerAchievement(string ObjectId, string Name, string Description, DateTimeOffset EarnedAt);

[EventType("achievement-earned")]
public sealed record AchievementEarned(Guid PlayerId, PlayerAchievement Achievement) : IGameEvent;

/// <summary>Awards seasonal achievements from the same resolved triggers as effects.</summary>
internal sealed class SeasonalAchievements : ITriggerHandler, IFiringTriggerHandler
{
    public static SeasonalAchievements Instance { get; } = new();

    internal static bool Supports(Trigger? trigger, ConditionSpec? condition) => trigger switch
    {
        Trigger.HostileIncoming => condition is
        { Stat: ContentStat.HostileReceived, Gte: > 0, Game: null, DifficultyAtLeast: null, DifficultyAtMost: null, Tag: null, MinDice: null },
        Trigger.RunCompleted => condition is
        { Stat: ContentStat.CompletedStreakWithTag, Gte: > 0, Game: null, DifficultyAtLeast: null, DifficultyAtMost: null, Tag: { } tag, MinDice: null }
            && !string.IsNullOrWhiteSpace(tag),
        _ => false,
    };

    public IEnumerable<IGameEvent> React(SeasonState state, IGameEvent trigger, EngineContext context) =>
        ReactInFirings(state, trigger, context, depth: 1, eventsBefore: 0).SelectMany(firing => firing);

    public IEnumerable<IReadOnlyList<IGameEvent>> ReactInFirings(
        SeasonState state, IGameEvent trigger, EngineContext context, int depth, int eventsBefore)
    {
        if (state.Ruleset?.Features.Achievements != true)
        {
            return [];
        }

        return trigger switch
        {
            HostileReceived hit => Awards(state, hit.PlayerId, Trigger.HostileIncoming, null, context, depth, eventsBefore),
            RunCompleted completed => Awards(state, completed.PlayerId, Trigger.RunCompleted, completed.RunId, context, depth, eventsBefore),
            CoopRewardSplit split => Awards(state, split.PartnerId, Trigger.RunCompleted, split.RunId, context, depth, eventsBefore),
            _ => [],
        };
    }

    private static IEnumerable<IReadOnlyList<IGameEvent>> Awards(
        SeasonState state, Guid playerId, Trigger trigger, Guid? runId, EngineContext context, int depth, int eventsBefore)
    {
        if (Targets.IsFirst(state, playerId))
        {
            yield break;
        }

        var player = state.Players[playerId];
        foreach (var entry in state.Catalog.Objects.Values)
        {
            if (entry is not { Deleted: false, Definition: { Kind: ObjectKind.Achievement, Scope: AchievementScope.Season } definition }
                || definition.Effect is not { Condition: { } condition } effect || effect.Trigger != trigger
                || !Supports(trigger, condition)
                || player.Achievements.Any(award => award.ObjectId == definition.Id)
                || !Conditions.Hold(state, context, player.PlayerId, condition, runId))
            {
                continue;
            }

            var run = new EffectRun(state, context, player.PlayerId, definition.Id, hostile: false, parameters: null,
                runId: runId, depth: depth, eventsBefore: eventsBefore)
            {
                CoinReason = CoinsReason.Achievement,
            };
            run.Emit(new AchievementEarned(
                player.PlayerId,
                new PlayerAchievement(definition.Id, definition.Name, definition.Description, context.Clock.UtcNow)));
            run.Run(effect, [player.PlayerId]);

            // A badge and its reward are one firing, so the chain writes both or cuts both.
            yield return run.Events;
        }
    }

    public static SeasonState Apply(SeasonState state, AchievementEarned e)
    {
        var player = state.Players[e.PlayerId];
        return state with
        {
            Players = state.Players.SetItem(e.PlayerId, player with { Achievements = [.. player.Achievements, e.Achievement] }),
        };
    }
}

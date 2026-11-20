using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Achievements;

/// <summary>The identity of an active award across all seasons.</summary>
public readonly record struct AccountAchievementKey(Guid UserId, string ObjectId);

[EventType("lifetime-achievement-earned")]
public sealed record LifetimeAchievementEarned(Guid PlayerId, PlayerAchievement Achievement) : IGameEvent;

/// <summary>Awards account achievements after the ordinary after-dice effects have changed the throw.</summary>
internal sealed class LifetimeAchievements : ITriggerHandler, IFiringTriggerHandler
{
    public static LifetimeAchievements Instance { get; } = new();

    internal static bool Supports(ConditionSpec? condition) => condition is
    {
        Stat: ContentStat.AllDiceMax, MinDice: > 0, Gte: null, Gt: null, Lte: null,
        Game: null, DifficultyAtLeast: null, DifficultyAtMost: null, Tag: null,
    };

    public IEnumerable<IGameEvent> React(SeasonState state, IGameEvent trigger, EngineContext context) =>
        ReactInFirings(state, trigger, context, depth: 1, eventsBefore: 0).SelectMany(firing => firing);

    public IEnumerable<IReadOnlyList<IGameEvent>> ReactInFirings(
        SeasonState state, IGameEvent trigger, EngineContext context, int depth, int eventsBefore)
    {
        if (state.Ruleset?.Features.Achievements != true || trigger is not CompletionRolled completed)
        {
            yield break;
        }

        var player = state.Players[completed.PlayerId];
        // With no proof requirement the finishing throw freezes the player in this command,
        // after its own completion rewards. Subsequent throws cannot change the frozen balance.
        if (Finishes.IsFrozen(player) && player.Finish?.RunId != completed.RunId)
        {
            yield break;
        }

        var run = state.Runs[completed.RunId];
        var dice = run.Dice.Concat(run.ChallengeDice).Concat(run.DiceMods.ExtraDice).ToArray();
        var atMaximum = dice.Select((die, index) => run.DiceMods.Face(die, index == 0) == die.Sides).All(face => face);

        foreach (var entry in state.Catalog.Objects.Values)
        {
            if (entry is not { Deleted: false, Definition: { Kind: ObjectKind.Achievement, Scope: AchievementScope.AllTime } definition }
                || definition.Effect is not { Trigger: Trigger.AfterDice, Condition: { MinDice: { } minDice } condition } effect
                || !Supports(condition) || dice.Length < minDice || !atMaximum
                || player.LifetimeAchievements.Any(award => award.ObjectId == definition.Id)
                || context.AccountAchievements?.Contains(new AccountAchievementKey(player.UserId, definition.Id)) == true)
            {
                continue;
            }

            var firing = new EffectRun(state, context, player.PlayerId, definition.Id, hostile: false, parameters: null,
                runId: run.RunId, depth: depth, eventsBefore: eventsBefore)
            {
                CoinReason = CoinsReason.Achievement,
            };
            firing.Emit(new LifetimeAchievementEarned(player.PlayerId,
                new PlayerAchievement(definition.Id, definition.Name, definition.Description, context.Clock.UtcNow)));
            firing.Run(effect, [player.PlayerId]);

            // The chain accepts or cuts the badge and its coin grants together.
            yield return firing.Events;
        }
    }

    public static SeasonState Apply(SeasonState state, LifetimeAchievementEarned e)
    {
        var player = state.Players[e.PlayerId];
        return state with
        {
            Players = state.Players.SetItem(e.PlayerId, player with
            {
                LifetimeAchievements = [.. player.LifetimeAchievements, e.Achievement],
            }),
        };
    }
}

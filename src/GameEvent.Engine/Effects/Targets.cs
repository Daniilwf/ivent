using GameEvent.Engine.Content;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Effects;

/// <summary>
/// Whom an effect reaches (SPEC «Цели», «Взаимодействие игроков»): everyone but the first finisher; random targets only
/// among the active; effects only on the position skip the finishers, whose position is fixed; <c>among</c> and
/// <c>excludeSelf</c> narrow; <c>effects.attacksOnlyOnHigherPoints</c> keeps hostile effects on those with more points.
/// </summary>
internal static class Targets
{
    /// <summary>The first finisher is out of the game for others: not a target, and does not act on others (SPEC).</summary>
    public static bool IsFirst(SeasonState state, Guid playerId) => FinishLine.First(state) == playerId;

    /// <summary>
    /// The targets of an item used by <paramref name="userId"/> (a <c>chosen</c> one is <paramref name="chosen"/>), or a
    /// refusal naming what is wrong.
    /// </summary>
    public static (IReadOnlyList<Guid> Targets, Decision? Refusal) ForUse(
        SeasonState state, EngineContext context, ObjectDefinition definition, Guid userId, Guid? chosen)
    {
        var spec = definition.Effect?.Target;
        var selector = spec?.Selector ?? TargetSelector.Self;
        if (selector == TargetSelector.Chosen)
        {
            if (chosen is not { } id)
            {
                return ([], Decision.Reject(RejectionCodes.ItemTargetRequired, $"«{definition.Id}» needs a chosen player."));
            }

            return Candidates(state, definition, spec, userId).Any(p => p.PlayerId == id)
                ? ([id], null)
                : ([], Decision.Reject(RejectionCodes.ItemInvalidTarget, $"Player {id} cannot be a target of «{definition.Id}»."));
        }

        var targets = Resolve(state, context, definition, spec, userId);
        return targets.Count == 0
            ? ([], Decision.Reject(RejectionCodes.ItemNoTarget, $"«{definition.Id}» has nobody to act on."))
            : (targets, null);
    }

    /// <summary>The targets of an effect that fired for its owner (no one chooses: <c>chosen</c> reaches nobody).</summary>
    public static IReadOnlyList<Guid> ForTriggered(SeasonState state, EngineContext context, ObjectDefinition definition, Guid ownerId) =>
        definition.Effect?.Target?.Selector == TargetSelector.Chosen ? [] : Resolve(state, context, definition, definition.Effect?.Target, ownerId);

    private static List<Guid> Resolve(SeasonState state, EngineContext context, ObjectDefinition definition, TargetSpec? spec, Guid userId)
    {
        var candidates = Candidates(state, definition, spec, userId);
        switch (spec?.Selector ?? TargetSelector.Self)
        {
            case TargetSelector.Self:
                return [userId];
            case TargetSelector.RandomActive:
                var active = candidates.Where(p => !p.IsInactive).ToList();
                return active.Count == 0 ? [] : [active[context.Random.NextInt(0, active.Count)].PlayerId];
            case TargetSelector.Leader:
                return candidates.OrderByDescending(p => p.Points).ThenBy(p => p.PointsTick).ThenBy(p => p.PlayerId).Take(1).Select(p => p.PlayerId).ToList();
            default:
                return [.. candidates.Select(p => p.PlayerId)];
        }
    }

    // Everyone the effect may reach, in id order; the selector picks among them.
    private static List<SeasonPlayer> Candidates(SeasonState state, ObjectDefinition definition, TargetSpec? spec, Guid userId)
    {
        var user = state.Players[userId];
        var selector = spec?.Selector ?? TargetSelector.Self;
        var positionOnly = PositionOnly(definition.Effect);
        var among = selector switch
        {
            TargetSelector.HigherPoints => Among.HigherPoints,
            TargetSelector.LowerPoints => Among.LowerPoints,
            _ => spec?.Among,
        };
        var onlyHigher = definition.Hostile && state.Rules.Effects.AttacksOnlyOnHigherPoints;
        return [.. state.Players.Values.Where(p =>
            !IsFirst(state, p.PlayerId)
            && !(positionOnly && p.Finish is not null)
            && !(spec?.ExcludeSelf == true && p.PlayerId == userId)
            && !(among == Among.HigherPoints && p.Points <= user.Points)
            && !(among == Among.LowerPoints && p.Points >= user.Points)
            && !(onlyHigher && p.PlayerId != userId && p.Points <= user.Points))];
    }

    /// <summary>An effect that only moves the token: finishers are out of its reach (SPEC «Исключения по умолчанию»).</summary>
    public static bool PositionOnly(EffectSpec? effect)
    {
        if (effect is null)
        {
            return false;
        }

        var actions = effect.Actions.Concat(effect.Outcomes?.Cases.SelectMany(c => c.Actions) ?? []).ToList();
        return actions.Count > 0 && actions.All(a => a is MoveAction or TeleportAction);
    }
}

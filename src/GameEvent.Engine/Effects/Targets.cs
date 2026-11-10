using GameEvent.Engine.Content;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Effects;

/// <summary>
/// Whom an effect reaches (SPEC «Цели», «Взаимодействие игроков»): everyone but the first finisher; random targets only
/// among the active; a player who finished not first is reached only by effects on points and coins, and his own effects that only move him
/// have nobody to act on (his position is fixed), and the changes of the game others put on him before are ignored
/// (D-425); <c>among</c> and
/// <c>excludeSelf</c> narrow; <c>effects.attacksOnlyOnHigherPoints</c> keeps hostile effects on those with more points.
/// </summary>
internal static class Targets
{
    /// <summary>The first finisher is out of the game for others: not a target, and does not act on others (SPEC).</summary>
    public static bool IsFirst(SeasonState state, Guid playerId) => FinishLine.First(state) == playerId;

    /// <summary>A player who finished, but not first: his position is fixed, only points and coins reach him (SPEC «Цели»).</summary>
    public static bool IsLaterFinisher(SeasonState state, Guid playerId) =>
        state.Players[playerId].Finish is not null && !IsFirst(state, playerId);

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

    /// <summary>
    /// The players <paramref name="userId"/> may pick as the target of <paramref name="definition"/>'s <c>chosen</c>
    /// selector, in id order: exactly the ones <see cref="ForUse"/> accepts (the screen offers no other, D-440).
    /// </summary>
    public static IReadOnlyList<Guid> Choosable(SeasonState state, ObjectDefinition definition, Guid userId) =>
        [.. Candidates(state, definition, definition.Effect?.Target, userId).Select(p => p.PlayerId)];

    /// <summary>The targets of an effect that fired for its owner (no one chooses: <c>chosen</c> reaches nobody).</summary>
    public static IReadOnlyList<Guid> ForTriggered(SeasonState state, EngineContext context, ObjectDefinition definition, Guid ownerId) =>
        definition.Effect?.Target?.Selector == TargetSelector.Chosen ? [] : Resolve(state, context, definition, definition.Effect?.Target, ownerId);

    private static List<Guid> Resolve(SeasonState state, EngineContext context, ObjectDefinition definition, TargetSpec? spec, Guid userId)
    {
        var candidates = Candidates(state, definition, spec, userId);
        switch (spec?.Selector ?? TargetSelector.Self)
        {
            case TargetSelector.Self:
                // A finisher's position is fixed: an effect that only moves him has nobody to act on (D-422)
                return candidates.Any(p => p.PlayerId == userId) || !PositionOnly(definition.Effect) ? [userId] : [];
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
        var pointsAndCoinsOnly = PointsAndCoinsOnly(definition.Effect);
        var among = selector switch
        {
            TargetSelector.HigherPoints => Among.HigherPoints,
            TargetSelector.LowerPoints => Among.LowerPoints,
            _ => spec?.Among,
        };
        var onlyHigher = definition.Hostile && state.Rules.Effects.AttacksOnlyOnHigherPoints;
        return [.. state.Players.Values.Where(p =>
            !IsFirst(state, p.PlayerId)
            && !(p.Finish is not null && (p.PlayerId == userId ? positionOnly : !pointsAndCoinsOnly))
            && !(spec?.ExcludeSelf == true && p.PlayerId == userId)
            && !(among == Among.HigherPoints && p.Points <= user.Points)
            && !(among == Among.LowerPoints && p.Points >= user.Points)
            && !(onlyHigher && p.PlayerId != userId && p.Points <= user.Points))];
    }

    /// <summary>
    /// An effect that acts on its target only through points and coins (dice for the amount aside; actions aimed at the
    /// user himself do not count): the only kind that reaches a player who finished not first (SPEC «Цели», D-424).
    /// </summary>
    public static bool PointsAndCoinsOnly(EffectSpec? effect)
    {
        if (effect is null)
        {
            return false;
        }

        var onTarget = effect.Actions.Concat(effect.Outcomes?.Cases.SelectMany(c => c.Actions) ?? [])
            .Where(a => a.Target != ActionTarget.Self)
            .ToList();
        return onTarget.Any(a => a is ChangeResourceAction)
            && onTarget.All(a => a is RollAction or RequestChoiceAction || (a is ChangeResourceAction c && c.Resource is Actions.ChangeResourceHandler.Points or Actions.ChangeResourceHandler.Coins));
    }

    /// <summary>
    /// An effect that changes the choice of the game (<c>modifyNextRoll</c>): put by another player before the finish, it
    /// is ignored at the rolls of one who finished not first (D-425).
    /// </summary>
    public static bool ChangesTheGame(EffectSpec? effect) =>
        effect is not null && effect.Actions.Concat(effect.Outcomes?.Cases.SelectMany(c => c.Actions) ?? []).Any(a => a is ModifyNextRollAction);

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

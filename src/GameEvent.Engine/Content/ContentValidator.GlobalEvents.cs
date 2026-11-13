using GameEvent.Engine.GlobalEvents;

namespace GameEvent.Engine.Content;

public static partial class ContentValidator
{
    /// <summary>The longest a global event runs, in hours (a season's year, as a poll, D-750): its end stays a moment of the calendar.</summary>
    public const int MaxGlobalEventHours = MaxPollHours;

    /// <summary>The largest target of a global goal (a safety ceiling, not balance).</summary>
    public const int MaxGlobalGoalTarget = 1_000_000;

    /// <summary>
    /// The errors of a global event as the admin launches it (D-517…D-519, D-750): its term, the effect of the event on
    /// everyone and the goal with its reward. <paramref name="definition"/> is the published event (checked as content
    /// already), <paramref name="catalog"/> holds what the reward gives.
    /// </summary>
    public static IReadOnlyList<ContentError> CheckGlobalEvent(ObjectDefinition definition, int durationHours, GlobalEventGoal? goal, ContentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(catalog);
        var errors = new List<ContentError>();
        void Error(string path, string message) => errors.Add(new ContentError(path, message));
        if (durationHours is < 1 or > MaxGlobalEventHours)
        {
            Error("$.durationHours", $"A global event runs 1–{MaxGlobalEventHours} hours.");
        }

        if (definition.Effect is { } effect && !definition.Manual)
        {
            CheckForAll(effect, $"$.objects[{definition.Id}].effect", allowVote: true, Error);
        }

        if (goal is null)
        {
            return errors;
        }

        if (!Enum.IsDefined(goal.Metric))
        {
            Error("$.goal.metric", "A global goal counts «hours» or «runs».");
        }

        if (goal.Target is < 1 or > MaxGlobalGoalTarget)
        {
            Error("$.goal.target", $"A global goal's target is 1–{MaxGlobalGoalTarget}.");
        }

        if (goal.Reward is { } reward)
        {
            CheckEffect(reward, "$.goal.reward", Error);
            CheckForAll(reward, "$.goal.reward", allowVote: false, Error);
            var given = reward.Actions.Concat(reward.Outcomes?.Cases.SelectMany(c => c.Actions) ?? []).OfType<GiveObjectAction>();
            foreach (var missing in given.Where(g => catalog.Live(g.ObjectId) is null).Select(g => g.ObjectId).Distinct(StringComparer.Ordinal))
            {
                Error("$.goal.reward", $"The reward gives «{missing}», which is not in the season's content.");
            }
        }

        return errors;
    }

    /// <summary>
    /// An effect the system plays on everyone (D-519, D-703 (3)): at once, on each player as himself, asking nobody — only a
    /// global event's own first action may ask the voters (EV23, D-753) — and moving nobody.
    /// </summary>
    private static void CheckForAll(EffectSpec effect, string path, bool allowVote, Action<string, string> error)
    {
        if (effect.Trigger is not null || effect.Duration is not null || effect.Intercept is not null || effect.Condition is not null)
        {
            error(path, "An effect on everyone acts at once: no trigger, duration, condition or interception.");
        }

        if (effect.Target is { } target && (target.Selector is not (TargetSelector.Self or TargetSelector.All) || target.Among is not null || target.ExcludeSelf))
        {
            error($"{path}.target", "An effect on everyone reaches each player as himself: «all» or «self» only.");
        }

        if (effect.Outcomes is { Choice: not null })
        {
            error($"{path}.outcomes.choice", "Nobody answers an effect on everyone: it branches by a roll only.");
        }

        var actions = effect.Actions.Select((a, i) => (Action: a, Path: $"{path}.actions[{i}]"))
            .Concat((effect.Outcomes?.Cases ?? []).SelectMany((c, j) => c.Actions.Select((a, i) => (Action: a, Path: $"{path}.outcomes.cases[{j}].actions[{i}]"))))
            .ToList();
        foreach (var (action, at) in actions)
        {
            var vote = allowVote && at == $"{path}.actions[0]" && action is RequestChoiceAction { Vote: not null };
            if (!vote && action is RequestChoiceAction or TakeObjectAction { Pick: Pick.Chosen } or MoveAction { Direction: MoveDirection.Chosen })
            {
                error(at, "Nobody answers an effect on everyone: only a global event's first action may ask the voters.");
            }

            if (action is MoveAction or TeleportAction)
            {
                error(at, "An effect on everyone does not move or teleport (D-519).");
            }
        }
    }
}

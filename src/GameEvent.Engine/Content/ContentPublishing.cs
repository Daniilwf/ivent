using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Content;

/// <summary>
/// Loading content into a season (D-400): the pack is checked whole — every object by <see cref="ContentValidator"/>, the
/// references between them, and what this build plays (stage 4: items, effects, special rolls; events and achievements
/// come with their stages) — and stored in the log.
/// </summary>
public static class ContentPublishing
{
    /// <summary>The problems of <paramref name="pack"/>, each with its JSON path; empty when it can be published.</summary>
    public static IReadOnlyList<ContentError> Check(ContentPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var errors = new List<ContentError>();
        void Error(string path, string message) => errors.Add(new ContentError(path, message));

        var ids = pack.Objects.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var wheels = pack.Wheels.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var duplicate in pack.Objects.GroupBy(o => o.Id).Where(g => g.Count() > 1))
        {
            Error("$.objects", $"Object «{duplicate.Key}» is listed twice.");
        }

        foreach (var duplicate in pack.Wheels.GroupBy(w => w.Id).Where(g => g.Count() > 1))
        {
            Error("$.wheels", $"Wheel «{duplicate.Key}» is listed twice.");
        }

        for (var i = 0; i < pack.Objects.Count; i++)
        {
            var definition = pack.Objects[i];
            var at = $"$.objects[{i}]";
            errors.AddRange(ContentValidator.Check(definition).Select(e => new ContentError(at + e.Path[1..], e.Message)));
            Supported(definition, at, Error);
            if (definition.Effect is { } effect)
            {
                References(effect, $"{at}.effect", ids, wheels, Error);
            }
        }

        for (var i = 0; i < pack.Wheels.Count; i++)
        {
            var wheel = pack.Wheels[i];
            var at = $"$.wheels[{i}]";
            if (wheel.Entries.Count == 0 || wheel.Entries.Any(e => e.Weight < 0) || wheel.Entries.Sum(e => e.Weight) <= 0)
            {
                Error($"{at}.entries", "A wheel has entries with weights of 0 or more, and some weight in all.");
            }

            for (var j = 0; j < wheel.Entries.Count; j++)
            {
                if (!ids.Contains(wheel.Entries[j].ObjectId))
                {
                    Error($"{at}.entries[{j}].objectId", $"«{wheel.Entries[j].ObjectId}» is not an object of the pack.");
                }
            }

            if (string.IsNullOrWhiteSpace(wheel.Name) || wheel.Name.Length > ContentValidator.MaxNameLength)
            {
                Error($"{at}.name", $"1–{ContentValidator.MaxNameLength} characters.");
            }
        }

        return errors;
    }

    // What stage 4 plays (D-402): the rest waits for its stage and is refused rather than silently ignored.
    private static void Supported(ObjectDefinition definition, string at, Action<string, string> error)
    {
        if (definition.Kind is ObjectKind.Event or ObjectKind.Achievement)
        {
            error($"{at}.kind", $"{definition.Kind} objects come with their stage (events — 5, achievements — 6).");
            return;
        }

        if (definition.Effect is not { } effect)
        {
            return;
        }

        var triggered = definition.Kind != ObjectKind.Item;
        switch (effect.Trigger)
        {
            case Trigger.Time:
                error($"{at}.effect.trigger", "The «time» trigger is not played by this build yet.");
                break;
            case Trigger.HostileIncoming when effect.Intercept is null:
                error($"{at}.effect.trigger", "«hostileIncoming» without an interception comes with achievements (stage 6).");
                break;
            case not Trigger.BeforeRoll when definition.Kind == ObjectKind.SpecialRoll:
                error($"{at}.effect.trigger", "A special roll acts before a roll («beforeRoll»).");
                break;
            default:
                break;
        }

        if (triggered && effect.Target?.Selector == TargetSelector.Chosen)
        {
            error($"{at}.effect.target", "An effect that acts on its own has nobody to choose its target.");
        }

        var rolled = effect.Outcomes is not null;
        var actions = effect.Actions.Concat(effect.Outcomes?.Cases.SelectMany(c => c.Actions) ?? []).ToList();
        for (var i = 0; i < actions.Count; i++)
        {
            var path = $"{at}.effect.actions[{i}]";
            switch (actions[i])
            {
                case ModifyNextRollAction { RunCondition: not null }:
                    error($"{path}.runCondition", "A condition on the run comes with events (stage 5).");
                    break;
                case DrawEventAction draw when draw.Deck is not ("good" or "bad"):
                    error($"{path}.deck", "Until events (stage 5) only «good» and «bad» are drawn, by hand.");
                    break;
                case RequestChoiceAction when triggered:
                case TakeObjectAction { Pick: Pick.Chosen } when triggered:
                    error(path, "An effect that acts on its own has nobody to ask.");
                    break;
                case RequestChoiceAction when rolled:
                    error(path, "A choice is asked before any roll: the player answers when using the item.");
                    break;
                case ModifyDiceAction { Stage: DiceStage.Count, Value: { Kind: ContentValueKind.Number, Number: < 1 } }:
                    error($"{path}.value", "The «count» stage adds dice: 1 or more.");
                    break;
                case ModifyDiceAction { Stage: DiceStage.Reroll, When: DiceWhen.Next }:
                    error($"{path}.when", "A reroll changes the throw just made: «current».");
                    break;
                default:
                    break;
            }

            rolled |= actions[i] is RollAction;
        }
    }

    // giveObject, transformObject and spinWheel name objects and wheels of the same pack.
    private static void References(EffectSpec effect, string at, HashSet<string> ids, HashSet<string> wheels, Action<string, string> error)
    {
        var actions = effect.Actions.Select((a, i) => (a, $"{at}.actions[{i}]"))
            .Concat(effect.Outcomes?.Cases.SelectMany((c, ci) => c.Actions.Select((a, i) => (a, $"{at}.outcomes.cases[{ci}].actions[{i}]"))) ?? []);
        foreach (var (action, path) in actions)
        {
            switch (action)
            {
                case GiveObjectAction give when !ids.Contains(give.ObjectId):
                    error($"{path}.objectId", $"«{give.ObjectId}» is not an object of the pack.");
                    break;
                case TransformObjectAction { Mode: TransformMode.Transform, Into: { } into } when !ids.Contains(into):
                    error($"{path}.into", $"«{into}» is not an object of the pack.");
                    break;
                case SpinWheelAction spin when !wheels.Contains(spin.Wheel):
                    error($"{path}.wheel", $"«{spin.Wheel}» is not a wheel of the pack.");
                    break;
                default:
                    break;
            }
        }
    }

    internal static Decision Decide(SeasonState state, PublishContent command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (!state.Rules.Features.Items)
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "Items are off in this season: turn features.items on first.");
        }

        if (state.Status is not (SeasonStatus.Draft or SeasonStatus.Active))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"Content is published while the season is a draft or running; it is {state.Status}.");
        }

        if (string.IsNullOrWhiteSpace(command.Comment))
        {
            return Decision.Reject(RejectionCodes.CommentRequired, "Every admin change explains itself in the public log.");
        }

        if (command.Comment.Length > Limits.MaxCommentLength)
        {
            return Decision.Reject(RejectionCodes.CommentTooLong, $"The comment is limited to {Limits.MaxCommentLength} characters.");
        }

        var errors = Check(command.Pack);
        if (errors.Count > 0)
        {
            return Decision.Reject(RejectionCodes.ContentInvalid, string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}")));
        }

        var catalog = state.Catalog;
        var unchanged = catalog.Version > 0
            && catalog.Objects.Values.Where(e => !e.Deleted).Select(e => e.Definition).OrderBy(d => d.Id, StringComparer.Ordinal)
                .SequenceEqual(command.Pack.Objects.OrderBy(d => d.Id, StringComparer.Ordinal))
            && catalog.Wheels.Values.SequenceEqual(command.Pack.Wheels.OrderBy(w => w.Id, StringComparer.Ordinal));
        return unchanged
            ? Decision.Reject(RejectionCodes.ContentUnchanged, "The pack equals the content in force.")
            : Decision.Accept(new ContentPublished(catalog.Version + 1, command.Pack, command.Comment.Trim()));
    }

    internal static SeasonState Apply(SeasonState state, ContentPublished e) =>
        state with { Content = state.Catalog.Publish(e.Pack) with { Version = e.Version } };
}

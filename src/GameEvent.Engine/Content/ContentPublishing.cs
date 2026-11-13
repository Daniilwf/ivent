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
    // Size limits of one pack (NB-SEC-3, D-653): the same numbers as the admin's content API (D-491), entries per wheel on top.
    // The imported old season has under 400 objects.
    public const int MaxObjects = 1000;
    public const int MaxWheels = 100;
    public const int MaxWheelEntries = 200;

    /// <summary>
    /// The problems of <paramref name="pack"/>, each with its JSON path; empty when it can be published. With
    /// <paramref name="events"/> off (<c>features.events</c>) the pack holds no events and no decks, and draws only
    /// «good» and «bad» by hand (D-10, D-532); on, it may hold events and their decks (D-501).
    /// </summary>
    public static IReadOnlyList<ContentError> Check(ContentPack pack, bool events = false)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var errors = new List<ContentError>();
        void Error(string path, string message) => errors.Add(new ContentError(path, message));

        // The reader lets a null list item through (nullable annotations do not reach list items): named, then skipped
        Nulls(pack.Objects, "$.objects", Error);
        Nulls(pack.Wheels, "$.wheels", Error);
        var objects = pack.Objects.Where(o => o is not null).ToList();
        var ids = objects.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var eventIds = objects.Where(o => o.Kind == ObjectKind.Event).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var wheels = pack.Wheels.Where(w => w is not null && w.Deck is null).Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        var decks = pack.Wheels.Where(w => w is not null && w.Deck is not null).Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var duplicate in objects.GroupBy(o => o.Id).Where(g => g.Count() > 1))
        {
            Error("$.objects", $"Object «{duplicate.Key}» is listed twice.");
        }

        foreach (var duplicate in pack.Wheels.Where(w => w is not null).GroupBy(w => w.Id).Where(g => g.Count() > 1))
        {
            Error("$.wheels", $"Wheel «{duplicate.Key}» is listed twice.");
        }

        for (var i = 0; i < pack.Objects.Count; i++)
        {
            var definition = pack.Objects[i];
            if (definition is null)
            {
                continue;
            }

            var at = $"$.objects[{i}]";
            errors.AddRange(ContentValidator.Check(definition).Select(e => new ContentError(at + e.Path[1..], e.Message)));
            Supported(definition, at, events, Error);
            if (definition.Effect is { } effect)
            {
                References(effect, $"{at}.effect", ids, eventIds, wheels, decks, Error);
            }
        }

        for (var i = 0; i < pack.Wheels.Count; i++)
        {
            var wheel = pack.Wheels[i];
            if (wheel is null)
            {
                continue;
            }

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

            Deck(wheel, at, events, eventIds, Error);
        }

        return errors;
    }

    private static void Nulls<T>(IReadOnlyList<T> items, string path, Action<string, string> error)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is null)
            {
                error($"{path}[{i}]", "A list item is an object, not null.");
            }
        }
    }

    // What this build plays (D-402): the rest waits for its stage and is refused rather than silently ignored. Events
    // play once features.events is on (D-532).
    private static void Supported(ObjectDefinition definition, string at, bool events, Action<string, string> error)
    {
        if (definition.Kind == ObjectKind.Achievement || (definition.Kind == ObjectKind.Event && !events))
        {
            error($"{at}.kind", definition.Kind == ObjectKind.Event
                ? "Events are played once features.events is on."
                : $"{definition.Kind} objects come with their stage (achievements — 6).");
            return;
        }

        if (definition.Kind == ObjectKind.Event)
        {
            Event(definition, at, error);
        }

        if (definition.Effect is not { } effect)
        {
            return;
        }

        var triggered = definition.Kind != ObjectKind.Item;

        // A drawn event asks its player and waits for the answer on the server (D-511): it has somebody to ask
        var asksLater = definition.Kind == ObjectKind.Event;
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

        if (triggered && !asksLater && effect.Target?.Selector == TargetSelector.Chosen)
        {
            error($"{at}.effect.target", "An effect that acts on its own has nobody to choose its target.");
        }

        var rolled = effect.Outcomes is { Roll: not null };
        if (triggered && !asksLater && effect.Outcomes is { Choice: not null })
        {
            error($"{at}.effect.outcomes.choice", "An effect that acts on its own has nobody to ask.");
        }

        // What the screen asks is known before the use (D-440): not inside a branch whose case a roll or the answer picks,
        // not the objects of a target the selector picks (a list of preference asks last, ContentValidator).
        var byRoll = rolled;
        var byChoice = effect.Outcomes is { Choice: not null };
        var picksTarget = effect.Target?.Selector is not (null or TargetSelector.Self or TargetSelector.Chosen);
        var actions = effect.Actions.Concat(effect.Outcomes?.Cases.SelectMany(c => c.Actions) ?? []).ToList();
        for (var i = 0; i < actions.Count; i++)
        {
            var path = $"{at}.effect.actions[{i}]";
            var asks = actions[i] is RequestChoiceAction or TakeObjectAction { Pick: Pick.Chosen, All: false } or MoveAction { Direction: MoveDirection.Chosen };
            if (!triggered && asks && byChoice)
            {
                error(path, "A branch by the player's answer asks nothing more: the questions after it would depend on it.");
            }

            // An event waits for one answer at a time, at an action of its list (D-511): not inside a case, not a list of preference
            if (asksLater && asks)
            {
                if (i >= effect.Actions.Count)
                {
                    error(path, "An event asks in its list of actions, not inside a case of its outcomes.");
                }
                else if (actions[i] is TakeObjectAction { Count: not null })
                {
                    error($"{path}.count", "An event asks for one object at a time.");
                }

                continue;
            }

            switch (actions[i])
            {
                case ModifyNextRollAction { RunCondition: not null }:
                    error($"{path}.runCondition", "A condition on the run comes with events (stage 5).");
                    break;
                case DrawEventAction draw when !events && draw.Deck is not ("good" or "bad"):
                    error($"{path}.deck", "While features.events is off only «good» and «bad» are drawn, by hand.");
                    break;
                case DrawEventAction when definition.Kind == ObjectKind.Event:
                    // Played inside the draw, a draw of a draw would loop past the levels of the chain (D-531)
                    error(path, "An event does not draw another event.");
                    break;
                case RequestChoiceAction when triggered && !asksLater:
                case TakeObjectAction { Pick: Pick.Chosen } when triggered && !asksLater:
                case MoveAction { Direction: MoveDirection.Chosen } when triggered && !asksLater:
                    error(path, "An effect that acts on its own has nobody to ask.");
                    break;
                case RequestChoiceAction when rolled:
                case MoveAction { Direction: MoveDirection.Chosen } when rolled:
                case TakeObjectAction { Pick: Pick.Chosen, All: false } when byRoll:
                    error(path, "A choice is asked before any roll: the player answers when using the item.");
                    break;
                case TakeObjectAction { Pick: Pick.Chosen, All: false } take when picksTarget && take.Target != ActionTarget.Self:
                    error(path, "The selector picks the target only at the use: whose objects, or whom a given one goes to, is not known before.");
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

    // What an event plays (D-536, D-511, 5.3): its effect at once, from the one who drew it — on him, on the players its
    // selector picks, or on a player he picks (a chosen target is a question that waits for his answer); a text by hand
    // (manual: true) waits for resolution (D-410). It is hostile only when another player's hostile item drew it (D-504),
    // never by itself; a condition comes with 5.5 and is refused until then rather than ignored.
    private static void Event(ObjectDefinition definition, string at, Action<string, string> error)
    {
        if (definition.Hostile)
        {
            error($"{at}.hostile", "An event is played on the one who drew it: it is not hostile.");
        }

        if (definition.Effect is { Condition: not null })
        {
            error($"{at}.effect.condition", "An event plays when drawn, without a condition.");
        }
    }

    // A deck holds events only, and a loot wheel none (D-501): a drawn event is played, never kept. The standard decks are
    // the wheels named like the kinds, and of that kind.
    private static void Deck(WheelDefinition wheel, string at, bool events, HashSet<string> eventIds, Action<string, string> error)
    {
        if (wheel.Deck is { } kind)
        {
            if (!events)
            {
                error($"{at}.deck", "Decks of events are played once features.events is on.");
                return;
            }

            if (!Enum.IsDefined(kind))
            {
                error($"{at}.deck", $"Unknown kind of events {kind}.");
            }

            for (var j = 0; j < wheel.Entries.Count; j++)
            {
                if (!eventIds.Contains(wheel.Entries[j].ObjectId))
                {
                    error($"{at}.entries[{j}].objectId", $"A deck holds events: «{wheel.Entries[j].ObjectId}» is not an event of the pack.");
                }
            }
        }
        else
        {
            for (var j = 0; j < wheel.Entries.Count; j++)
            {
                if (eventIds.Contains(wheel.Entries[j].ObjectId))
                {
                    error($"{at}.entries[{j}].objectId", $"«{wheel.Entries[j].ObjectId}» is an event: events are drawn from decks, not won.");
                }
            }
        }

        if (events && Decks.EventDraws.KindOf(wheel.Id) is { } named && wheel.Deck != named)
        {
            error($"{at}.deck", $"«{wheel.Id}» is the standard deck of its kind: «deck» is «{wheel.Id}».");
        }
    }

    // giveObject, transformObject, spinWheel and drawEvent name objects, wheels and decks of the same pack.
    private static void References(
        EffectSpec effect, string at, HashSet<string> ids, HashSet<string> eventIds, HashSet<string> wheels, HashSet<string> decks, Action<string, string> error)
    {
        var actions = effect.Actions.Select((a, i) => (a, $"{at}.actions[{i}]"))
            .Concat(effect.Outcomes?.Cases.SelectMany((c, ci) => c.Actions.Select((a, i) => (a, $"{at}.outcomes.cases[{ci}].actions[{i}]"))) ?? []);
        foreach (var (action, path) in actions)
        {
            switch (action)
            {
                case GiveObjectAction give when eventIds.Contains(give.ObjectId):
                    error($"{path}.objectId", $"«{give.ObjectId}» is an event: events are drawn from decks, not given.");
                    break;
                case TransformObjectAction { Mode: TransformMode.Transform, Into: { } eventInto } when eventIds.Contains(eventInto):
                    error($"{path}.into", $"«{eventInto}» is an event: events are drawn from decks, not kept.");
                    break;
                case GiveObjectAction give when !ids.Contains(give.ObjectId):
                    error($"{path}.objectId", $"«{give.ObjectId}» is not an object of the pack.");
                    break;
                case TransformObjectAction { Mode: TransformMode.Transform, Into: { } into } when !ids.Contains(into):
                    error($"{path}.into", $"«{into}» is not an object of the pack.");
                    break;
                case SpinWheelAction spin when !wheels.Contains(spin.Wheel):
                    error($"{path}.wheel", decks.Contains(spin.Wheel)
                        ? $"«{spin.Wheel}» is a deck: its events are drawn with drawEvent."
                        : $"«{spin.Wheel}» is not a wheel of the pack.");
                    break;
                case DrawEventAction draw when Decks.EventDraws.KindOf(draw.Deck) is null && draw.Deck != Decks.DeckRef.Zone && !decks.Contains(draw.Deck):
                    error($"{path}.deck", $"«{draw.Deck}» is not a deck of the pack.");
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

        if (TooLarge(command.Pack) is { } tooLarge)
        {
            return Decision.Reject(RejectionCodes.ContentTooLarge, tooLarge);
        }

        if (string.IsNullOrWhiteSpace(command.Comment))
        {
            return Decision.Reject(RejectionCodes.CommentRequired, "Every admin change explains itself in the public log.");
        }

        if (command.Comment.Length > Limits.MaxCommentLength)
        {
            return Decision.Reject(RejectionCodes.CommentTooLong, $"The comment is limited to {Limits.MaxCommentLength} characters.");
        }

        if (command.ExpectedVersion is { } expected && expected != state.Catalog.Version)
        {
            return Decision.Reject(
                RejectionCodes.ContentVersionChanged,
                $"The content changed while it was edited: version {state.Catalog.Version} is in force, the pack was made from {expected}.");
        }

        var errors = Check(command.Pack, state.Rules.Features.Events);
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

    // Checked before the pack's contents, so a huge pack costs the queue nothing more
    private static string? TooLarge(ContentPack pack) =>
        pack.Objects.Count > MaxObjects ? $"At most {MaxObjects} objects in one pack; it has {pack.Objects.Count}."
        : pack.Wheels.Count > MaxWheels ? $"At most {MaxWheels} wheels in one pack; it has {pack.Wheels.Count}."
        : pack.Wheels.FirstOrDefault(w => w?.Entries.Count > MaxWheelEntries) is { } wheel
            ? $"At most {MaxWheelEntries} entries in a wheel; «{wheel.Id}» has {wheel.Entries.Count}."
            : null;

    internal static SeasonState Apply(SeasonState state, ContentPublished e) =>
        state with { Content = state.Catalog.Publish(e.Pack) with { Version = e.Version } };
}

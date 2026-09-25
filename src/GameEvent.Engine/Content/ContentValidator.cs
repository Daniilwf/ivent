using System.Text.RegularExpressions;

namespace GameEvent.Engine.Content;

/// <summary>A content error: the JSON path of the field and what is wrong.</summary>
public sealed record ContentError(string Path, string Message);

/// <summary>
/// The rules of content beyond its types (CONTENT.md, D-103): what each kind needs, what an effect may combine, and
/// that every value and reference makes sense where it is used. Parsing (<see cref="ContentJson"/>) checks the types.
/// </summary>
public static partial class ContentValidator
{
    public const int MaxIdLength = 50;
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 1000;

    /// <summary>The errors of <paramref name="definition"/>; empty when it is valid.</summary>
    public static IReadOnlyList<ContentError> Check(ObjectDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var errors = new List<ContentError>();
        void Error(string path, string message) => errors.Add(new ContentError(path, message));

        CheckId(definition.Id, "$.id", Error);
        CheckText(definition.Name, MaxNameLength, "$.name", Error);
        CheckText(definition.Description, MaxDescriptionLength, "$.description", Error);

        if (definition.Price is < 0)
        {
            Error("$.price", "A price is 0 or more; null means not for sale.");
        }

        var kind = definition.Kind;
        if (kind == ObjectKind.Item && definition.Window is null)
        {
            Error("$.window", "An item needs a window of use.");
        }

        if (kind != ObjectKind.Item && definition.Window is not null)
        {
            Error("$.window", "Only items have a window of use.");
        }

        if (kind == ObjectKind.Achievement && definition.Scope is null)
        {
            Error("$.scope", "An achievement is counted per season or over all seasons.");
        }

        if (kind != ObjectKind.Achievement && definition.Scope is not null)
        {
            Error("$.scope", "Only achievements have a scope.");
        }

        if (definition.Effect is not { } effect)
        {
            if (!definition.Manual)
            {
                Error("$.effect", "An object without an effect must be manual.");
            }

            return errors;
        }

        // Items and events act when used or drawn; effects, special rolls and achievements wait for their trigger.
        var waits = kind is ObjectKind.Effect or ObjectKind.SpecialRoll or ObjectKind.Achievement;
        if (waits && effect.Trigger is null)
        {
            Error("$.effect.trigger", $"A {kind} needs a trigger.");
        }

        if (!waits && effect.Trigger is not null)
        {
            Error("$.effect.trigger", $"A {kind} acts when it is used or drawn, without a trigger.");
        }

        CheckEffect(effect, "$.effect", Error);
        return errors;
    }

    /// <summary>The errors of an effect block; used for objects and for a poll's result.</summary>
    public static void CheckEffect(EffectSpec effect, string path, Action<string, string> error)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(error);

        if (effect.Intercept is not null && effect.Trigger != Trigger.HostileIncoming)
        {
            error($"{path}.intercept", "An interception answers the «hostileIncoming» trigger.");
        }

        var hasActions = effect.Actions.Count > 0;
        if (hasActions && effect.Outcomes is not null)
        {
            error(path, "An effect has either actions or outcomes, not both.");
        }

        if (!hasActions && effect.Outcomes is null && effect.Intercept is null)
        {
            error(path, "An effect needs actions, outcomes or an interception.");
        }

        if (effect.Target is { } target && target.Among is not null && target.Selector is not (Selector.Chosen or Selector.RandomActive))
        {
            error($"{path}.target.among", "«among» narrows only «chosen» and «randomActive».");
        }

        if (effect.Duration is { } duration)
        {
            CheckDuration(duration, $"{path}.duration", error);
        }

        CheckActions(effect.Actions, $"{path}.actions", rolled: false, error);

        if (effect.Outcomes is { } outcomes)
        {
            CheckOutcomes(outcomes, $"{path}.outcomes", error);
        }
    }

    public static IReadOnlyList<ContentError> Check(ZoneDefinition zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var errors = new List<ContentError>();
        void Error(string path, string message) => errors.Add(new ContentError(path, message));
        CheckId(zone.Id, "$.id", Error);
        CheckText(zone.Name, MaxNameLength, "$.name", Error);
        if (zone.DiceModifier is { } modifier)
        {
            CheckDiceValue(modifier.Stage, modifier.Value, "$.diceModifier.value", Error);
        }

        if (zone.DropPenaltyMultiplier is <= 0 || zone.ShopPriceMultiplier is <= 0)
        {
            Error("$", "Multipliers are above 0.");
        }

        return errors;
    }

    public static IReadOnlyList<ContentError> Check(IReadOnlyList<CellDefinition> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var errors = new List<ContentError>();
        void Error(string path, string message) => errors.Add(new ContentError(path, message));
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            var path = $"$[{i}]";
            CheckId(cell.Id, $"{path}.id", Error);
            var missing = cell.Type switch
            {
                ContentCellType.Shop when string.IsNullOrWhiteSpace(cell.Grants) => "grants",
                ContentCellType.Event when string.IsNullOrWhiteSpace(cell.Deck) => "deck",
                ContentCellType.Teleport when string.IsNullOrWhiteSpace(cell.To) => "to",
                ContentCellType.PointsBonus when cell.Amount is null or 0 => "amount",
                _ => null,
            };
            if (missing is not null)
            {
                Error($"{path}.{missing}", $"A {cell.Type} cell needs «{missing}».");
            }
        }

        if (cells.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            Error("$", $"Cell «{duplicate.Key}» is listed twice.");
        }

        return errors;
    }

    public static IReadOnlyList<ContentError> Check(PollDefinition poll)
    {
        ArgumentNullException.ThrowIfNull(poll);
        var errors = new List<ContentError>();
        void Error(string path, string message) => errors.Add(new ContentError(path, message));
        CheckText(poll.Question, MaxNameLength * 2, "$.question", Error);
        CheckOptions(poll.Options, "$.options", Error);
        if (poll.ClosesInHours <= 0)
        {
            Error("$.closesInHours", "A poll closes after a positive number of hours.");
        }

        if (poll.OnResult is { } result)
        {
            CheckEffect(result, "$.onResult", Error);
        }

        return errors;
    }

    public static IReadOnlyList<ContentError> Check(ChallengeDefinition challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var errors = new List<ContentError>();
        void Error(string path, string message) => errors.Add(new ContentError(path, message));
        CheckId(challenge.Id, "$.id", Error);
        CheckText(challenge.Name, MaxNameLength, "$.name", Error);
        CheckText(challenge.Description, MaxDescriptionLength, "$.description", Error);
        if (challenge.Reward.Coins is null or <= 0)
        {
            Error("$.reward.coins", "A challenge pays a positive number of coins.");
        }

        return errors;
    }

    private static void CheckActions(Kernel.EquatableArray<ActionSpec> actions, string path, bool rolled, Action<string, string> error)
    {
        var chose = false;
        for (var i = 0; i < actions.Count; i++)
        {
            var at = $"{path}[{i}]";
            var values = actions[i] switch
            {
                MoveAction a => [("steps", a.Steps)],
                ChangeResourceAction a => [("amount", a.Amount)],
                RollAction a => [("dice", a.Dice)],
                ModifyDiceAction a => [("value", a.Value)],
                _ => Array.Empty<(string, ContentValue)>(),
            };
            foreach (var (field, value) in values)
            {
                CheckReference(value, $"{at}.{field}", rolled, chose, error);
            }

            switch (actions[i])
            {
                case ChangeResourceAction change when string.IsNullOrWhiteSpace(change.Resource):
                    error($"{at}.resource", "A resource is «points», «coins» or another key.");
                    break;
                case RollAction roll when roll.Dice.Kind != ContentValueKind.Dice:
                    error($"{at}.dice", "A roll needs dice such as «1d6».");
                    break;
                case ModifyDiceAction modify:
                    CheckDiceValue(modify.Stage, modify.Value, $"{at}.value", error);
                    break;
                case ModifyNextRollAction next when next.Filter is null && next.ChoiceCount is null && next.RunCondition is null:
                    error(at, "«modifyNextRoll» changes a filter, the choice count or the run condition.");
                    break;
                case ModifyNextRollAction { ChoiceCount: < 2 }:
                    error($"{at}.choiceCount", "A choice is among at least 2 games.");
                    break;
                case GiveObjectAction give:
                    CheckId(give.ObjectId, $"{at}.objectId", error);
                    foreach (var (name, value) in give.Params ?? new Dictionary<string, string>())
                    {
                        if (value.StartsWith('$') || value.StartsWith("-$", StringComparison.Ordinal))
                        {
                            CheckReference(ContentValue.TryParse(value), $"{at}.params.{name}", rolled, chose, error);
                        }
                    }

                    break;
                case TransformObjectAction { Mode: TransformMode.Transform, Into: null or "" }:
                    error($"{at}.into", "«transform» names the object to turn into.");
                    break;
                case TransformObjectAction { Mode: TransformMode.Annotate, Note: null or "" }:
                    error($"{at}.note", "«annotate» needs a note.");
                    break;
                case DrawEventAction draw when string.IsNullOrWhiteSpace(draw.Deck):
                    error($"{at}.deck", "A deck is «good», «bad», «special», «risky», «zone» or a deck id.");
                    break;
                case SpinWheelAction spin when string.IsNullOrWhiteSpace(spin.Wheel):
                    error($"{at}.wheel", "A wheel id is required.");
                    break;
                case TeleportAction teleport when string.IsNullOrWhiteSpace(teleport.Cell):
                    error($"{at}.cell", "A cell id or «nearestShortcut» is required.");
                    break;
                case RequestChoiceAction request:
                    CheckText(request.Prompt, MaxNameLength * 2, $"{at}.prompt", error);
                    CheckOptions(request.Options, $"{at}.options", error);
                    break;
            }

            rolled |= actions[i] is RollAction;
            chose |= actions[i] is RequestChoiceAction;
        }
    }

    // $roll needs a roll before it (an action or the outcome's own roll); $choice a choice before it. $result and
    // parameters come from outside the effect: a poll, a giveObject's params.
    private static void CheckReference(ContentValue? value, string path, bool rolled, bool chose, Action<string, string> error)
    {
        if (value is null)
        {
            error(path, "A reference is «$name» or «-$name».");
            return;
        }

        if (value.Kind != ContentValueKind.Reference)
        {
            return;
        }

        if (value.Name == "roll" && !rolled)
        {
            error(path, "«$roll» needs a «roll» action before it.");
        }

        if (value.Name == "choice" && !chose)
        {
            error(path, "«$choice» needs a «requestChoice» action before it.");
        }
    }

    // Counts, sides, rerolls and bounds are plain numbers; additions may be dice or a reference.
    private static void CheckDiceValue(DiceStage stage, ContentValue value, string path, Action<string, string> error)
    {
        if (stage is DiceStage.Add)
        {
            return;
        }

        if (value.Kind != ContentValueKind.Number)
        {
            error(path, $"The «{stage}» stage takes a whole number.");
        }
        else if (stage is DiceStage.Sides or DiceStage.Multiply && value.Number < 1)
        {
            error(path, $"The «{stage}» stage takes a number of 1 or more.");
        }
    }

    private static void CheckOutcomes(OutcomesSpec outcomes, string path, Action<string, string> error)
    {
        if (outcomes.Roll.Kind != ContentValueKind.Dice || outcomes.Roll.Negative)
        {
            error($"{path}.roll", "Outcomes are picked by dice such as «1d6».");
            return;
        }

        var low = outcomes.Roll.Count;
        var high = outcomes.Roll.Count * outcomes.Roll.Sides;
        var covered = new HashSet<int>();
        for (var i = 0; i < outcomes.Cases.Count; i++)
        {
            var @case = outcomes.Cases[i];
            var at = $"{path}.cases[{i}]";
            if (@case.From > @case.To || @case.From < low || @case.To > high)
            {
                error(at, $"A case covers a range within {low}–{high}.");
                continue;
            }

            if (Enumerable.Range(@case.From, @case.To - @case.From + 1).Any(n => !covered.Add(n)))
            {
                error(at, "Cases do not overlap.");
            }

            CheckActions(@case.Actions, $"{at}.actions", rolled: true, error);
        }

        if (covered.Count != high - low + 1)
        {
            error($"{path}.cases", $"Cases cover every result {low}–{high}.");
        }
    }

    private static void CheckDuration(DurationSpec duration, string path, Action<string, string> error)
    {
        var set = new[] { duration.Uses is not null, duration.Hours is not null, duration.Runs is not null, duration.UntilTriggered is not null }
            .Count(x => x);
        if (set != 1)
        {
            error(path, "A duration is exactly one of «uses», «hours», «runs», «untilTriggered».");
        }

        if (duration.Uses is <= 0 || duration.Hours is <= 0 || duration.Runs is <= 0 || duration.UntilTriggered == false)
        {
            error(path, "A duration is positive.");
        }
    }

    private static void CheckOptions(ChoiceOptionsSpec options, string path, Action<string, string> error)
    {
        var fromList = options.List is { Count: > 0 };
        if (options.From is null == !fromList)
        {
            error(path, "Options come either «from» a source or as a «list».");
        }
    }

    private static void CheckId(string id, string path, Action<string, string> error)
    {
        if (id.Length > MaxIdLength || !IdPattern().IsMatch(id))
        {
            error(path, $"An id is lowercase words with hyphens, up to {MaxIdLength} characters (for example «lucky-die»).");
        }
    }

    private static void CheckText(string text, int max, string path, Action<string, string> error)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > max)
        {
            error(path, $"1–{max} characters.");
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdPattern();
}

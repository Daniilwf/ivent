using System.Text.Json;
using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;
using GameEvent.Infrastructure.Seasons;

namespace GameEvent.Web.Seasons;

/// <summary>
/// A condition of an event on a run (SPEC «Условия от ивентов», D-507, D-821): whose it is, the kind of the deck it came
/// from (null for an item's), whether another player put it, and its bounds of difficulty (null when not set).
/// </summary>
public sealed record RunConditionView(string ObjectId, string Name, EventKind? Kind, bool Hostile, Difficulty? DifficultyAtLeast, Difficulty? DifficultyAtMost);

/// <summary>The check of a completed run's conditions (D-507, D-821): met at the difficulty that counts, waived by the admin.</summary>
public sealed record RunConditionCheckView(bool Met, bool Waived);

/// <summary>
/// An event I drew whose effect waits for my future steps (SPEC «Ивенты копятся», D-505, D-823): a change of my next roll
/// or of my next throw; <c>kind</c> — the kind of the deck it came from. <c>waitingForRoll</c> and
/// <c>waitingForThrow</c> describe what still waits, not every action in the original description. Always set by the server.
/// </summary>
public sealed record WaitingEventView(string ObjectId, string Name, string Description, EventKind Kind, bool? WaitingForRoll = null, bool? WaitingForThrow = null);

/// <summary>
/// The proof of an IRL effect (D-509, D-820), for its owner only: <c>status</c>, the own uploaded pictures, the https link
/// (never opened by the server), the admin's comment after a reject and when it was sent.
/// </summary>
public sealed record IrlProofView(IrlProofStatus Status, IReadOnlyList<Files.FileLinkView> Files, string? Link, string? Comment, DateTimeOffset SubmittedAt);

/// <summary>
/// Displays projected wallet modifiers, run snapshots and manual effects. Conditions and waiting events follow the
/// events flag; the metadata of an existing manual effect remains available independently of it.
/// </summary>
internal sealed class EventTurnViews(bool enabled, Func<string, ObjectCard?> cardOf)
{
    /// <summary>The changes of my next roll that are conditions (D-507): they wait for my next roll.</summary>
    public IReadOnlyList<RunConditionView> WaitingConditions(PlayerEconomy? wallet) =>
        enabled && wallet is not null
            ? [.. wallet.NextRoll.Where(m => m.RunCondition is not null)
                .Select(m => Condition(m.ObjectId, m.RunCondition!, m.Hostile, m.EventKind))]
            : [];

    /// <summary>
    /// My drawn events waiting for my future steps (D-505, D-823): the changes of the next roll (conditions apart — they are
    /// <see cref="WaitingConditions"/>) and of the next throw that came from an event, one row per event and kind.
    /// </summary>
    public IReadOnlyList<WaitingEventView> WaitingEvents(PlayerEconomy? wallet)
    {
        if (!enabled || wallet is null)
        {
            return [];
        }

        var rollEvents = wallet.NextRoll
            .Where(m => m.RunCondition is null && m.EventKind is not null)
            .Select(m => (m.ObjectId, Kind: m.EventKind!.Value))
            .ToArray();
        var throwEvents = wallet.NextDice.Where(d => d.EventKind is not null)
            .Select(d => (d.ObjectId, Kind: d.EventKind!.Value)).ToArray();
        var rollKeys = rollEvents.ToHashSet();
        var throwKeys = throwEvents.ToHashSet();
        var fromEvents = rollEvents.Concat(throwEvents).Distinct();
        return [.. fromEvents.Select(e =>
        {
            var definition = cardOf(e.ObjectId);
            return new WaitingEventView(e.ObjectId, definition?.Name ?? e.ObjectId, definition?.Description ?? "", e.Kind, rollKeys.Contains(e), throwKeys.Contains(e));
        })];
    }

    /// <summary>The roll fixes its conditions in its snapshot before the player picks a starting difficulty.</summary>
    public IReadOnlyList<RunConditionView> ConditionsOf(RunSnapshot? snapshot) =>
        enabled && snapshot is not null
            ? [.. snapshot.Conditions.Select(c => Condition(c.ObjectId, c.Condition, c.Hostile, c.EventKind))]
            : [];

    /// <summary>The check of a run's conditions; null for a run without any or with events off.</summary>
    public RunConditionCheckView? CheckOf(RunSnapshot snapshot, RunConditionState? check) =>
        enabled && snapshot.Conditions.Count > 0 && check is not null
            ? new RunConditionCheckView(check.Met, check.Waived)
            : null;

    /// <summary>In the projection, the boolean column is authoritative (D-810).</summary>
    public RunConditionCheckView? CheckOf(RunSnapshot snapshot, RunRecord run) =>
        enabled && snapshot.Conditions.Count > 0 && run.ConditionsJson is { } json
            ? new RunConditionCheckView(run.ConditionMet == true, JsonSerializer.Deserialize<RunConditionState>(json, EngineJson.Options)!.Waived)
            : null;

    public ManualEffectView ManualEffectOf(PendingManualEffectRecord effect)
    {
        var card = effect.ObjectId is { } id ? cardOf(id) : null;
        var proof = effect.IrlProofJson is { } json
            ? JsonSerializer.Deserialize<IrlProofState>(json, EngineJson.Options)
            : null;
        return new ManualEffectView(effect.Id, effect.DrawEvent, effect.Source, effect.ObjectId, card?.Proof == ContentProof.Media,
            proof is null ? null : new IrlProofView(proof.Status, [.. proof.Files.Select(Files.FileLinkView.Of)], proof.Link, proof.Comment, proof.SubmittedAt),
            card?.Name ?? effect.ObjectId, effect.ObjectId is null ? null : card?.Description ?? "");
    }

    private RunConditionView Condition(string objectId, ConditionSpec condition, bool hostile, EventKind? kind) =>
        new(objectId, cardOf(objectId)?.Name ?? objectId, kind, hostile, condition.DifficultyAtLeast, condition.DifficultyAtMost);
}

/// <summary>The live suspension includes its action and target, which the projected choice alone does not describe.</summary>
internal sealed class EventQuestionViews(SeasonState? state, Guid playerId)
{
    /// <summary>The games named by the current requestChoice, for one batched title lookup by its caller.</summary>
    public IReadOnlyList<Guid> GameOptionIds(PendingChoice? choice)
    {
        if (SuspensionOf(choice) is not { } suspended
            || ActionOf(suspended) is not RequestChoiceAction { Options.From: ChoiceSource.Games })
        {
            return [];
        }

        return [.. suspended.Options
            .Select(o => Guid.TryParseExact(o.Id, "N", out var id) ? (Guid?)id : null)
            .OfType<Guid>()
            .Distinct()];
    }

    /// <summary>
    /// An answer's name follows the question's source, never the shape of its id: a literal may look like a player's id.
    /// Missing players, games and objects keep the answer's original id, which remains what the engine accepts.
    /// </summary>
    public string OptionLabel(PendingChoice choice, string optionId, IReadOnlyDictionary<Guid, string> gameTitles)
    {
        if (SuspensionOf(choice) is not { } suspended || !suspended.Options.Any(o => o.Id == optionId))
        {
            return optionId;
        }

        if (suspended.Step == SuspendedStep.Target)
        {
            return PlayerName(optionId);
        }

        return ActionOf(suspended) switch
        {
            RequestChoiceAction { Options.From: ChoiceSource.Players } => PlayerName(optionId),
            RequestChoiceAction { Options.From: ChoiceSource.Games } when Guid.TryParseExact(optionId, "N", out var gameId)
                => gameTitles.GetValueOrDefault(gameId, optionId),
            TakeObjectAction { Pick: Pick.Chosen, All: false } take => ObjectName(suspended, take, optionId),
            _ => optionId,
        };
    }

    /// <summary>Only an actual chosen move gives its answers a direction; a literal «forward» stays a literal.</summary>
    public ChoiceDirection? OptionDirection(PendingChoice choice, string optionId) =>
        SuspensionOf(choice) is { } suspended && suspended.Options.Any(o => o.Id == optionId)
            && ActionOf(suspended) is MoveAction { Direction: MoveDirection.Chosen }
            ? optionId switch
            {
                "forward" => ChoiceDirection.Forward,
                "back" => ChoiceDirection.Back,
                _ => null,
            }
            : null;

    /// <summary>
    /// The question of a suspended event I am asked (D-511, D-822): the event and its prompt — the branch's question or the
    /// asking action's; null for a choice of another kind.
    /// </summary>
    public (string ObjectId, string Name, string? Prompt, string Description)? QuestionOf(PendingChoice? choice)
    {
        if (SuspensionOf(choice) is not { } suspended)
        {
            return null;
        }

        var definition = state!.Catalog.Find(suspended.ObjectId);
        var prompt = suspended.Step switch
        {
            SuspendedStep.Outcome => definition?.Effect?.Outcomes?.Choice?.Prompt,
            SuspendedStep.Action => (ActionOf(suspended) as RequestChoiceAction)?.Prompt,
            _ => null,
        };
        return (suspended.ObjectId, definition?.Name ?? suspended.ObjectId, prompt, definition?.Description ?? "");
    }

    private SuspendedEffect? SuspensionOf(PendingChoice? choice) =>
        choice is { Kind: ChoiceKind.Effect } && state is not null && state.Players.TryGetValue(playerId, out var player)
            ? player.Suspended.FirstOrDefault(s => s.ChoiceId == choice.ChoiceId)
            : null;

    // Published events ask only in their top-level action list, never inside an outcomes case (D-511).
    private ActionSpec? ActionOf(SuspendedEffect suspended) =>
        suspended.Step == SuspendedStep.Action && state?.Catalog.Find(suspended.ObjectId)?.Effect is { } effect
            && suspended.NextAction - 1 is var at && at >= 0 && at < effect.Actions.Count
            ? effect.Actions[at]
            : null;

    private string PlayerName(string optionId) =>
        Guid.TryParseExact(optionId, "N", out var id) && state is not null && state.Players.TryGetValue(id, out var player)
            ? player.Name
            : optionId;

    private string ObjectName(SuspendedEffect suspended, TakeObjectAction action, string optionId)
    {
        // Giving always takes the user's own object; the other modes take from the action's self or effect target.
        var owner = action.Mode == TakeMode.Give || action.Target == ActionTarget.Self ? playerId : suspended.TargetId ?? playerId;
        return Guid.TryParseExact(optionId, "N", out var instanceId) && state is not null
            && state.Players.TryGetValue(owner, out var player) && player.Wallet.Find(instanceId) is { } item
            ? state.Catalog.Find(item.ObjectId)?.Name ?? optionId
            : optionId;
    }

}

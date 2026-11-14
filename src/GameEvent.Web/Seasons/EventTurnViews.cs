using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;

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
/// or of my next throw; <c>kind</c> — the kind of the deck it came from.
/// </summary>
public sealed record WaitingEventView(string ObjectId, string Name, string Description, EventKind Kind);

/// <summary>
/// The proof of an IRL effect (D-509, D-820), for its owner only: <c>status</c>, the own uploaded pictures, the https link
/// (never opened by the server), the admin's comment after a reject and when it was sent.
/// </summary>
public sealed record IrlProofView(IrlProofStatus Status, IReadOnlyList<Files.FileLinkView> Files, string? Link, string? Comment, DateTimeOffset SubmittedAt);

/// <summary>
/// The events part of my turn, read from the season folded from the log (stage 5): the projection tables of 5.16 are not
/// read here (D-824). Every list is empty and every mark off with <c>features.events</c> off.
/// </summary>
internal sealed class EventTurnViews(SeasonState? state, Guid playerId)
{
    private bool On => state is { } s && s.Rules.Features.Events;

    /// <summary>The changes of my next roll that are conditions (D-507): they wait for my next roll.</summary>
    public IReadOnlyList<RunConditionView> WaitingConditions()
    {
        if (!On || !state!.Players.TryGetValue(playerId, out var player))
        {
            return [];
        }

        return [.. player.Wallet.NextRoll
            .Where(m => m.RunCondition is not null)
            .Select(m => Condition(m.ObjectId, m.RunCondition!, m.Hostile, m.EventKind))];
    }

    /// <summary>
    /// My drawn events waiting for my future steps (D-505, D-823): the changes of the next roll (conditions apart — they are
    /// <see cref="WaitingConditions"/>) and of the next throw that came from an event, one row per event and kind.
    /// </summary>
    public IReadOnlyList<WaitingEventView> WaitingEvents()
    {
        if (!On || !state!.Players.TryGetValue(playerId, out var player))
        {
            return [];
        }

        var wallet = player.Wallet;
        var fromEvents = wallet.NextRoll
            .Where(m => m.RunCondition is null && m.EventKind is not null)
            .Select(m => (m.ObjectId, Kind: m.EventKind!.Value))
            .Concat(wallet.NextDice.Where(d => d.EventKind is not null).Select(d => (d.ObjectId, Kind: d.EventKind!.Value)))
            .Distinct();
        return [.. fromEvents.Select(e =>
        {
            var definition = state.Catalog.Find(e.ObjectId);
            return new WaitingEventView(e.ObjectId, definition?.Name ?? e.ObjectId, definition?.Description ?? "", e.Kind);
        })];
    }

    /// <summary>The conditions a roll fixed on a run (D-507); none with events off or for an unknown run.</summary>
    public IReadOnlyList<RunConditionView> ConditionsOf(Guid runId) =>
        On && state!.Runs.TryGetValue(runId, out var run)
            ? [.. run.Snapshot.Conditions.Select(c => Condition(c.ObjectId, c.Condition, c.Hostile, c.EventKind))]
            : [];

    /// <summary>The check of a run's conditions; null for a run without any or with events off.</summary>
    public RunConditionCheckView? CheckOf(Guid runId) =>
        On && state!.Runs.TryGetValue(runId, out var run) && run.Snapshot.Conditions.Count > 0 && run.Condition is { } check
            ? new RunConditionCheckView(check.Met, check.Waived)
            : null;

    /// <summary>Whether a manual effect is an IRL one (D-509): its object asks for a media proof.</summary>
    public bool IsIrl(Guid effectId) =>
        state is not null && state.ManualEffects.TryGetValue(effectId, out var effect) && IrlProofs.IsIrl(state, effect);

    /// <summary>The owner's view of an IRL effect's proof; null before one is sent.</summary>
    public IrlProofView? ProofOf(Guid effectId) =>
        state is not null && state.ManualEffects.TryGetValue(effectId, out var effect) && effect.IrlProof is { } proof
            ? new IrlProofView(proof.Status, [.. proof.Files.Select(Files.FileLinkView.Of)], proof.Link, proof.Comment, proof.SubmittedAt)
            : null;

    /// <summary>
    /// The question of a suspended event I am asked (D-511, D-822): the event and its prompt — the branch's question or the
    /// asking action's; null for a choice of another kind.
    /// </summary>
    public (string ObjectId, string Name, string? Prompt)? QuestionOf(PendingChoice? choice)
    {
        if (choice is not { Kind: ChoiceKind.Effect } || state is null || !state.Players.TryGetValue(playerId, out var player)
            || player.Suspended.FirstOrDefault(s => s.ChoiceId == choice.ChoiceId) is not { } suspended)
        {
            return null;
        }

        var definition = state.Catalog.Find(suspended.ObjectId);
        var prompt = suspended.Step switch
        {
            SuspendedStep.Outcome => definition?.Effect?.Outcomes?.Choice?.Prompt,
            SuspendedStep.Action when definition?.Effect is { } effect && suspended.NextAction - 1 is var at && at >= 0 && at < effect.Actions.Count
                => (effect.Actions[at] as RequestChoiceAction)?.Prompt,
            _ => null,
        };
        return (suspended.ObjectId, definition?.Name ?? suspended.ObjectId, prompt);
    }

    private RunConditionView Condition(string objectId, ConditionSpec condition, bool hostile, EventKind? kind) =>
        new(objectId, state!.Catalog.Find(objectId)?.Name ?? objectId, kind, hostile, condition.DifficultyAtLeast, condition.DifficultyAtMost);
}

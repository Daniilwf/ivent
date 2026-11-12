using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Effects;

/// <summary>What a suspended effect asks (D-511): its target, the case of its outcomes, or the answer of an action.</summary>
public enum SuspendedStep
{
    /// <summary>The target <c>chosen</c>: an option is a player's id; the effect runs on the answer from its start.</summary>
    Target,

    /// <summary><c>outcomes.choice</c>: an option is a case; the case's actions run from the first.</summary>
    Outcome,

    /// <summary>
    /// An action that asks (<c>requestChoice</c>, <c>takeObject pick: chosen</c>, <c>move direction: chosen</c>): it is
    /// played again with the answer, then the actions from <see cref="SuspendedEffect.NextAction"/> go on.
    /// </summary>
    Action,
}

/// <summary>
/// An effect waiting for its player's answer (D-511, D-563): the object, what it asks, the target it acts on (null while
/// the target is the question), the first action to run after the answer (zero-based), the level of the effect chain it
/// stopped at, the options the server counted, the <c>$roll</c> and <c>$choice</c> computed so far, and when the timer
/// answers for the player (D-560; null — never). <see cref="Kind"/> is the kind of the deck it came from: several suspended
/// effects ask one by one, the special first (D-505, D-562).
/// </summary>
public sealed record SuspendedEffect(
    Guid ChoiceId,
    string ObjectId,
    EventKind? Kind,
    SuspendedStep Step,
    Guid? TargetId,
    int NextAction,
    int Depth,
    EquatableArray<ChoiceOption> Options,
    int? Roll,
    string? Choice,
    DateTimeOffset? DueAt);

/// <summary>
/// An event that needs an answer stopped and waits for it on the server (D-511): <see cref="PlayerId"/> answers with the
/// common <see cref="MakeChoice"/> naming <see cref="ChoiceId"/>. The fields are those of <see cref="SuspendedEffect"/>.
/// </summary>
[EventType("effect-suspended")]
public sealed record EffectSuspended(
    Guid PlayerId,
    Guid ChoiceId,
    string ObjectId,
    EventKind? Kind,
    SuspendedStep Step,
    Guid? TargetId,
    int NextAction,
    int Depth,
    EquatableArray<ChoiceOption> Options,
    int? Roll,
    string? Choice,
    DateTimeOffset? DueAt) : IGameEvent;

/// <summary>
/// A suspended effect goes on with <see cref="OptionId"/> (D-511): the player's answer, or the timer's random pick past the
/// answer timeout (D-560). The events of the rest of the effect follow in the same command.
/// </summary>
[EventType("effect-resumed")]
public sealed record EffectResumed(Guid PlayerId, Guid ChoiceId, string OptionId) : IGameEvent;

/// <summary>
/// Effects waiting for an answer (D-511, D-560…D-564): the pending choice of <see cref="ChoiceKind.Effect"/>, the answer
/// that goes on in its own command with a new chain budget at the level it stopped at, the timer's answer, the discard.
/// </summary>
internal static class Suspensions
{
    /// <summary>
    /// D-505: special, good, bad; risky ones and draws of no kind after them. <c>events.applyOrder</c> of D-505 is not in
    /// the rules yet (task 5.4): until then the default order (D-565).
    /// </summary>
    private static int Rank(EventKind? kind) => kind switch
    {
        EventKind.Special => 0,
        EventKind.Good => 1,
        EventKind.Bad => 2,
        EventKind.Risky => 3,
        _ => 4,
    };

    /// <summary>The suspended effect the player is asked now; null when the pending choice is not one.</summary>
    public static SuspendedEffect? Current(SeasonPlayer player) =>
        player.Choice is { Kind: ChoiceKind.Effect } choice ? player.Suspended.FirstOrDefault(s => s.ChoiceId == choice.ChoiceId) : null;

    /// <summary>
    /// The pending choice after a change (D-562): a choice of another kind stays (an effect waits behind a branch); otherwise
    /// the first open suspension in order, or none.
    /// </summary>
    public static SeasonPlayer Refresh(SeasonPlayer player)
    {
        if (player.Choice is { Kind: not ChoiceKind.Effect })
        {
            return player;
        }

        var next = player.Suspended.Select((s, i) => (Effect: s, Index: i)).OrderBy(x => Rank(x.Effect.Kind)).ThenBy(x => x.Index).Select(x => x.Effect).FirstOrDefault();
        var choice = next is null ? null : new PendingChoice(next.ChoiceId, ChoiceKind.Effect, next.Options);
        return player.Choice == choice ? player : player with { Choice = choice };
    }

    /// <summary>The chain level a command's continuations stopped at: its reactions go on from there (D-511, EV31).</summary>
    public static int Level(SeasonState state, IEnumerable<IGameEvent> events) =>
        events.OfType<EffectResumed>()
            .Select(r => state.Players.GetValueOrDefault(r.PlayerId)?.Suspended.FirstOrDefault(s => s.ChoiceId == r.ChoiceId)?.Depth ?? 0)
            .DefaultIfEmpty(0)
            .Max();

    /// <summary>
    /// The answer of <paramref name="player"/> to the effect asked now (D-511, D-563): the season running or closing (the
    /// question belongs to a step made before the deadline, Н-13), events on (D-564), the choice pending, the option one of
    /// the snapshot. Then the rest of the effect runs in this command.
    /// </summary>
    public static Decision Answer(SeasonState state, SeasonPlayer player, PendingChoice choice, MakeChoice command, EngineContext context)
    {
        if (state.Status is not (SeasonStatus.Active or SeasonStatus.Closing))
        {
            return Decision.Reject(RejectionCodes.SeasonNotActive, $"The season is {state.Status}.");
        }

        if (!state.Rules.Features.Events)
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "Events are switched off: the answer waits until they are back (D-564).");
        }

        if (choice.ChoiceId != command.ChoiceId)
        {
            return Decision.Reject(RejectionCodes.NoPendingChoice, $"Choice {command.ChoiceId} is not pending.");
        }

        if (choice.Options.All(o => o.Id != command.OptionId))
        {
            return Decision.Reject(RejectionCodes.UnknownChoiceOption, $"The option is not one of choice {command.ChoiceId}.");
        }

        return Decision.Accept(Continue(state, context, player.PlayerId, Current(player)!, command.OptionId));
    }

    /// <summary>
    /// The timer answers the questions past their timeout (D-560): a random option, written as the answer, so a replay never
    /// picks again. Nothing while events are off (D-564).
    /// </summary>
    public static List<IGameEvent> Timeouts(SeasonState state, EngineContext context)
    {
        var events = new List<IGameEvent>();
        if (!state.Rules.Features.Events)
        {
            return events;
        }

        var now = context.Clock.UtcNow;
        foreach (var playerId in state.Players.Keys)
        {
            if (Current(state.Players[playerId]) is { DueAt: { } due } waiting && due <= now)
            {
                var option = waiting.Options[context.Random.NextInt(0, waiting.Options.Count)].Id;
                var resumed = Continue(state, context, playerId, waiting, option);
                events.AddRange(resumed);
                state = resumed.Aggregate(state, SeasonEngine.Apply);
            }
        }

        return events;
    }

    /// <summary>When the timer answers for <paramref name="player"/>; null when nothing waits or events are off (D-420).</summary>
    public static DateTimeOffset? Due(SeasonPlayer player, Features features) => features.Events ? Current(player)?.DueAt : null;

    /// <summary>
    /// The rest of a suspended effect with <paramref name="optionId"/> (D-511): the computed references come back, a target
    /// asked is checked again (Н-10: one no longer allowed is skipped, the answer is not refused), after the deadline the
    /// token does not move (Н-13). The first finisher's effects are frozen (D-510): the answer closes the question only.
    /// </summary>
    public static List<IGameEvent> Continue(SeasonState state, EngineContext context, Guid playerId, SuspendedEffect suspended, string optionId)
    {
        var resumed = new EffectResumed(playerId, suspended.ChoiceId, optionId);
        var after = SeasonEngine.Apply(state, resumed);
        if (after.Catalog.Find(suspended.ObjectId) is not { Effect: not null } definition || Targets.IsFirst(after, playerId))
        {
            return [resumed];
        }

        var run = new EffectRun(
            after, context, playerId, definition.Id, hostile: false, parameters: null,
            choices: suspended.Step == SuspendedStep.Target ? [] : [optionId], eventsBefore: 1)
        {
            Asks = new Asking(suspended.Kind, suspended.Depth),
            Resuming = true,
            PositionLocked = after.Status != SeasonStatus.Active || SeasonSetup.IsPastDeadline(after, context.Clock.UtcNow),
        };
        run.Resume(definition, suspended, optionId);
        return [resumed, .. run.Events];
    }

    public static SeasonState Apply(SeasonState state, EffectSuspended e) =>
        Update(state, e.PlayerId, p => p with
        {
            Suspended =
            [
                .. p.Suspended,
                new SuspendedEffect(e.ChoiceId, e.ObjectId, e.Kind, e.Step, e.TargetId, e.NextAction, e.Depth, e.Options, e.Roll, e.Choice, e.DueAt),
            ],
        });

    public static SeasonState Apply(SeasonState state, EffectResumed e) => Close(state, e.PlayerId, e.ChoiceId);

    /// <summary>Whether <paramref name="choiceId"/> is a suspended effect of the player (a discard of it closes the effect, Н-12).</summary>
    public static bool IsSuspended(SeasonState state, Guid playerId, Guid choiceId) =>
        state.Players.TryGetValue(playerId, out var player) && player.Suspended.Any(s => s.ChoiceId == choiceId);

    /// <summary>The effect is answered or discarded: the next one, if any, is asked (D-562).</summary>
    public static SeasonState Close(SeasonState state, Guid playerId, Guid choiceId) =>
        Update(state, playerId, p => p with
        {
            Suspended = [.. p.Suspended.Where(s => s.ChoiceId != choiceId)],
            Choice = p.Choice?.ChoiceId == choiceId ? null : p.Choice,
        });

    private static SeasonState Update(SeasonState state, Guid playerId, Func<SeasonPlayer, SeasonPlayer> change) =>
        state with { Players = state.Players.SetItem(playerId, Refresh(change(state.Players[playerId]))) };
}

/// <summary>An effect that may ask its player and wait (D-511): the kind of its deck and the chain level it runs at.</summary>
internal sealed record Asking(EventKind? Kind, int Depth);

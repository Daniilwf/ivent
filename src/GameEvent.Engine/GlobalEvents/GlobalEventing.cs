using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.GlobalEvents;

/// <summary>
/// Global events (SPEC «Общие ивенты», D-517…D-519, D-750…): the admin launches an event of the content on everyone for a
/// term, with a goal or without. Its effect acts once, at the launch, settled for everyone (D-537) — or, when its first
/// action asks the voters, at the close of that poll with its result (EV23). A goal counts the completions in the event's
/// window (the HowLongToBeat hours of the snapshot or the runs, a coop run once); a reject takes a contribution back, an
/// hours correction changes it by the difference; reached once, its reward is settled for everyone and stays. The term
/// ends by the timer, the admin or the deadline. Progress follows the command's events as a settlement (D-413): the chain
/// limit never cuts it.
/// </summary>
internal static class GlobalEventing
{
    public static Decision Decide(SeasonState state, LaunchGlobalEvent command, EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(command.ObjectId);

        // In closing the deadline has passed: no new global events (D-516)
        if (state.IsCreated && state.Status == SeasonStatus.Closing)
        {
            return Decision.Reject(RejectionCodes.SeasonDeadlinePassed, "The deadline has passed: no new global events this season.");
        }

        if (SeasonSetup.RequireActive(state) is { } inactive)
        {
            return inactive;
        }

        if (!IsOn(state))
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "Global events are off in this season.");
        }

        var now = context.Clock.UtcNow;
        if (SeasonSetup.IsPastDeadline(state, now))
        {
            return Decision.Reject(RejectionCodes.SeasonDeadlinePassed, "The deadline has passed: no new global events this season.");
        }

        if (state.Catalog.Live(command.ObjectId) is not { } definition)
        {
            return Decision.Reject(RejectionCodes.ObjectUnknown, $"«{command.ObjectId}» is not in the season's content.");
        }

        if (definition.Kind != ObjectKind.Event)
        {
            return Decision.Reject(RejectionCodes.GlobalEventNotEvent, $"«{command.ObjectId}» is not an event: only events are launched on everyone.");
        }

        var vote = Vote(definition);
        var errors = ContentValidator.CheckGlobalEvent(definition, command.DurationHours, command.Goal, state.Catalog).ToList();
        if (vote is { } asked && asked.ClosesInHours >= command.DurationHours)
        {
            errors.Add(new ContentError("$.durationHours", "The vote of a global event closes before the event ends."));
        }

        if (errors.Count > 0)
        {
            return Decision.Reject(RejectionCodes.ContentInvalid, string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}")));
        }

        // A vote needs polls (D-512): with them off nobody would be asked
        if (vote is not null && !state.Rules.Features.Polls)
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "The event asks the voters: polls are off in this season.");
        }

        var max = state.Rules.GlobalEvents?.MaxActive ?? 0;
        if (state.GlobalEvents.Values.Count(g => g.RunsAt(now)) >= max)
        {
            return Decision.Reject(RejectionCodes.GlobalEventTooMany, $"{max} global event(s) run already: end one first.");
        }

        var kind = KindOf(state, definition.Id);
        var launched = new GlobalEventLaunched(context.Ids.NewId(), definition.Id, definition, now, now.AddHours(command.DurationHours))
        {
            Goal = command.Goal,
            Kind = kind,
        };
        List<IGameEvent> events = [launched];
        if (definition.Effect is not { } effect || definition.Manual)
        {
            // A banner (and a goal) only: a manual event is played by hand, outside the engine (D-752)
            return Decision.Accept(events);
        }

        var after = SeasonEngine.Apply(state, launched);
        if (vote is { } spec && effect.Actions[0] is RequestChoiceAction question)
        {
            // The voters answer first; the effect goes to everyone with the result (EV23, D-753)
            var options = Polling.Options(after, context, question.Options, target: null);
            if (options.Count == 0)
            {
                return Decision.Reject(RejectionCodes.PollNoOptions, "The event's vote would offer nothing to choose from.");
            }

            events.Add(new PollStarted(
                context.Ids.NewId(),
                PollSource.GlobalEvent,
                question.Prompt.Trim(),
                options,
                spec.Voters,
                spec.Anonymous,
                Polling.LiveCounts(state, spec.Anonymous, spec.ShowLiveCounts),
                now,
                now.AddHours(spec.ClosesInHours))
            {
                ObjectId = definition.Id,
                Kind = kind,
                GlobalEventId = launched.GlobalEventId,
            });
            return Decision.Accept(events);
        }

        // Once, on everyone, in a settlement of its own (D-517, D-537)
        events.Add(ForAllSettlements.Queue(after, context, ForAllSource.GlobalEventLaunch, launched.GlobalEventId, definition.Id, effect, kind: kind));
        return Decision.Accept(events);
    }

    public static Decision Decide(SeasonState state, EndGlobalEvent command, EngineContext context)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (state.Status is not (SeasonStatus.Active or SeasonStatus.Closing))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: global events are over.");
        }

        if (!IsOn(state))
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "Global events are off in this season.");
        }

        if (state.GlobalEvents.GetValueOrDefault(command.GlobalEventId) is not { } global)
        {
            return Decision.Reject(RejectionCodes.GlobalEventUnknown, $"Global event {command.GlobalEventId} is not in the season.");
        }

        return global.Status != GlobalEventStatus.Active
            ? Decision.Reject(RejectionCodes.GlobalEventEnded, "The global event has ended already.")
            : Decision.Accept([.. End(state, global, global.EndsAt <= context.Clock.UtcNow ? GlobalEventEndReason.Timer : GlobalEventEndReason.Admin)]);
    }

    /// <summary>Whether the mechanic plays: <c>features.globalEvents</c> with events (D-517, D-420).</summary>
    public static bool IsOn(SeasonState state) => IsOn(state.Rules.Features);

    private static bool IsOn(Rulesets.Features features) => features is { GlobalEvents: true, Events: true };

    /// <summary>
    /// Why an undo that leaves <paramref name="after"/> must be refused (D-750, D-751): more global events running than
    /// <c>globalEvents.maxActive</c> (an end undone after a later launch), or one running or counting an unchecked run while
    /// the mechanic is off (undone after the switch-off); null when it may go.
    /// </summary>
    public static Decision? RefuseUndo(SeasonState before, SeasonState after, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (!after.IsCreated || after.Ruleset is not { } rules)
        {
            return null;
        }

        var running = after.GlobalEvents.Values.Count(g => g.RunsAt(now));
        if (IsOn(rules.Features) && running > before.GlobalEvents.Values.Count(g => g.RunsAt(now)) && running > (rules.GlobalEvents?.MaxActive ?? 0))
        {
            return Decision.Reject(RejectionCodes.GlobalEventTooMany, "The undo would bring back a global event over globalEvents.maxActive: end one first.");
        }

        return !IsOn(rules.Features) && InPlay(after) && !InPlay(before)
            ? Decision.Reject(RejectionCodes.GlobalEventsInPlay, "The undo would bring back a global event while global events are off: switch them on first.")
            : null;
    }

    /// <summary>The vote a global event asks first (EV23): its first action's <c>requestChoice.vote</c>; null when none.</summary>
    public static PollVoteSpec? Vote(ObjectDefinition definition) =>
        definition is { Manual: false, Effect.Actions: [RequestChoiceAction { Vote: { } vote }, ..] } ? vote : null;

    // The kind of the first deck (by id) holding the event (D-706): its objects and changes keep their place in the order
    private static Rulesets.EventKind? KindOf(SeasonState state, string objectId) =>
        state.Catalog.Wheels.Values
            .Where(w => w.Deck is not null && w.Entries.Any(e => string.Equals(e.ObjectId, objectId, StringComparison.Ordinal)))
            .Select(w => w.Deck)
            .FirstOrDefault();

    // The end: its open poll is cancelled — the effect it waited for no longer comes (D-753)
    private static IEnumerable<IGameEvent> End(SeasonState state, GlobalEventState global, GlobalEventEndReason reason)
    {
        yield return new GlobalEventEnded(global.GlobalEventId, reason);
        if (global.PollId is { } pollId && state.Polls.GetValueOrDefault(pollId) is { Status: PollStatus.Open })
        {
            yield return new PollCancelled(pollId);
        }
    }

    /// <summary>The timer ends the global events whose term is up, oldest first; nothing while the mechanic is off (D-420).</summary>
    public static IEnumerable<IGameEvent> Due(SeasonState state, EngineContext context)
    {
        if (!IsOn(state))
        {
            return [];
        }

        var now = context.Clock.UtcNow;
        return state.GlobalEvents.Values
            .Where(g => g.Status == GlobalEventStatus.Active && g.EndsAt <= now)
            .OrderBy(g => g.EndsAt).ThenBy(g => g.GlobalEventId)
            .SelectMany(g => End(state, g, GlobalEventEndReason.Timer))
            .ToList();
    }

    /// <summary>When the next running global event ends (the scheduler's moment, D-727); null when none, or while off.</summary>
    public static DateTimeOffset? NextEnd(SeasonState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Ruleset is { } rules && IsOn(rules.Features)
            ? state.GlobalEvents.Values.Where(g => g.Status == GlobalEventStatus.Active).Select(g => (DateTimeOffset?)g.EndsAt).Min()
            : null;
    }

    /// <summary>The deadline ends every running global event (D-516); their polls are cancelled with the other polls.</summary>
    public static IEnumerable<IGameEvent> EndAtDeadline(SeasonState state) =>
        state.GlobalEvents.Values.Where(g => g.Status == GlobalEventStatus.Active).Select(g => new GlobalEventEnded(g.GlobalEventId, GlobalEventEndReason.Deadline));

    /// <summary>
    /// The result of a global event's vote (EV23, D-753): its effect goes to everyone, the result answering its question.
    /// Nothing once the event has ended.
    /// </summary>
    public static IEnumerable<IGameEvent> Voted(SeasonState state, EngineContext context, Poll poll, string result)
    {
        if (poll.GlobalEventId is not { } id || state.GlobalEvents.GetValueOrDefault(id) is not { Status: GlobalEventStatus.Active } global
            || global.Definition.Effect is not { } effect)
        {
            return [];
        }

        return [ForAllSettlements.Queue(state, context, ForAllSource.GlobalEventLaunch, id, global.ObjectId, effect, kind: global.Kind, answers: [result])];
    }

    /// <summary>
    /// What <paramref name="e"/> (the state already holds it) changes in the goals (D-518, D-546 (6)): a completion in a running
    /// event's window contributes, a reject takes the contribution back, an hours correction changes it by the difference; a
    /// goal reached for the first time — its reward queued for everyone (D-537).
    /// </summary>
    public static IReadOnlyList<IGameEvent> Settle(SeasonState state, IGameEvent e, EngineContext context)
    {
        var events = new List<IGameEvent>();
        switch (e)
        {
            // Н-23 needs no check here: a completion after the deadline is refused before it is written (D-755)
            case RunCompleted completed:
                foreach (var global in Goals(state).Where(g =>
                    g.Status == GlobalEventStatus.Active && g.InWindow(completed.CompletedAt) && g.Contributions.All(c => c.RunId != completed.RunId)))
                {
                    var amount = global.Goal!.Metric == GoalMetric.Hours ? completed.Hours : 1;
                    if (amount > 0)
                    {
                        events.AddRange(Progress(state, context, global, completed.RunId, GlobalProgressReason.Completed, amount, mayReach: true));
                    }
                }

                break;
            case ProofRejected rejected:
                foreach (var global in Goals(state))
                {
                    if (global.Contributions.FirstOrDefault(c => c.RunId == rejected.RunId) is { } contribution)
                    {
                        events.AddRange(Progress(state, context, global, rejected.RunId, GlobalProgressReason.Rejected, -contribution.Amount, mayReach: false));
                    }
                }

                break;
            case RunHoursCorrected corrected:
                foreach (var global in Goals(state).Where(g => g.Goal!.Metric == GoalMetric.Hours))
                {
                    if (global.Contributions.FirstOrDefault(c => c.RunId == corrected.RunId) is { } contribution && corrected.NewHours != contribution.Amount)
                    {
                        var running = global.RunsAt(context.Clock.UtcNow) && !SeasonSetup.IsPastDeadline(state, context.Clock.UtcNow);
                        events.AddRange(Progress(
                            state, context, global, corrected.RunId, GlobalProgressReason.HoursCorrected, corrected.NewHours - contribution.Amount, mayReach: running));
                    }
                }

                break;
            default:
                break;
        }

        return events;
    }

    private static IEnumerable<GlobalEventState> Goals(SeasonState state) =>
        state.GlobalEvents.Values.Where(g => g.Goal is not null).OrderBy(g => g.StartedAt).ThenBy(g => g.GlobalEventId);

    private static IEnumerable<IGameEvent> Progress(
        SeasonState state, EngineContext context, GlobalEventState global, Guid runId, GlobalProgressReason reason, decimal delta, bool mayReach)
    {
        var total = global.Progress + delta;
        var changed = new GlobalProgressChanged(global.GlobalEventId, runId, reason, delta, total);
        yield return changed;
        var goal = global.Goal!;
        if (!mayReach || global.Reached || total < goal.Target)
        {
            yield break;
        }

        var reached = new GlobalGoalReached(global.GlobalEventId, runId);
        yield return reached;
        if (goal.Reward is { } reward)
        {
            // The targets are counted now (D-700): a player added since the launch is one of them
            var after = SeasonEngine.Apply(SeasonEngine.Apply(state, changed), reached);
            yield return ForAllSettlements.Queue(after, context, ForAllSource.GoalReward, global.GlobalEventId, global.ObjectId, reward, kind: global.Kind);
        }
    }

    /// <summary>
    /// Whether switching the mechanic off would strand something (D-420, D-751): an event runs, or a contribution stands on a
    /// run whose proof is not approved yet, so a reject could no longer take it back.
    /// </summary>
    public static bool InPlay(SeasonState state) =>
        state.GlobalEvents.Values.Any(g => g.Status == GlobalEventStatus.Active
            || g.Contributions.Any(c => state.Runs.TryGetValue(c.RunId, out var run) && run.Status == RunStatus.Completed && run.Proof?.Status is not ProofStatus.Approved));

    public static SeasonState Apply(SeasonState state, GlobalEventLaunched e) =>
        state with
        {
            GlobalEvents = state.GlobalEvents.SetItem(
                e.GlobalEventId,
                new GlobalEventState(e.GlobalEventId, e.ObjectId, e.Definition, e.StartedAt, e.EndsAt, GlobalEventStatus.Active, [], Reached: false)
                {
                    Goal = e.Goal,
                    Kind = e.Kind,
                }),
        };

    // A global event's poll is remembered by the event it waits for
    public static SeasonState Apply(SeasonState state, PollStarted e) =>
        e.GlobalEventId is { } id && state.GlobalEvents.GetValueOrDefault(id) is { } global
            ? state with { GlobalEvents = state.GlobalEvents.SetItem(id, global with { PollId = e.PollId }) }
            : state;

    public static SeasonState Apply(SeasonState state, GlobalEventEnded e) =>
        Update(state, e.GlobalEventId, g => g with { Status = GlobalEventStatus.Ended, EndReason = e.Reason });

    public static SeasonState Apply(SeasonState state, GlobalProgressChanged e) =>
        Update(state, e.GlobalEventId, g =>
        {
            var amount = (g.Contributions.FirstOrDefault(c => c.RunId == e.RunId)?.Amount ?? 0) + e.Delta;
            var others = g.Contributions.Where(c => c.RunId != e.RunId);
            return g with { Contributions = Ordered(e.Reason == GlobalProgressReason.Rejected ? others : others.Append(new GlobalContribution(e.RunId, amount))) };
        });

    public static SeasonState Apply(SeasonState state, GlobalGoalReached e) => Update(state, e.GlobalEventId, g => g with { Reached = true });

    /// <summary>Contributions in one order whatever came first — by run — so an undo leaves the state a replay without it gives.</summary>
    public static EquatableArray<GlobalContribution> Ordered(IEnumerable<GlobalContribution> contributions) => [.. contributions.OrderBy(c => c.RunId)];

    private static SeasonState Update(SeasonState state, Guid id, Func<GlobalEventState, GlobalEventState> change) =>
        state with { GlobalEvents = state.GlobalEvents.SetItem(id, change(state.GlobalEvents[id])) };
}

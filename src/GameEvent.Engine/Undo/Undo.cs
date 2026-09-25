using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Undo;

/// <summary>One command of the season log: its id and its events in log order (D-104).</summary>
public sealed record LoggedCommand(Guid CommandId, EquatableArray<IGameEvent> Events);

/// <summary>
/// The admin undoes a whole earlier command (SPEC «Откат действий», D-20, D-104). Refused while later commands depend
/// on it: the refusal lists them (<see cref="Rejection.Related"/>), to be undone first or fixed by hand.
/// </summary>
public sealed record UndoCommand(Guid TargetCommandId, string Comment) : ICommand;

/// <summary>
/// The season's own fields as they were before the undone command; <see cref="Ruleset"/> only when the command changed
/// the rules (null — they stay).
/// </summary>
public sealed record SeasonFields(SeasonStatus Status, DateTimeOffset? Deadline, string Name, Rulesets.Ruleset? Ruleset);

/// <summary>
/// A command is undone (D-104): what it touched gets back the values it had before — players, runs and pending manual
/// effects it changed, those it created are removed, the season's fields if it changed them. Nothing later touched them
/// (that is when an undo is allowed), so this equals its events inverted in reverse order. The counters of finishes and
/// points changes are not wound back: numbers are never reused.
/// </summary>
[EventType("command-undone")]
public sealed record CommandUndone(
    Guid CommandId,
    string Comment,
    EquatableArray<SeasonPlayer> Players,
    EquatableArray<Guid> RemovedPlayers,
    EquatableArray<RunState> Runs,
    EquatableArray<Guid> RemovedRuns,
    EquatableArray<PendingManualEffect> Effects,
    EquatableArray<Guid> RemovedEffects,
    SeasonFields? Season) : IGameEvent;

internal static class Undoing
{
    public static Decision Decide(SeasonState state, UndoCommand command, EngineContext context)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        // After the finish the results are fixed (D-101).
        if (SeasonSetup.IsOver(state))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: results are fixed.");
        }

        if (string.IsNullOrWhiteSpace(command.Comment))
        {
            return Decision.Reject(RejectionCodes.CommentRequired, "Every admin change explains itself in the public log.");
        }

        if (command.Comment.Length > Limits.MaxCommentLength)
        {
            return Decision.Reject(RejectionCodes.CommentTooLong, $"The comment is limited to {Limits.MaxCommentLength} characters.");
        }

        if (context.History is not { } history)
        {
            return Decision.Reject(RejectionCodes.UndoNoHistory, "An undo needs the season's command history (the queue gives it).");
        }

        var index = -1;
        for (var i = 0; i < history.Count && index < 0; i++)
        {
            index = history[i].CommandId == command.TargetCommandId ? i : -1;
        }

        if (index < 0)
        {
            return Decision.Reject(RejectionCodes.UndoUnknownCommand, $"Command {command.TargetCommandId} is not in this season's log.");
        }

        var target = history[index];
        if (target.Events.Any(e => e is SeasonCreated or CommandUndone))
        {
            return Decision.Reject(RejectionCodes.UndoNotUndoable, "The season's creation and undos are not undone; do the action again instead.");
        }

        var undone = UndoneCommands(history);
        if (undone.Contains(target.CommandId))
        {
            return Decision.Reject(RejectionCodes.UndoAlreadyUndone, $"Command {target.CommandId} is already undone.");
        }

        // Replay the log command by command: what each one touched is the difference it made.
        var before = SeasonState.Empty;
        SeasonState? beforeTarget = null;
        ISet<string>? targetKeys = null;
        var dependents = new List<Guid>();
        for (var i = 0; i < history.Count; i++)
        {
            var after = history[i].Events.Aggregate(before, SeasonEngine.Apply);
            if (i == index)
            {
                beforeTarget = before;
                targetKeys = Keys(before, after, history[i].Events);
            }
            else if (i > index && !undone.Contains(history[i].CommandId) && !history[i].Events.Any(e => e is CommandUndone)
                && (targetKeys!.Contains(SeasonKey) || Keys(before, after, history[i].Events).Overlaps(targetKeys)))
            {
                // Every later command was decided under the season's status, deadline and rules: after a change of
                // those, all of them depend on it (D-104).
                dependents.Add(history[i].CommandId);
            }

            before = after;
        }

        if (dependents.Count > 0)
        {
            return Decision.Reject(
                RejectionCodes.UndoDependents,
                $"{dependents.Count} later command(s) depend on it: undo them first or fix by hand.",
                [.. dependents]);
        }

        var compensation = Compensation(beforeTarget!, state, target, command.Comment);
        if (compensation.Season is { } season && season.Deadline != state.Deadline && season.Deadline is { } deadline
            && deadline <= context.Clock.UtcNow)
        {
            return Decision.Reject(RejectionCodes.SeasonDeadlineInPast, "The deadline it would bring back has passed.");
        }

        // Statuses go back only while the turns they reopen are still allowed by the clock (D-101, D-104): a season
        // closed at or after its deadline stays closed.
        if (compensation.Season is { Status: SeasonStatus.Active } reopened && state.Status != SeasonStatus.Active
            && SeasonSetup.IsPastDeadline(state with { Deadline = reopened.Deadline }, context.Clock.UtcNow))
        {
            return Decision.Reject(RejectionCodes.SeasonDeadlinePassed, "The deadline has passed: the season stays closed.");
        }

        return Decision.Accept(compensation);
    }

    private const string SeasonKey = "season";

    public static SeasonState Apply(SeasonState state, CommandUndone e)
    {
        var players = state.Players.RemoveRange(e.RemovedPlayers).SetItems(e.Players.Select(p => KeyValuePair.Create(p.PlayerId, p)));
        var runs = state.Runs.RemoveRange(e.RemovedRuns).SetItems(e.Runs.Select(r => KeyValuePair.Create(r.RunId, r)));
        var effects = state.ManualEffects.RemoveRange(e.RemovedEffects).SetItems(e.Effects.Select(x => KeyValuePair.Create(x.EffectId, x)));
        var restored = state with { Players = players, Runs = runs, ManualEffects = effects };
        if (e.Season is not { } season)
        {
            return restored;
        }

        // The rules come back as a new version, so a version number always names one set of rules (D-104).
        return restored with
        {
            Status = season.Status,
            Deadline = season.Deadline,
            Name = season.Name,
            Ruleset = season.Ruleset ?? state.Ruleset,
            RulesetVersion = season.Ruleset is null || season.Ruleset == state.Ruleset ? state.RulesetVersion : state.RulesetVersion + 1,
        };
    }

    /// <summary>The commands undone so far: the targets of the log's <see cref="CommandUndone"/> events.</summary>
    public static HashSet<Guid> UndoneCommands(IEnumerable<LoggedCommand> history) =>
        [.. history.SelectMany(c => c.Events.OfType<CommandUndone>()).Select(u => u.CommandId)];

    /// <summary>
    /// What a command touched (D-20, D-104): the players, runs and manual effects it changed, created or removed; the games
    /// its events name or its runs play; the season's fields; the finish order.
    /// </summary>
    public static ISet<string> Keys(SeasonState before, SeasonState after, IEnumerable<IGameEvent> events)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in Changed(before.Players, after.Players))
        {
            keys.Add($"player:{id}");
            foreach (var player in new[] { before.Players.GetValueOrDefault(id), after.Players.GetValueOrDefault(id) }.OfType<SeasonPlayer>())
            {
                // A game offered or among the choice's options is taken by this player (D-06, D-91)
                if (player.Offer is { } offer)
                {
                    keys.Add($"game:{offer.GameId}");
                }

                foreach (var option in player.Choice?.Options ?? [])
                {
                    if (option.Game is { } game)
                    {
                        keys.Add($"game:{game.GameId}");
                    }
                }

                // A finisher's changes move the finish order, bonuses and the freeze (Q-3, Q-4, D-99)
                if (player.Finish is not null)
                {
                    keys.Add(FinishKey);
                }
            }
        }

        foreach (var id in Changed(before.Runs, after.Runs))
        {
            keys.Add($"run:{id}");
            foreach (var run in new[] { before.Runs.GetValueOrDefault(id), after.Runs.GetValueOrDefault(id) }.OfType<RunState>())
            {
                keys.Add($"game:{run.GameId}");
                keys.Add($"player:{run.PlayerId}");
                if (before.Players.GetValueOrDefault(run.PlayerId)?.Finish is not null || after.Players.GetValueOrDefault(run.PlayerId)?.Finish is not null)
                {
                    keys.Add(FinishKey);
                }
            }
        }

        foreach (var id in Changed(before.ManualEffects, after.ManualEffects))
        {
            keys.Add($"effect:{id}");
        }

        if (before.Status != after.Status || before.Deadline != after.Deadline || before.Name != after.Name
            || before.Ruleset != after.Ruleset || before.Result != after.Result)
        {
            keys.Add(SeasonKey);
        }

        foreach (var e in events)
        {
            if (e is PlayerFinished or PlayerFinishRevoked or PlayerFrozen or FinishSurplusChanged
                or Scoring.PointsChanged { Reason: Scoring.PointsReason.FinishBonus or Scoring.PointsReason.FinishBonusRevoked })
            {
                keys.Add(FinishKey);
            }

            foreach (var (kind, id) in GameIds.Of(e).Concat(GameIds.Own(e)))
            {
                keys.Add($"{kind}:{id}");
            }
        }

        return keys;
    }

    private const string FinishKey = "finishOrder";

    private static CommandUndone Compensation(SeasonState before, SeasonState now, LoggedCommand target, string comment)
    {
        var after = target.Events.Aggregate(before, SeasonEngine.Apply);
        var players = Changed(before.Players, after.Players).ToList();
        var runs = Changed(before.Runs, after.Runs).ToList();
        var effects = Changed(before.ManualEffects, after.ManualEffects).ToList();
        var seasonChanged = before.Status != after.Status || before.Deadline != after.Deadline || before.Name != after.Name
            || before.Ruleset != after.Ruleset;

        // Present now but not before: created by the command, removed by the undo.
        return new CommandUndone(
            target.CommandId,
            comment.Trim(),
            [.. players.Where(before.Players.ContainsKey).Select(id => before.Players[id])],
            [.. players.Where(id => !before.Players.ContainsKey(id) && now.Players.ContainsKey(id))],
            [.. runs.Where(before.Runs.ContainsKey).Select(id => before.Runs[id])],
            [.. runs.Where(id => !before.Runs.ContainsKey(id) && now.Runs.ContainsKey(id))],
            [.. effects.Where(before.ManualEffects.ContainsKey).Select(id => before.ManualEffects[id])],
            [.. effects.Where(id => !before.ManualEffects.ContainsKey(id) && now.ManualEffects.ContainsKey(id))],
            seasonChanged ? new SeasonFields(before.Status, before.Deadline, before.Name, before.Ruleset == after.Ruleset ? null : before.Rules) : null);
    }

    private static IEnumerable<Guid> Changed<T>(
        System.Collections.Immutable.ImmutableSortedDictionary<Guid, T> before, System.Collections.Immutable.ImmutableSortedDictionary<Guid, T> after) =>
        ReferenceEquals(before, after)
            ? []
            : before.Keys.Union(after.Keys)
                .Where(id => !(before.TryGetValue(id, out var was) && after.TryGetValue(id, out var now) && (ReferenceEquals(was, now) || Equals(was, now))))
                .Order();
}

/// <summary>
/// What an event names beyond its own player and run: every <c>GameId</c> and <c>GameIds</c> (a roll's misses, a choice's
/// options) and a proof's <c>WitnessId</c>, however deep. Found by reflection once per event type, so a new event with a
/// game in it is covered without a list (D-104).
/// </summary>
internal static class GameIds
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> s_properties = new();

    public static IEnumerable<(string Kind, Guid Id)> Of(object value)
    {
        var found = new List<(string, Guid)>();
        Collect(value, found, depth: 0);
        return found;
    }

    private static readonly ConcurrentDictionary<Type, (string Kind, PropertyInfo Property)[]> s_own = new();

    /// <summary>
    /// The player, run and manual effect an event itself is about (its own <c>PlayerId</c>, <c>RunId</c>, <c>EffectId</c>).
    /// A command touches them even when the state comes out the same — a proof sent again: its event still stands in the
    /// log, so an earlier command on that run cannot be undone past it (C13, BUGS.md C13-1).
    /// </summary>
    public static IEnumerable<(string Kind, Guid Id)> Own(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var properties = s_own.GetOrAdd(value.GetType(), t =>
        [
            .. new[] { ("player", "PlayerId"), ("run", "RunId"), ("effect", "EffectId") }
                .Select(x => (x.Item1, Property: t.GetProperty(x.Item2, BindingFlags.Public | BindingFlags.Instance)))
                .Where(x => x.Property is not null && (x.Property.PropertyType == typeof(Guid) || x.Property.PropertyType == typeof(Guid?)))
                .Select(x => (x.Item1, x.Property!)),
        ]);
        foreach (var (kind, property) in properties)
        {
            if (property.GetValue(value) is Guid id)
            {
                yield return (kind, id);
            }
        }
    }

    private static void Collect(object? value, List<(string, Guid)> found, int depth)
    {
        if (value is null || depth > 6 || value is string || value.GetType().IsPrimitive || value is Guid or DateTimeOffset or decimal)
        {
            return;
        }

        if (value is IEnumerable items)
        {
            foreach (var item in items)
            {
                Collect(item, found, depth + 1);
            }

            return;
        }

        foreach (var property in s_properties.GetOrAdd(value.GetType(), t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)))
        {
            if (property.GetIndexParameters().Length > 0 || property.DeclaringType?.Assembly != typeof(GameIds).Assembly)
            {
                continue;
            }

            var inner = property.GetValue(value);
            switch (property.Name)
            {
                case "GameId" when inner is Guid game:
                    found.Add(("game", game));
                    break;
                case "GameIds" when inner is IEnumerable<Guid> games:
                    found.AddRange(games.Select(g => ("game", g)));
                    break;
                case "WitnessId" when inner is Guid witness:
                    found.Add(("player", witness));
                    break;
                default:
                    if (inner is not null && (inner is IEnumerable || inner.GetType().Assembly == typeof(GameIds).Assembly))
                    {
                        Collect(inner, found, depth + 1);
                    }

                    break;
            }
        }
    }
}

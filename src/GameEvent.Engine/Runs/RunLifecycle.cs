using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Runs;

internal static class RunLifecycle
{
    public static Decision Decide(SeasonState state, StartRun command, EngineContext context)
    {
        if (Check(state, command.PlayerId, TurnPhase.Rolling) is { } rejection)
        {
            return rejection;
        }

        var player = state.Players[command.PlayerId];
        var offer = player.Offer ?? throw new InvalidOperationException($"Player {player.PlayerId} is Rolling without an offer.");
        return Decision.Accept(new RunStarted(
            context.Ids.NewId(), player.PlayerId, offer.GameId, offer.Snapshot, offer.RolledAt, context.Clock.UtcNow));
    }

    public static Decision Decide(SeasonState state, CompleteRun command, EngineContext context)
    {
        if (Check(state, command.PlayerId, TurnPhase.Playing) is { } rejection)
        {
            return rejection;
        }

        var player = state.Players[command.PlayerId];
        var run = state.Runs[player.ActiveRunId ?? throw new InvalidOperationException($"Player {player.PlayerId} is Playing without a run.")];

        // Hours come from the snapshot; the player's estimate counts only when the pool had none (D-44).
        var hours = run.Snapshot.Hours is > 0 ? run.Snapshot.Hours : command.EstimatedHours;
        if (hours is null)
        {
            return Decision.Reject(RejectionCodes.HoursRequired, "The game has no hours: give an estimate with a source.");
        }

        if (hours <= 0)
        {
            return Decision.Reject(RejectionCodes.InvalidHours, $"Hours must be positive, got {hours}.");
        }

        var count = CompletionRoll.Count(hours.Value, run.Snapshot.DiceCount);
        var die = CompletionRoll.DieFor(command.Difficulty, run.Snapshot.DieByDifficulty);
        var dice = CompletionRoll.Roll(count, die.Sides, context.Random);
        var sum = dice.Sum(d => d.Value);

        var events = new List<IGameEvent>
        {
            new RunCompleted(run.RunId, player.PlayerId, command.Difficulty, hours.Value, context.Clock.UtcNow),
            new CompletionRolled(run.RunId, player.PlayerId, dice),
        };

        if (sum != 0)
        {
            events.Add(new PointsChanged(player.PlayerId, sum, PointsReason.CompletionRoll, run.RunId));
        }

        var path = Movement.Forward(state.Map, player.CellId, sum);
        if (path.Count > 0)
        {
            events.Add(new PlayerMoved(player.PlayerId, player.CellId, path[^1], sum, [.. path], MoveReason.CompletionRoll, run.RunId));
        }

        return Decision.Accept(events);
    }

    public static SeasonState Apply(SeasonState state, RunStarted e)
    {
        var run = new RunState(
            e.RunId, e.PlayerId, e.GameId, RunStatus.Playing, e.Snapshot, e.RolledAt, e.StartedAt, Difficulty: null, Hours: null, Dice: []);
        var player = state.Players[e.PlayerId] with { Phase = TurnPhase.Playing, Offer = null, ActiveRunId = e.RunId };
        return state with { Runs = state.Runs.Add(e.RunId, run), Players = state.Players.SetItem(e.PlayerId, player) };
    }

    public static SeasonState Apply(SeasonState state, RunCompleted e)
    {
        var run = state.Runs[e.RunId] with { Status = RunStatus.Completed, Difficulty = e.Difficulty, Hours = e.Hours };
        var player = state.Players[e.PlayerId] with { Phase = TurnPhase.Idle, ActiveRunId = null };
        return state with { Runs = state.Runs.SetItem(e.RunId, run), Players = state.Players.SetItem(e.PlayerId, player) };
    }

    public static SeasonState Apply(SeasonState state, CompletionRolled e) =>
        state with { Runs = state.Runs.SetItem(e.RunId, state.Runs[e.RunId] with { Dice = e.Dice }) };

    private static Decision? Check(SeasonState state, Guid playerId, TurnPhase required)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (!state.Players.TryGetValue(playerId, out var player))
        {
            return Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {playerId} is not in the season.");
        }

        return player.Phase == required
            ? null
            : Decision.Reject(RejectionCodes.WrongPhase, $"Needs phase {required}, player is {player.Phase}.");
    }
}

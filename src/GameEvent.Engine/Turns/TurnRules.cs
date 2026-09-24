using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Turns;

/// <summary>
/// The turn state machine (SPEC «Игровой цикл», K-5) as one table: which phase each player turn command needs.
/// A pending choice blocks every turn command except <see cref="MakeChoice"/>. Admin commands are not turn commands.
/// Checks run in a fixed order: season active, player known, pending choice, phase. The active run limit is the
/// phase itself: a run starts only from Rolling, and RulesetSupport keeps the limit at 1 (D-91).
/// </summary>
internal static class TurnRules
{
    // MakeChoice needs a pending choice rather than a phase; a choice exists only while Rolling.
    private static readonly Dictionary<Type, TurnPhase> s_phaseFor = new()
    {
        [typeof(RollGame)] = TurnPhase.Idle,
        [typeof(StartRun)] = TurnPhase.Rolling,
        [typeof(CompleteRun)] = TurnPhase.Playing,
    };

    /// <summary>Rejects <paramref name="command"/> of <paramref name="playerId"/> when the turn does not allow it; null when it does.</summary>
    public static Decision? Check(SeasonState state, Guid playerId, ICommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (SeasonSetup.RequireActive(state) is { } inactive)
        {
            return inactive;
        }

        if (!state.Players.TryGetValue(playerId, out var player))
        {
            return Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {playerId} is not in the season.");
        }

        if (command is MakeChoice)
        {
            return player.Choice is null
                ? Decision.Reject(RejectionCodes.NoPendingChoice, "The player has no pending choice.")
                : null;
        }

        if (player.Choice is not null)
        {
            return Decision.Reject(RejectionCodes.ChoicePending, "The player must make the pending choice first.");
        }

        var required = s_phaseFor.TryGetValue(command.GetType(), out var phase)
            ? phase
            : throw new ArgumentException($"{command.GetType().Name} is not a turn command.", nameof(command));
        if (player.Phase != required)
        {
            return Decision.Reject(RejectionCodes.WrongPhase, $"Needs phase {required}, player is {player.Phase}.");
        }

        return null;
    }
}

using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Turns;

/// <summary>
/// The turn state machine (SPEC «Игровой цикл», K-5) as one table: which phase each player turn command needs.
/// A pending choice blocks every turn command except <see cref="MakeChoice"/>. Admin commands are not turn commands.
/// Checks run in a fixed order: season active, player known, pending choice, phase, active run limit.
/// </summary>
internal static class TurnRules
{
    /// <summary>Rejects <paramref name="command"/> of <paramref name="playerId"/> when the turn does not allow it; null when it does.</summary>
    public static Decision? Check(SeasonState state, Guid playerId, ICommand command) =>
        throw new NotImplementedException("C4");
}

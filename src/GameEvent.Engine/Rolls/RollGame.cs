using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Rolls;

/// <summary>
/// "Spin the wheel": the server picks a category among those with available games, then a game (D-05).
/// The player moves Idle → Rolling with the game offered and reserved.
/// </summary>
public sealed record RollGame(Guid PlayerId) : ICommand;

/// <summary>Why the wheel landed on a game the player cannot take.</summary>
public enum RollMissReason
{
    /// <summary>Someone already completed it this season: «Уже прошёл Вася».</summary>
    CompletedInSeason,

    /// <summary>Someone is playing it or has it offered: «Сейчас играет Вася».</summary>
    BeingPlayed,
}

/// <summary>A wheel miss recorded in the log; not a reroll.</summary>
public sealed record RollMiss(Guid GameId, RollMissReason Reason, Guid ByPlayerId);

/// <summary>The rolled game waiting for the player to start it, with rules fixed at roll time.</summary>
public sealed record RollOffer(Guid GameId, RunSnapshot Snapshot, DateTimeOffset RolledAt);

[EventType("game-rolled")]
public sealed record GameRolled(
    Guid PlayerId,
    string Category,
    EquatableArray<RollMiss> Misses,
    Guid GameId,
    RunSnapshot Snapshot,
    DateTimeOffset RolledAt) : IGameEvent;

internal static class Rolling
{
    public static Decision Decide(SeasonState state, RollGame command, EngineContext context) =>
        throw new NotImplementedException("B1");

    public static SeasonState Apply(SeasonState state, GameRolled e) =>
        throw new NotImplementedException("B1");
}

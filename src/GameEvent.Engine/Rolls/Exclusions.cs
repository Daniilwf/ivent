using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Rolls;

/// <summary>Why a game never comes to a player again this season (SPEC «Статусы игры в сезоне»).</summary>
public enum ExclusionReason
{
    /// <summary>«Уже проходил»: the player played it before the event (D-08: no limit, on their honour, logged).</summary>
    AlreadyPlayed,

    /// <summary>The player dropped it (or the admin turned their tech reroll into a drop).</summary>
    Dropped,

    /// <summary>The player tech-rerolled it.</summary>
    TechRerolled,
}

/// <summary>
/// A game excluded for one player of the season; the player never sees it on the wheel (D-05: skipped silently).
/// <see cref="Seasons.SeasonPlayer.Exclusions"/> keeps them ordered by game id, one per game.
/// </summary>
public sealed record GameExclusion(Guid GameId, ExclusionReason Reason);

/// <summary>
/// «Уже проходил» on the offered game or on one option of a pending choice: the game is excluded for the player and
/// the wheel spins again for free — not a reroll, it spends nothing (D-07). A pending choice is rolled anew as a whole.
/// </summary>
public sealed record DeclareAlreadyPlayed(Guid PlayerId, Guid GameId) : ICommand;

/// <summary>
/// The game is excluded for the player. If it was offered to them (or among their pending options) the offer or the
/// choice is dropped and the player is Idle, so a free roll written by the same command can follow.
/// </summary>
[EventType("game-excluded")]
public sealed record GameExcluded(Guid PlayerId, Guid GameId, ExclusionReason Reason) : IGameEvent;

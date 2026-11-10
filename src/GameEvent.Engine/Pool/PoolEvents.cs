using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Pool;

// The global log's pool events (D-119): the shared pool of games and the category wheel live across seasons. A season's
// roll reads the pool as it is then, and a run keeps its own snapshot of the hours (invariant 5).

/// <summary>
/// A game's card as the pool keeps it: what a player sees and what the roll reads. <see cref="Note"/> — the challenge or a
/// remark (SPEC «Челлендж из заметки»); <see cref="CompletionCondition"/> — what counts as finishing an endless or
/// multiplayer game (SPEC «условие прохождения»); <see cref="IsCoop"/> — a game played together.
/// </summary>
public sealed record GameCard(
    string Title,
    EquatableArray<string> Tags,
    decimal? Hours,
    int? Year,
    string? SteamAppId,
    Guid? CoverFileId,
    string? Note,
    bool IsCoop,
    string? CompletionCondition = null);

/// <summary>
/// A player or the admin added a game to the pool; <see cref="AuthorId"/> — the account that added it;
/// <see cref="AuthorName"/> — who added it as the imported table names them, with no account (F1, D-125).
/// </summary>
[EventType("game-added")]
public sealed record GameAdded(Guid GameId, GameCard Card, Guid? AuthorId, string? AuthorName = null) : IGameEvent;

/// <summary>The admin changed a game's card (the whole card, as it is now).</summary>
[EventType("game-changed")]
public sealed record GameChanged(Guid GameId, GameCard Card) : IGameEvent;

/// <summary>
/// The admin removed a game from the pool (soft): no roll gets it; runs that had it keep it (SPEC «Удаление мягкое»).
/// <see cref="Reason"/> — why, as the admin wrote it (D-208); none in v1, written before a reason was required.
/// </summary>
[EventType("game-deleted", version: 2)]
public sealed record GameDeleted(Guid GameId, string? Reason) : IGameEvent;

[EventType("game-restored")]
public sealed record GameRestored(Guid GameId) : IGameEvent;

/// <summary>A category of the wheel got this weight (added, or its weight changed).</summary>
[EventType("category-set")]
public sealed record CategorySet(string Name, int Weight) : IGameEvent;

/// <summary>A category left the wheel; the games keep the tag.</summary>
[EventType("category-removed")]
public sealed record CategoryRemoved(string Name) : IGameEvent;

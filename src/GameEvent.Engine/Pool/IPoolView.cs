using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Pool;

/// <summary>
/// Read-only view of the global game pool, passed to the engine when a command needs it (rolls).
/// Replaying the log never reads the pool: events already hold the results.
/// </summary>
public interface IPoolView
{
    IReadOnlyList<Game> Games { get; }

    IReadOnlyList<Category> Categories { get; }
}

/// <summary>
/// A game in the pool. <see cref="Hours"/> is null when neither HowLongToBeat nor the admin set it;
/// <see cref="ReleaseYear"/> when it is not known (a zone's year filter then does not pass it, D-307).
/// </summary>
public sealed record Game(Guid Id, string Title, EquatableArray<string> Tags, decimal? Hours, bool IsDeleted = false, int? ReleaseYear = null);

/// <summary>A category on the category wheel: a tag with a weight.</summary>
public sealed record Category(string Name, int Weight);

public sealed record PoolSnapshot(IReadOnlyList<Game> Games, IReadOnlyList<Category> Categories) : IPoolView;

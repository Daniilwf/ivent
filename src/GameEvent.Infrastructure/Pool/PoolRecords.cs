using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Pool;

/// <summary>A game of the global pool (D-19). Soft-deleted games stay for history.</summary>
public sealed class GameRecord
{
    public Guid Id { get; set; }

    public required string Title { get; set; }

    /// <summary>Tags as a JSON array. The roll filters in memory, never in SQL (invariant 9).</summary>
    public required string TagsJson { get; set; }

    public decimal? Hours { get; set; }

    public bool IsDeleted { get; set; }
}

/// <summary>A category on the category wheel: a tag with a weight.</summary>
public sealed class CategoryRecord
{
    public required string Name { get; set; }

    public int Weight { get; set; }
}

public static class PoolReader
{
    /// <summary>Loads the pool for a roll. ~600 games: small enough to read whole.</summary>
    public static async Task<PoolSnapshot> LoadAsync(Database.GameEventDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var games = await db.Games.AsNoTracking().ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().ToListAsync(ct);
        return new PoolSnapshot(
            [.. games.Select(g => new Game(g.Id, g.Title, Tags(g.TagsJson), g.Hours, g.IsDeleted))],
            [.. categories.Select(c => new Category(c.Name, c.Weight))]);
    }

    public static string TagsToJson(IEnumerable<string> tags) => JsonSerializer.Serialize(tags.ToArray(), EngineJson.Options);

    private static EquatableArray<string> Tags(string json) =>
        [.. JsonSerializer.Deserialize<string[]>(json, EngineJson.Options) ?? []];
}

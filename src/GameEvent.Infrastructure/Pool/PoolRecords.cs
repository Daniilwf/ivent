using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Pool;

/// <summary>
/// A game of the global pool (D-19, D-119; SPEC «Модель данных»: Game). Soft-deleted games stay for history. Changed only
/// by the pool commands of the queue; the seed and the import fill it directly.
/// </summary>
public sealed class GameRecord
{
    public Guid Id { get; set; }

    public required string Title { get; set; }

    /// <summary>Tags as a JSON array. The roll filters in memory, never in SQL (invariant 9).</summary>
    public required string TagsJson { get; set; }

    /// <summary>The main story in hours (HowLongToBeat or by hand); a run keeps its own snapshot at the roll.</summary>
    public decimal? Hours { get; set; }

    public int? Year { get; set; }

    public string? SteamAppId { get; set; }

    /// <summary>The cover: a stored file (D-108, D-118), or none — the site draws a placeholder with the title.</summary>
    public Guid? CoverFileId { get; set; }

    /// <summary>A challenge or the condition of finishing an endless game (SPEC «Пул общий»).</summary>
    public string? Note { get; set; }

    /// <summary>What counts as finishing an endless or multiplayer game (SPEC «условие прохождения»).</summary>
    public string? CompletionCondition { get; set; }

    public bool IsCoop { get; set; }

    /// <summary>The account that added the game; none for the seed and the import.</summary>
    public Guid? AuthorId { get; set; }

    /// <summary>Who added the game as the imported table names them, when no account did (F1, D-125).</summary>
    public string? AuthorName { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

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
            [.. games.Select(g => new Game(g.Id, g.Title, Tags(g.TagsJson), g.Hours, g.IsDeleted, g.Year))],
            [.. categories.Select(c => new Category(c.Name, c.Weight))]);
    }

    public static string TagsToJson(IEnumerable<string> tags) => JsonSerializer.Serialize(tags.ToArray(), EngineJson.Options);

    public static EquatableArray<string> Tags(string json) =>
        [.. JsonSerializer.Deserialize<string[]>(json, EngineJson.Options) ?? []];
}

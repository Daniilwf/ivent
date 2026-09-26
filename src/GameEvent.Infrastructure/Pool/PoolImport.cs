using GameEvent.Engine.Pool;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Pool;

/// <summary>A game row of the table: its row number, the title, the tags, who added it and the note.</summary>
public sealed record ImportedGame(int Row, string Title, IReadOnlyList<string> Tags, string? Author, string? Note);

/// <summary>A category row of the table: the name and the weight on the wheel.</summary>
public sealed record ImportedCategory(int Row, string Name, int Weight);

/// <summary>What the table holds, and the rows that could not be read, with why.</summary>
public sealed record ImportedTable(IReadOnlyList<ImportedGame> Games, IReadOnlyList<ImportedCategory> Categories, IReadOnlyList<string> Problems);

/// <summary>
/// What an import will do (F1, D-125): the games to add (<c>Force</c> — the pool or the table has an alike title), the
/// categories new to the wheel, and what it leaves out — a title twice in the table, a title the pool has already, a title
/// the admin deleted from the pool, a category the wheel has already (its weight is the admin's: kept, and told when the
/// table says otherwise), a row that is not a game card.
/// </summary>
public sealed record PoolImportPlan(
    IReadOnlyList<(ImportedGame Game, GameCard Card, bool Force)> ToAdd,
    IReadOnlyList<ImportedCategory> Categories,
    IReadOnlyList<(ImportedGame Game, ImportedGame First)> SameInTable,
    IReadOnlyList<ImportedGame> InPool,
    IReadOnlyList<(ImportedGame Game, string Alike)> Alike,
    IReadOnlyList<string> Problems,
    IReadOnlyList<ImportedGame> Deleted,
    IReadOnlyList<(ImportedCategory Category, int OnWheel)> WeightsKept);

/// <summary>What the import did: games and categories written, and refusals by the queue.</summary>
public sealed record PoolImportResult(int GamesAdded, int CategoriesSet, IReadOnlyList<string> Refused);

/// <summary>
/// The one-time import of the pool from the table «Игры крутить» (SPEC «Импорт», F1, D-125): sheet «Игры» — A the title,
/// B the tags (the computed values of their formulas, separated by commas), C who added it, D the note; sheet «Категории»
/// — the name and the weight. Titles and tags are tidied like any card; a repeated import adds nothing twice. Every write
/// is a command of the queue, like the site's own.
/// </summary>
public static class PoolImport
{
    public const string GamesSheet = "Игры";
    public const string CategoriesSheet = "Категории";

    /// <summary>Plans the import against the pool as it is: nothing is written.</summary>
    public static async Task<PoolImportPlan> PlanAsync(GameEventDbContext db, ImportedTable table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(table);
        var games = await db.Games.AsNoTracking().Select(g => new { g.Title, g.IsDeleted }).ToListAsync(ct);
        var pool = games.Where(g => !g.IsDeleted).Select(g => g.Title).ToList();
        var deletedTitles = games.Where(g => g.IsDeleted).Select(g => g.Title).ToList();
        var problems = new List<string>(table.Problems);
        var deleted = new List<ImportedGame>();
        var toAdd = new List<(ImportedGame, GameCard, bool)>();
        var same = new List<(ImportedGame, ImportedGame)>();
        var inPool = new List<ImportedGame>();
        var alike = new List<(ImportedGame, string)>();
        var taken = new List<(string Title, ImportedGame Game)>();

        foreach (var game in table.Games)
        {
            var (card, problem) = PoolRules.Normalize(new GameCard(game.Title, [.. game.Tags], null, null, null, null, game.Note, false));
            if (card is null)
            {
                problems.Add($"«{GamesSheet}», row {game.Row}: «{game.Title.Trim()}» — {problem}");
                continue;
            }

            if (game.Author is { } author && PoolRules.Tidy(author).Length > PoolRules.MaxAuthorNameLength)
            {
                problems.Add($"«{GamesSheet}», row {game.Row}: the author of «{card.Title}» is longer than {PoolRules.MaxAuthorNameLength} characters.");
                continue;
            }

            if (pool.Any(t => PoolRules.IsSame(t, card.Title)))
            {
                inPool.Add(game);
                continue;
            }

            // The admin took it out of the pool: an import does not bring it back as a new game
            if (deletedTitles.Any(t => PoolRules.IsSame(t, card.Title)))
            {
                deleted.Add(game);
                continue;
            }

            if (taken.FirstOrDefault(t => PoolRules.IsSame(t.Title, card.Title)) is { Game: { } first })
            {
                same.Add((game, first));
                continue;
            }

            var similar = pool.Concat(taken.Select(t => t.Title)).FirstOrDefault(t => PoolRules.IsAlike(t, card.Title));
            if (similar is not null)
            {
                alike.Add((game, similar));
            }

            toAdd.Add((game, card, similar is not null));
            taken.Add((card.Title, game));
        }

        var wheel = await db.Categories.AsNoTracking().ToListAsync(ct);
        var categories = new List<ImportedCategory>();
        var kept = new List<(ImportedCategory, int)>();
        foreach (var group in table.Categories.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var category = group.First();
            problems.AddRange(group.Skip(1).Select(c => $"«{CategoriesSheet}», row {c.Row}: «{c.Name}» is in the table again (row {category.Row} counts)."));
            if (PoolRules.CheckCategory(category.Name, category.Weight) is { } invalid)
            {
                problems.Add($"«{CategoriesSheet}», row {category.Row}: «{category.Name}» — {invalid.Detail}");
            }
            else if (wheel.FirstOrDefault(c => string.Equals(c.Name, category.Name, StringComparison.OrdinalIgnoreCase)) is { } onWheel)
            {
                if (onWheel.Weight != category.Weight)
                {
                    kept.Add((category, onWheel.Weight));
                }
            }
            else
            {
                categories.Add(category);
            }
        }

        return new PoolImportPlan(toAdd, categories, same, inPool, alike, problems, deleted, kept);
    }

    /// <summary>Writes the plan through the queue: the new games, then the categories new to the wheel.</summary>
    public static async Task<PoolImportResult> ApplyAsync(PoolImportPlan plan, CommandBus bus, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(bus);
        var refused = new List<string>();
        var added = 0;
        foreach (var (game, card, force) in plan.ToAdd)
        {
            var outcome = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), Guid.Empty, new AddGame(card, null, force, game.Author), null), ct);
            if (outcome.IsAccepted)
            {
                added++;
            }
            else
            {
                refused.Add($"«{GamesSheet}», row {game.Row}: «{card.Title}» — {outcome.Rejection!.Code}: {outcome.Rejection.Detail}");
            }
        }

        var set = 0;
        foreach (var category in plan.Categories)
        {
            var outcome = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), Guid.Empty, new SetCategory(category.Name, category.Weight), null), ct);
            if (outcome.IsAccepted)
            {
                set++;
            }
            else
            {
                refused.Add($"«{CategoriesSheet}», row {category.Row}: «{category.Name}» — {outcome.Rejection!.Code}: {outcome.Rejection.Detail}");
            }
        }

        return new PoolImportResult(added, set, refused);
    }

    /// <summary>The plan as a report for a person: what will be added and every reason something is left out.</summary>
    public static string Report(PoolImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var lines = new List<string>
        {
            $"Games to add: {plan.ToAdd.Count}; categories new to the wheel: {plan.Categories.Count}.",
            $"Already in the pool (skipped): {plan.InPool.Count}.",
        };
        Section("Deleted from the pool by the admin (skipped)", plan.Deleted.Select(d => $"row {d.Row} «{d.Title.Trim()}»"));
        Section("On the wheel with another weight (the wheel's is kept)", plan.WeightsKept.Select(k => $"row {k.Category.Row} «{k.Category.Name}»: {k.Category.Weight} in the table, {k.OnWheel} on the wheel"));
        Section("Twice in the table (the later rows are skipped)", plan.SameInTable.Select(s => $"row {s.Game.Row} «{s.Game.Title.Trim()}» = row {s.First.Row}"));
        Section("Alike titles (added anyway, check by hand)", plan.Alike.Select(a => $"row {a.Game.Row} «{a.Game.Title.Trim()}» ~ «{a.Alike}»"));
        Section("Rows left out", plan.Problems);
        return string.Join(Environment.NewLine, lines);

        void Section(string title, IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count > 0)
            {
                lines.Add($"{title}: {list.Count}");
                lines.AddRange(list.Select(i => "  " + i));
            }
        }
    }

}

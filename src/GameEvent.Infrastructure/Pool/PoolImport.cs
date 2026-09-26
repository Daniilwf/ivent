using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
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
/// categories to put on the wheel, and what it leaves out — a title twice in the table, a title the pool has already,
/// a row that is not a game card.
/// </summary>
public sealed record PoolImportPlan(
    IReadOnlyList<(ImportedGame Game, GameCard Card, bool Force)> ToAdd,
    IReadOnlyList<ImportedCategory> Categories,
    IReadOnlyList<(ImportedGame Game, ImportedGame First)> SameInTable,
    IReadOnlyList<ImportedGame> InPool,
    IReadOnlyList<(ImportedGame Game, string Alike)> Alike,
    IReadOnlyList<string> Problems);

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

    private static readonly char[] s_tagSeparators = [',', ';'];

    public static ImportedTable Read(Stream xlsx)
    {
        ArgumentNullException.ThrowIfNull(xlsx);
        using var document = SpreadsheetDocument.Open(xlsx, false);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("The file has no workbook.");
        var strings = workbook.SharedStringTablePart?.SharedStringTable.Elements<SharedStringItem>().Select(s => s.InnerText).ToList() ?? [];
        var problems = new List<string>();

        var games = new List<ImportedGame>();
        foreach (var (row, cells) in Rows(workbook, GamesSheet, strings, problems).Skip(1))
        {
            var title = cells.GetValueOrDefault("A");
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var tags = (cells.GetValueOrDefault("B") ?? "").Split(s_tagSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            games.Add(new ImportedGame(row, title, tags, Blank(cells.GetValueOrDefault("C")), Blank(cells.GetValueOrDefault("D"))));
        }

        var categories = new List<ImportedCategory>();
        foreach (var (row, cells) in Rows(workbook, CategoriesSheet, strings, problems).Skip(1))
        {
            var name = cells.GetValueOrDefault("A");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!decimal.TryParse(cells.GetValueOrDefault("B"), NumberStyles.Number, CultureInfo.InvariantCulture, out var weight) || weight != decimal.Truncate(weight) || weight < 1)
            {
                problems.Add($"«{CategoriesSheet}», row {row}: the weight of «{name.Trim()}» is not a whole number above 0.");
                continue;
            }

            categories.Add(new ImportedCategory(row, name.Trim(), (int)weight));
        }

        return new ImportedTable(games, categories, problems);
    }

    /// <summary>Plans the import against the pool as it is: nothing is written.</summary>
    public static async Task<PoolImportPlan> PlanAsync(GameEventDbContext db, ImportedTable table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(table);
        var pool = await db.Games.AsNoTracking().Where(g => !g.IsDeleted).Select(g => g.Title).ToListAsync(ct);
        var problems = new List<string>(table.Problems);
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

            if (game.Author?.Length > PoolRules.MaxAuthorNameLength)
            {
                problems.Add($"«{GamesSheet}», row {game.Row}: the author of «{card.Title}» is longer than {PoolRules.MaxAuthorNameLength} characters.");
                continue;
            }

            if (pool.Any(t => PoolRules.IsSame(t, card.Title)))
            {
                inPool.Add(game);
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

        var categories = table.Categories
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        return new PoolImportPlan(toAdd, categories, same, inPool, alike, problems);
    }

    /// <summary>Writes the plan through the queue; a category already on the wheel with the same weight is left as it is.</summary>
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
            else if (outcome.Rejection!.Code != PoolRules.NothingToChange)
            {
                refused.Add($"«{CategoriesSheet}», row {category.Row}: «{category.Name}» — {outcome.Rejection.Code}: {outcome.Rejection.Detail}");
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
            $"Games to add: {plan.ToAdd.Count}; categories: {plan.Categories.Count}.",
            $"Already in the pool (skipped): {plan.InPool.Count}.",
        };
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

    /// <summary>A sheet's rows by number, each as its cells by column letter, with the values the cells show.</summary>
    private static IEnumerable<(int Row, Dictionary<string, string> Cells)> Rows(WorkbookPart workbook, string name, List<string> strings, List<string> problems)
    {
        var sheet = workbook.Workbook.Sheets?.Elements<Sheet>().FirstOrDefault(s => string.Equals(s.Name?.Value?.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (sheet?.Id?.Value is not { } id || workbook.GetPartById(id) is not WorksheetPart part)
        {
            problems.Add($"The sheet «{name}» is not in the file.");
            yield break;
        }

        foreach (var row in part.Worksheet.Descendants<Row>())
        {
            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var cell in row.Elements<Cell>())
            {
                var column = new string([.. (cell.CellReference?.Value ?? "").TakeWhile(char.IsLetter)]);
                cells[column] = Value(cell, strings);
            }

            yield return ((int)(row.RowIndex?.Value ?? 0), cells);
        }
    }

    // A formula's cell keeps its last computed value: that is what the table shows, and what is imported
    private static string Value(Cell cell, List<string> strings)
    {
        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? "";
        }

        var raw = cell.CellValue?.Text ?? "";
        return cell.DataType?.Value == CellValues.SharedString && int.TryParse(raw, CultureInfo.InvariantCulture, out var index) && index < strings.Count
            ? strings[index]
            : raw;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

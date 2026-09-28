using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GameEvent.Infrastructure.Pool;

namespace GameEvent.Tools.Import;

/// <summary>
/// Reads the table «Игры крутить» for the pool import (F1, D-125): sheet «Игры» — A the title, B the tags (what the cell
/// shows: the computed value of its formula, separated by commas or semicolons), C who added it, D the note; sheet
/// «Категории» — the name and the weight. Row 1 of each sheet is the header. Whatever cannot be read is told, not guessed:
/// a formula's error, a row with data but no title, a weight that is not a whole number within the wheel's limits.
/// </summary>
public static class XlsxPoolReader
{
    private static readonly char[] s_tagSeparators = [',', ';'];

    public static ImportedTable Read(Stream xlsx)
    {
        ArgumentNullException.ThrowIfNull(xlsx);
        using var document = SpreadsheetDocument.Open(xlsx, false);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("The file has no workbook.");

        // Only the text runs of a shared string: phonetic hints (rPh) are not part of what the cell shows
        var strings = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(Text).ToList() ?? [];
        var problems = new List<string>();

        var games = new List<ImportedGame>();
        foreach (var (row, cells) in Rows(workbook, PoolImport.GamesSheet, strings, problems).Where(r => r.Row > 1))
        {
            var title = cells.GetValueOrDefault("A");
            if (string.IsNullOrWhiteSpace(title))
            {
                if (cells.Values.Any(v => !string.IsNullOrWhiteSpace(v)))
                {
                    problems.Add($"«{PoolImport.GamesSheet}», row {row}: no title, the rest of the row is left out.");
                }

                continue;
            }

            var tags = (cells.GetValueOrDefault("B") ?? "").Split(s_tagSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            games.Add(new ImportedGame(row, title, tags, Blank(cells.GetValueOrDefault("C")), Blank(cells.GetValueOrDefault("D"))));
        }

        var categories = new List<ImportedCategory>();
        foreach (var (row, cells) in Rows(workbook, PoolImport.CategoriesSheet, strings, problems).Where(r => r.Row > 1))
        {
            var name = cells.GetValueOrDefault("A");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!decimal.TryParse(cells.GetValueOrDefault("B"), NumberStyles.Number, CultureInfo.InvariantCulture, out var weight)
                || weight != decimal.Truncate(weight) || weight is < 1 or > PoolRules.MaxCategoryWeight)
            {
                problems.Add($"«{PoolImport.CategoriesSheet}», row {row}: the weight of «{name.Trim()}» is not a whole number from 1 to {PoolRules.MaxCategoryWeight}.");
                continue;
            }

            categories.Add(new ImportedCategory(row, name.Trim(), (int)weight));
        }

        return new ImportedTable(games, categories, problems);
    }

    /// <summary>A sheet's rows by number, each as its cells by column letter, with the values the cells show.</summary>
    private static IEnumerable<(int Row, Dictionary<string, string> Cells)> Rows(WorkbookPart workbook, string name, List<string> strings, List<string> problems)
    {
        var sheet = workbook.Workbook?.Sheets?.Elements<Sheet>().FirstOrDefault(s => string.Equals(s.Name?.Value?.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (sheet?.Id?.Value is not { } id || workbook.GetPartById(id) is not WorksheetPart part)
        {
            problems.Add($"The sheet «{name}» is not in the file.");
            yield break;
        }

        var number = 0;
        foreach (var row in part.Worksheet?.Descendants<Row>() ?? [])
        {
            // A row or a cell without its reference (some writers skip it) takes its place in order
            number = (int?)row.RowIndex?.Value ?? number + 1;
            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            var column = 0;
            foreach (var cell in row.Elements<Cell>())
            {
                var letters = new string([.. (cell.CellReference?.Value ?? "").TakeWhile(char.IsLetter)]);
                column = letters.Length > 0 ? ColumnNumber(letters) : column + 1;
                var (value, problem) = Value(cell, strings);
                cells[ColumnLetters(column)] = value;
                if (problem is not null)
                {
                    problems.Add($"«{name}», row {number}, column {ColumnLetters(column)}: {problem}");
                }
            }

            yield return (number, cells);
        }
    }

    // A formula's cell keeps its last computed value: that is what the table shows, and what is imported
    private static (string Value, string? Problem) Value(Cell cell, List<string> strings)
    {
        var raw = cell.CellValue?.Text ?? "";
        switch (cell.DataType?.Value)
        {
            case var type when type == CellValues.InlineString:
                return (cell.InlineString is { } inline ? Text(inline) : "", null);

            case var type when type == CellValues.SharedString:
                return int.TryParse(raw, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < strings.Count
                    ? (strings[index], null)
                    : ("", $"a shared string {raw} that the file does not have.");

            case var type when type == CellValues.Error:
                return ("", $"the formula gives {raw}, the cell is read as empty.");

            case var type when type == CellValues.Boolean:
                return (raw == "1" ? "TRUE" : "FALSE", null);

            default:
                return (raw, null);
        }
    }

    private static string Text(OpenXmlElement item) =>
        string.Concat(item.ChildElements.Select(e => e switch
        {
            Text text => text.Text,
            Run run => run.Text?.Text ?? "",
            _ => "",
        }));

    private static int ColumnNumber(string letters) =>
        letters.Aggregate(0, (sum, letter) => (sum * 26) + char.ToUpperInvariant(letter) - 'A' + 1);

    private static string ColumnLetters(int number)
    {
        var letters = "";
        for (; number > 0; number = (number - 1) / 26)
        {
            letters = (char)('A' + ((number - 1) % 26)) + letters;
        }

        return letters;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

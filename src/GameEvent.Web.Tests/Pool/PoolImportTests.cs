using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GameEvent.Infrastructure.Pool;
using GameEvent.Web.Tests.Queue;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Pool;

/// <summary>
/// The pool import from xlsx (F1, D-125) on a small synthetic table: the tags are the computed values of formulas, titles
/// and tags are tidied, a title twice in the table or already in the pool is left out, alike titles are added with a
/// warning, authors are kept as text, the report writes nothing, and importing again adds nothing twice.
/// </summary>
public sealed class PoolImportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The harness's pool has Silent Hill, Alan Wake, Tetris and Portal
    private static readonly string[][] s_games =
    [
        ["Игры", "Категории", "Кто добавил", "Дополнительно"],
        ["  Dice Fold ", "=Puzzle, Roguelike", "Автор 1", "  "],
        ["Dice & Fold", "=Puzzle", "Автор 2", "Пройти на харде"],
        ["dice fold", "=Puzzle", "Автор 3", ""],
        ["Portal", "=Puzzle", "Автор 4", ""],
        ["", "", "", ""],
        [new string('x', 201), "=Puzzle", "", ""],
        ["Hades", "=Roguelike; action ; Roguelike", "", "Первый побег"],
    ];

    private static readonly string[][] s_categories =
    [
        ["Категории", "Вес"],
        ["Puzzle", "3"],
        [" Roguelike ", "2"],
        ["Broken", "полтора"],
        ["Zero", "0"],
    ];

    [Fact]
    public void The_table_is_read_with_formula_values_and_blank_rows_skipped()
    {
        using var file = Xlsx(s_games, s_categories);

        var table = PoolImport.Read(file);

        Assert.Equal(6, table.Games.Count);
        var first = table.Games[0];
        Assert.Equal((2, "  Dice Fold ", "Автор 1", (string?)null), (first.Row, first.Title, first.Author, first.Note));
        Assert.Equal(["Puzzle", "Roguelike"], first.Tags);
        Assert.Equal(["Roguelike", "action", "Roguelike"], table.Games[^1].Tags);
        Assert.Equal([("Puzzle", 3), ("Roguelike", 2)], table.Categories.Select(c => (c.Name, c.Weight)));
        Assert.Equal(2, table.Problems.Count);
        Assert.All(table.Problems, p => Assert.Contains("Категории", p, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_plan_leaves_out_repeats_and_the_pools_titles_and_warns_of_alike_ones()
    {
        await using var h = await QueueHarness.StartAsync();
        using var file = Xlsx(s_games, s_categories);
        var table = PoolImport.Read(file);

        await using var db = h.NewDb();
        var plan = await PoolImport.PlanAsync(db, table, Ct);

        Assert.Equal(["Dice Fold", "Dice & Fold", "Hades"], plan.ToAdd.Select(a => a.Card.Title));
        Assert.Equal([false, true, false], plan.ToAdd.Select(a => a.Force));
        Assert.Equal(["Roguelike", "action"], plan.ToAdd[^1].Card.Tags);
        Assert.Equal((4, 2), (plan.SameInTable.Single().Game.Row, plan.SameInTable.Single().First.Row));
        Assert.Equal("Portal", plan.InPool.Single().Title);
        Assert.Equal(("Dice & Fold", "Dice Fold"), (plan.Alike.Single().Game.Title, plan.Alike.Single().Alike));
        Assert.Contains(plan.Problems, p => p.Contains("row 7", StringComparison.Ordinal));
        var report = PoolImport.Report(plan);
        Assert.Contains("Games to add: 3", report, StringComparison.Ordinal);
        Assert.Contains("row 4 «dice fold» = row 2", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_report_writes_nothing()
    {
        await using var h = await QueueHarness.StartAsync();
        using var file = Xlsx(s_games, s_categories);
        int before;
        await using (var db = h.NewDb())
        {
            before = await db.Games.CountAsync(Ct);
            _ = PoolImport.Report(await PoolImport.PlanAsync(db, PoolImport.Read(file), Ct));
        }

        await using var after = h.NewDb();
        Assert.Equal(before, await after.Games.CountAsync(Ct));
        Assert.Empty(await after.Events.ToListAsync(Ct));
    }

    [Fact]
    public async Task The_import_writes_through_the_queue_with_authors_as_text_and_again_adds_nothing()
    {
        await using var h = await QueueHarness.StartAsync();

        var first = await ImportAsync(h);
        var second = await ImportAsync(h);

        Assert.Equal((3, 2, 0), (first.GamesAdded, first.CategoriesSet, first.Refused.Count));
        Assert.Equal((0, 0, 0), (second.GamesAdded, second.CategoriesSet, second.Refused.Count));
        await using var db = h.NewDb();
        var fold = await db.Games.SingleAsync(g => g.Title == "Dice & Fold", Ct);
        Assert.Equal(("Автор 2", (Guid?)null, "Пройти на харде"), (fold.AuthorName, fold.AuthorId, fold.Note));
        Assert.Equal(3, (await db.Categories.SingleAsync(c => c.Name == "Puzzle", Ct)).Weight);
        Assert.Equal(5, await db.Events.CountAsync(e => e.Type == "game-added" || e.Type == "category-set", Ct));
    }

    [Fact]
    public void A_file_without_the_sheets_says_so()
    {
        using var file = Xlsx(null, null);

        var table = PoolImport.Read(file);

        Assert.Empty(table.Games);
        Assert.Equal(2, table.Problems.Count(p => p.StartsWith("The sheet", StringComparison.Ordinal)));
    }

    // ---- Helpers ----

    private static async Task<PoolImportResult> ImportAsync(QueueHarness h)
    {
        using var file = Xlsx(s_games, s_categories);
        PoolImportPlan plan;
        await using (var db = h.NewDb())
        {
            plan = await PoolImport.PlanAsync(db, PoolImport.Read(file), Ct);
        }

        return await PoolImport.ApplyAsync(plan, h.Bus, Ct);
    }

    /// <summary>A workbook with the two sheets; a value starting with «=» is a formula cell holding that text as its computed value.</summary>
    private static MemoryStream Xlsx(string[][]? games, string[][]? categories)
    {
        var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook(new Sheets());
            var sheets = workbook.Workbook.Sheets!;
            uint id = 1;
            foreach (var (name, rows) in new[] { ("Правила", new[] { new[] { "Правила игры" } }), (PoolImport.GamesSheet, games), (PoolImport.CategoriesSheet, categories) })
            {
                if (rows is null)
                {
                    continue;
                }

                var part = workbook.AddNewPart<WorksheetPart>();
                var data = new SheetData();
                for (var r = 0; r < rows.Length; r++)
                {
                    var row = new Row { RowIndex = (uint)(r + 1) };
                    for (var c = 0; c < rows[r].Length; c++)
                    {
                        var value = rows[r][c];
                        var reference = $"{(char)('A' + c)}{r + 1}";
                        row.Append(value.StartsWith('=')
                            ? new Cell(new CellFormula("XLOOKUP(A1, 'Игры Архив'!A:A, 'Игры Архив'!B:B)"), new CellValue(value[1..])) { CellReference = reference, DataType = CellValues.String }
                            : new Cell(new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve })) { CellReference = reference, DataType = CellValues.InlineString });
                    }

                    data.Append(row);
                }

                part.Worksheet = new Worksheet(data);
                sheets.Append(new Sheet { Id = workbook.GetIdOfPart(part), SheetId = id++, Name = name });
            }
        }

        stream.Position = 0;
        return stream;
    }
}

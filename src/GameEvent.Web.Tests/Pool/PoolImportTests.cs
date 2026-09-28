using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GameEvent.Engine.Pool;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Queue;
using GameEvent.Tools.Import;
using GameEvent.Web.Tests.Api;
using GameEvent.Web.Tests.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameEvent.Web.Tests.Pool;

/// <summary>
/// The pool import from xlsx (F1, D-125) on small synthetic tables written like Excel writes them — shared strings with
/// rich text, numbers, formulas with their computed values and their errors: titles and tags tidied, a title twice in the
/// table, already in the pool or deleted from it left out, alike titles added with a warning, the wheel's weights kept,
/// authors kept as text, the report writing nothing, and importing again adding nothing twice.
/// </summary>
public sealed class PoolImportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The harness's pool has Silent Hill, Alan Wake, Tetris and Portal; its wheel — Horror 2 and Puzzle 1
    private static readonly object[][] s_games =
    [
        ["Игры", "Категории", "Кто добавил", "Дополнительно"],
        ["  Dice Fold ", F("Puzzle, Roguelike"), new Rich("Автор", " 1"), "  "],
        ["Dice & Fold", F("Puzzle"), "Автор 2", "Пройти на харде"],
        ["dice fold", F("Puzzle"), "Автор 3", ""],
        ["Portal", F("Puzzle"), "Автор 4", ""],
        ["", "", "", ""],
        [new string('x', 201), F("Puzzle"), "", ""],
        ["Hades", F("Roguelike; action ; Roguelike"), "", "Первый побег"],
        ["Outer Wilds", new Error("#N/A"), "", ""],
        ["", F("Horror"), "Автор 5", ""],
    ];

    private static readonly object[][] s_categories =
    [
        ["Категории", "Вес"],
        ["Puzzle", 3m],
        [" Roguelike ", 2m],
        ["Broken", "полтора"],
        ["Zero", 0m],
        ["Huge", 1001m],
        ["roguelike", 5m],
    ];

    [Fact]
    public void The_table_is_read_as_excel_writes_it()
    {
        using var file = Xlsx(s_games, s_categories);

        var table = XlsxPoolReader.Read(file);

        Assert.Equal(7, table.Games.Count);
        var first = table.Games[0];
        Assert.Equal((2, "  Dice Fold ", "Автор 1", (string?)null), (first.Row, first.Title, first.Author, first.Note));
        Assert.Equal(["Puzzle", "Roguelike"], first.Tags);
        Assert.Equal(["Roguelike", "action", "Roguelike"], table.Games[5].Tags);
        Assert.Equal(("Outer Wilds", 0), (table.Games[^1].Title, table.Games[^1].Tags.Count));
        Assert.Equal([("Puzzle", 3), ("Roguelike", 2), ("roguelike", 5)], table.Categories.Select(c => (c.Name, c.Weight)));
        Assert.Contains(table.Problems, p => p.Contains("row 9, column B: the formula gives #N/A", StringComparison.Ordinal));
        Assert.Contains(table.Problems, p => p.Contains("row 10: no title", StringComparison.Ordinal));
        Assert.Equal(3, table.Problems.Count(p => p.Contains("the weight of", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_plan_leaves_out_repeats_the_pools_titles_and_keeps_the_wheels_weights()
    {
        await using var h = await QueueHarness.StartAsync();
        using var file = Xlsx(s_games, s_categories);
        var table = XlsxPoolReader.Read(file);

        await using var db = h.NewDb();
        var plan = await PoolImport.PlanAsync(db, table, Ct);

        Assert.Equal(["Dice Fold", "Dice & Fold", "Hades", "Outer Wilds"], plan.ToAdd.Select(a => a.Card.Title));
        Assert.Equal([false, true, false, false], plan.ToAdd.Select(a => a.Force));
        Assert.Equal(["Roguelike", "action"], plan.ToAdd[2].Card.Tags);
        Assert.Equal((4, 2), (plan.SameInTable.Single().Game.Row, plan.SameInTable.Single().First.Row));
        Assert.Equal("Portal", plan.InPool.Single().Title);
        Assert.Equal(("Dice & Fold", "Dice Fold"), (plan.Alike.Single().Game.Title, plan.Alike.Single().Alike));
        Assert.Contains(plan.Problems, p => p.Contains("row 7", StringComparison.Ordinal));
        Assert.Equal(["Roguelike"], plan.Categories.Select(c => c.Name));
        Assert.Equal(("Puzzle", 3, 1), (plan.WeightsKept.Single().Category.Name, plan.WeightsKept.Single().Category.Weight, plan.WeightsKept.Single().OnWheel));
        Assert.Contains(plan.Problems, p => p.Contains("«roguelike» is in the table again", StringComparison.Ordinal));
        var report = PoolImport.Report(plan);
        Assert.Contains("Games to add: 4", report, StringComparison.Ordinal);
        Assert.Contains("row 4 «dice fold» = row 2", report, StringComparison.Ordinal);
        Assert.Contains("«Puzzle»: 3 in the table, 1 on the wheel", report, StringComparison.Ordinal);
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
            _ = PoolImport.Report(await PoolImport.PlanAsync(db, XlsxPoolReader.Read(file), Ct));
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

        Assert.Equal((4, 1, 0), (first.GamesAdded, first.CategoriesSet, first.Refused.Count));
        Assert.Equal((0, 0, 0), (second.GamesAdded, second.CategoriesSet, second.Refused.Count));
        await using var db = h.NewDb();
        var fold = await db.Games.SingleAsync(g => g.Title == "Dice & Fold", Ct);
        Assert.Equal(("Автор 2", (Guid?)null, "Пройти на харде"), (fold.AuthorName, fold.AuthorId, fold.Note));
        Assert.Equal(1, (await db.Categories.SingleAsync(c => c.Name == "Puzzle", Ct)).Weight);
        Assert.Equal(5, await db.Events.CountAsync(e => e.Type == "game-added" || e.Type == "category-set", Ct));
    }

    [Fact]
    public async Task A_game_the_admin_deleted_does_not_come_back_with_the_next_import()
    {
        await using var h = await QueueHarness.StartAsync();
        await ImportAsync(h);
        Guid hades;
        await using (var db = h.NewDb())
        {
            hades = (await db.Games.SingleAsync(g => g.Title == "Hades", Ct)).Id;
        }

        await h.SendAsync(new DeleteGame(hades), Guid.Empty);
        PoolImportPlan plan;
        await using (var db = h.NewDb())
        {
            using var file = Xlsx(s_games, s_categories);
            plan = await PoolImport.PlanAsync(db, XlsxPoolReader.Read(file), Ct);
        }

        Assert.Equal("Hades", plan.Deleted.Single().Title);
        Assert.Empty(plan.ToAdd);
        Assert.Contains("Deleted from the pool by the admin (skipped): 1", PoolImport.Report(plan), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_author_too_long_is_left_out_by_the_plan_and_refused_by_the_queue()
    {
        await using var h = await QueueHarness.StartAsync();
        using var file = Xlsx([["Игры"], ["Hades", F("Roguelike"), new string('a', 65), ""]], [["Категории", "Вес"]]);

        await using var db = h.NewDb();
        var plan = await PoolImport.PlanAsync(db, XlsxPoolReader.Read(file), Ct);
        var refused = await h.SendAsync(new AddGame(new GameCard("Hades", ["Roguelike"], null, null, null, null, null, false), null, false, new string('a', 65)), Guid.Empty);
        var tidy = await h.SendAsync(new AddGame(new GameCard("Celeste", ["Platformer"], null, null, null, null, null, false), null, false, "  Автор   6 "), Guid.Empty);

        Assert.Empty(plan.ToAdd);
        Assert.Contains(plan.Problems, p => p.Contains("the author of «Hades» is longer", StringComparison.Ordinal));
        Assert.Equal(PoolRules.CardInvalid, refused.Rejection!.Code);
        Assert.Equal("Автор 6", Assert.IsType<GameAdded>(Assert.Single(tidy.Events).Event).AuthorName);
    }

    [Fact]
    public async Task The_pool_card_shows_the_account_or_else_the_author_as_text()
    {
        await using var site = new SiteFactory();
        await site.SeedAsync();
        var bus = site.Services.GetRequiredService<CommandBus>();
        await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), Guid.Empty, new AddGame(new GameCard("Celeste", ["Platformer"], null, null, null, null, null, false), null, false, "Автор 7"), null), Ct);
        await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), Guid.Empty, new AddGame(new GameCard("Hades", ["Roguelike"], null, null, null, null, null, false), site.Users["vasya"], false, "Автор 8"), null), Ct);

        using var pool = JsonDocument.Parse(await (await site.SignedInAsync("zritel")).GetStringAsync("/api/pool", Ct));

        var authors = pool.RootElement.EnumerateArray().ToDictionary(g => g.GetProperty("title").GetString()!, g => g.GetProperty("author").GetString());
        Assert.Equal("Автор 7", authors["Celeste"]);
        Assert.Equal("vasya", authors["Hades"]);
    }

    [Fact]
    public void A_file_without_the_sheets_says_so()
    {
        using var file = Xlsx(null, null);

        var table = XlsxPoolReader.Read(file);

        Assert.Empty(table.Games);
        Assert.Equal(2, table.Problems.Count(p => p.StartsWith("The sheet", StringComparison.Ordinal)));
    }

    [Fact]
    public void Cells_without_references_take_their_places_in_order()
    {
        using var file = Xlsx([["Игры"], ["Hades", F("Roguelike"), "Автор 9", "Первый побег"]], [["Категории", "Вес"], ["Roguelike", 4m]], withReferences: false);

        var table = XlsxPoolReader.Read(file);

        Assert.Equal(("Hades", "Автор 9", "Первый побег", 2), (table.Games.Single().Title, table.Games.Single().Author, table.Games.Single().Note, table.Games.Single().Row));
        Assert.Equal(4, table.Categories.Single().Weight);
    }

    // ---- Helpers ----

    private sealed record Formula(string Value);

    private sealed record Rich(string First, string Second);

    private sealed record Error(string Value);

    private static Formula F(string value) => new(value);

    private static async Task<PoolImportResult> ImportAsync(QueueHarness h)
    {
        using var file = Xlsx(s_games, s_categories);
        PoolImportPlan plan;
        await using (var db = h.NewDb())
        {
            plan = await PoolImport.PlanAsync(db, XlsxPoolReader.Read(file), Ct);
        }

        return await PoolImport.ApplyAsync(plan, h.Bus, Ct);
    }

    /// <summary>
    /// A workbook as Excel writes one: text in shared strings (rich text as runs, with a phonetic hint that is not shown),
    /// numbers as numbers, formulas with their computed text or their error.
    /// </summary>
    private static MemoryStream Xlsx(object[][]? games, object[][]? categories, bool withReferences = true)
    {
        var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook(new Sheets());
            var shared = workbook.AddNewPart<SharedStringTablePart>();
            shared.SharedStringTable = new SharedStringTable();
            var sheets = workbook.Workbook.Sheets!;
            uint id = 1;
            foreach (var (name, rows) in new[] { ("Правила", new[] { new object[] { "Правила игры" } }), (PoolImport.GamesSheet, games), (PoolImport.CategoriesSheet, categories) })
            {
                if (rows is null)
                {
                    continue;
                }

                var part = workbook.AddNewPart<WorksheetPart>();
                var data = new SheetData();
                for (var r = 0; r < rows.Length; r++)
                {
                    var row = withReferences ? new Row { RowIndex = (uint)(r + 1) } : new Row();
                    for (var c = 0; c < rows[r].Length; c++)
                    {
                        var cell = Cell(rows[r][c], shared.SharedStringTable);
                        if (withReferences)
                        {
                            cell.CellReference = $"{(char)('A' + c)}{r + 1}";
                        }

                        row.Append(cell);
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

    private static Cell Cell(object value, SharedStringTable strings)
    {
        switch (value)
        {
            case decimal number:
                return new Cell(new CellValue(number));
            case Formula formula:
                return new Cell(new CellFormula("XLOOKUP(A1, 'Игры Архив'!A:A, 'Игры Архив'!B:B)"), new CellValue(formula.Value)) { DataType = CellValues.String };
            case Error error:
                return new Cell(new CellFormula("XLOOKUP(A1, 'Игры Архив'!A:A, 'Игры Архив'!B:B)"), new CellValue(error.Value)) { DataType = CellValues.Error };
            case Rich rich:
                strings.Append(new SharedStringItem(
                    new Run(new Text(rich.First)),
                    new Run(new RunProperties(new Bold()), new Text(rich.Second) { Space = SpaceProcessingModeValues.Preserve }),
                    new PhoneticRun(new Text("ふりがな")) { BaseTextStartIndex = 0, EndingBaseIndex = 1 }));
                return new Cell(new CellValue(strings.Count() - 1)) { DataType = CellValues.SharedString };
            default:
                strings.Append(new SharedStringItem(new Text((string)value) { Space = SpaceProcessingModeValues.Preserve }));
                return new Cell(new CellValue(strings.Count() - 1)) { DataType = CellValues.SharedString };
        }
    }
}

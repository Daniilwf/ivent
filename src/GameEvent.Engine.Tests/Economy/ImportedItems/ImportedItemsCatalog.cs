using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Content;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy.ImportedItems;

/// <summary>
/// The import of the old pack as data (content/imports/old-items): TRIAGE.csv names in its «Импорт» column the content file
/// each imported item became (D-471), items.json keeps the original. The checks here hold for every imported item at once.
/// </summary>
public class ImportedItemsCatalog
{
    private static string Package(string file) => Path.Combine(RepositoryPaths.Root(), "content", "imports", "old-items", file);

    /// <summary>TRIAGE rows: the name of the item, its status and the content id it was imported as (empty when not yet).</summary>
    internal static IReadOnlyList<(string Name, string Status, string Imported)> Triage() =>
        [.. File.ReadAllLines(Package("TRIAGE.csv")).Skip(1).Where(l => l.Length > 0).Select(l => l.Split(';')).Select(c => (c[0], c[1], c[^1]))];

    /// <summary>The ids of content/items that came from the old pack.</summary>
    internal static HashSet<string> ImportedIds() => [.. Triage().Where(r => r.Imported.Length > 0).Select(r => r.Imported)];

    // By name, as TRIAGE names them; «Бабл» is an item and its effect: the item is the row.
    private static Dictionary<string, JsonObject> Originals() =>
        JsonNode.Parse(File.ReadAllText(Package("items.json")))!.AsArray().Select(n => n!.AsObject())
            .GroupBy(o => (string)o["name"]!)
            .ToDictionary(g => g.Key, g => g.OrderBy(o => (string)o["kind"]! == "item" ? 0 : 1).First());

    [Fact]
    public void Every_imported_item_is_an_automatic_one_of_the_pack_with_its_numbers_kept()
    {
        var files = EconomyScenario.RepositoryPack().Objects.ToDictionary(o => o.Id);
        var originals = Originals();
        var rows = Triage().Where(r => r.Imported.Length > 0).ToList();

        Assert.NotEmpty(rows);
        Assert.Equal(rows.Count, rows.Select(r => r.Imported).Distinct().Count());
        foreach (var (name, status, id) in rows)
        {
            Assert.Equal("авто", status);
            var item = files[id];
            var original = originals[name];

            // Name, kind, rarity, price, window and flags as the pack has them (README «Что нужно учесть при импорте»)
            Assert.Equal(name, item.Name);
            Assert.Equal((string)original["kind"]!, JsonSerializer.Serialize(item.Kind, ContentJson.Options).Trim('"'));
            Assert.Equal((string?)original["rarity"], item.Rarity is { } rarity ? JsonSerializer.Serialize(rarity, ContentJson.Options).Trim('"') : null);
            Assert.Equal((int?)original["price"], item.Price);
            Assert.Equal((bool)original["hostile"]!, item.Hostile);
            Assert.Equal((bool?)original["stackable"] ?? false, item.Stackable);
            Assert.Equal(original["tags"]!.AsArray().Select(t => (string)t!).ToList(), [.. item.Tags ?? []]);
            Assert.False(item.Manual);

            // A flag the pack has is kept; a text that promises one more («невозможно уничтожить») adds it (D-478)
            Assert.True(item.Unstealable || !((bool?)original["unstealable"] ?? false));
            Assert.True(item.Undestroyable || !((bool?)original["undestroyable"] ?? false));
            Assert.Equal(item.Description.Contains("невозможно украсть", StringComparison.OrdinalIgnoreCase), item.Unstealable);
        }
    }

    [Fact]
    public void Every_extension_example_of_the_doc_is_its_content_file()
    {
        // CONTENT.md «Примеры расширений формата»: each example is the file of content/items as it is (D-471)
        var files = EconomyScenario.RepositoryPack().Objects.ToDictionary(o => o.Id);
        var examples = Content.ContentExamplesTests.ExampleObjects();
        var ids = Content.ContentExamplesTests.ExtensionExampleIds();

        Assert.Equal(11, ids.Count);
        foreach (var id in ids)
        {
            Assert.Equal(files[id], examples[id]);
        }
    }

    [Fact]
    public void Imported_texts_speak_the_glossary()
    {
        // «Золото = монетки»: the old words do not reach players (README, GLOSSARY)
        var imported = ImportedIds();
        foreach (var item in EconomyScenario.RepositoryPack().Objects.Where(o => imported.Contains(o.Id)))
        {
            foreach (var word in new[] { "золот", "Предмет.", "дефолт", "Кубов", "Кубивок" })
            {
                Assert.DoesNotContain(word, item.Description, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}

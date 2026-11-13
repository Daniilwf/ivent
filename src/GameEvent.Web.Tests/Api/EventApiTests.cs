using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Events through the API (stage 5, 5.1): a stop on an event cell draws from the deck and the season view gives the drawn
/// event to its player only (<c>me.lastEvent</c>), the feed shows the draw with the event's name; the test endpoint that
/// loads a pack is the admin's, checks its input and refuses events while <c>features.events</c> is off (D-532, D-534).
/// </summary>
public sealed class EventApiTests : IAsyncLifetime
{
    // Every die shows 1 and every ticket is the first entry: three dice walk three steps, the deck gives its first event
    private readonly SiteFactory _site = new() { Random = new AlwaysFirst() };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Season => $"/api/seasons/{SiteFactory.SeasonId}";

    private static string Content => $"/api/test/seasons/{SiteFactory.SeasonId}/content";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_stop_on_an_event_cell_gives_the_drawn_event_to_its_player_and_the_feed()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        await SwitchOnEventsAsync(admin);
        await ApiCalls.PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/map/publish", new { commandId = Guid.NewGuid(), map = EventMap(), comment = "Клетки ивентов" });
        await PublishRepositoryContentAsync(admin);

        await ApiCalls.PostOkAsync(vasya, $"{Season}/roll");
        await ApiCalls.PostOkAsync(vasya, $"{Season}/start");
        await ApiCalls.PostOkAsync(vasya, $"{Season}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });

        var drawn = (await MeAsync(vasya)).GetProperty("lastEvent");
        Assert.Equal(("good", "good", "tailwind", "Попутный ветер", "cell"), (
            drawn.GetProperty("deck").GetString(), drawn.GetProperty("kind").GetString(), drawn.GetProperty("objectId").GetString(),
            drawn.GetProperty("name").GetString(), drawn.GetProperty("source").GetString()));
        Assert.StartsWith("В следующем броске", drawn.GetProperty("description").GetString(), StringComparison.Ordinal);

        // Another player's view has no event of Вася's; the feed shows the draw with its name to everyone
        Assert.Equal(JsonValueKind.Null, (await MeAsync(petya)).GetProperty("lastEvent").ValueKind);
        var feed = await ApiCalls.OkAsync(await petya.GetAsync($"{Season}/feed", Ct));
        Assert.Contains(feed.GetProperty("entries").EnumerateArray(), e => e.GetProperty("type").GetString() == "event-drawn");
        Assert.Contains(feed.GetProperty("objects").EnumerateArray(), o => o.GetProperty("id").GetString() == "tailwind" && o.GetProperty("name").GetString() == "Попутный ветер");
    }

    [Fact]
    public async Task The_admin_content_editor_checks_and_publishes_events_and_decks_by_the_season_flag()
    {
        var admin = await _site.SignedInAsync("admin");
        var editor = $"/api/admin/seasons/{SiteFactory.SeasonId}/content";
        var pack = EventPack();

        // Events off: the editor's check refuses the events as the engine would (D-532)
        await SwitchOnAsync(admin, events: false);
        var off = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{editor}/check", pack, Ct));
        Assert.False(off.GetProperty("valid").GetBoolean());

        await SwitchOnEventsAsync(admin);
        var on = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{editor}/check", pack, Ct));
        Assert.True(on.GetProperty("valid").GetBoolean(), on.ToString());
        var version = (await ApiCalls.OkAsync(await admin.GetAsync(editor, Ct))).GetProperty("version").GetInt32();
        await ApiCalls.PostOkAsync(admin, $"{editor}/publish", new { commandId = Guid.NewGuid(), expectedVersion = version, pack, comment = "Колоды" });
        Assert.Equal(version + 1, (await ApiCalls.OkAsync(await admin.GetAsync(editor, Ct))).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Without_events_the_view_has_no_drawn_event_and_a_pack_with_events_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchOnAsync(admin, events: false);

        Assert.Equal(JsonValueKind.Null, (await MeAsync(vasya)).GetProperty("lastEvent").ValueKind);
        var response = await admin.PostAsJsonAsync(Content, RepositoryContent(), Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("content.invalid", JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Only_the_admin_loads_content()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(Content, RepositoryContent(), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Content_that_does_not_parse_is_invalid_input()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Content, new { objects = new[] { """{ "id": "x", "kind": "item", "nme": "?" }""" }, wheels = Array.Empty<string>(), comment = "Сломано" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Task SwitchOnEventsAsync(HttpClient admin) => SwitchOnAsync(admin, events: true);

    /// <summary>The graph map, three dice a completion, items and — when <paramref name="events"/> — events with their rules.</summary>
    private static async Task SwitchOnAsync(HttpClient admin, bool events)
    {
        using var current = JsonDocument.Parse(await admin.GetStringAsync($"{Season}/rules", Ct));
        var ruleset = JsonNode.Parse(current.RootElement.GetProperty("ruleset").GetRawText())!;
        ruleset["features"]!["mapMode"] = "graph";
        ruleset["features"]!["items"] = true;
        ruleset["features"]!["events"] = events;
        ruleset["events"] = new JsonObject { ["defaultDeck"] = "risky", ["irlProof"] = new JsonObject { ["maxFiles"] = 5 } };
        ruleset["reward"]!["diceCount"]!["min"] = 3;
        ruleset["reward"]!["diceCount"]!["max"] = 3;
        var response = await admin.PutAsJsonAsync(
            $"/api/admin/seasons/{SiteFactory.SeasonId}/rules",
            new { commandId = Guid.NewGuid(), expectedVersion = current.RootElement.GetProperty("version").GetInt32(), ruleset },
            Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>start → c1 → c2 → e (an event cell of the good deck) → c3 → finish: three steps stop on e.</summary>
    private static JsonObject EventMap()
    {
        string[] ids = ["start", "c1", "c2", "e", "c3", "finish"];
        var cells = new JsonArray();
        var edges = new JsonArray();
        for (var i = 0; i < ids.Length; i++)
        {
            var type = ids[i] switch { "start" => "start", "finish" => "finish", "e" => "event", _ => "empty" };
            var cell = new JsonObject { ["id"] = ids[i], ["type"] = type, ["x"] = i * 100, ["y"] = 100 };
            if (type == "event")
            {
                cell["deck"] = "good";
            }

            cells.Add(cell);
            if (i > 0)
            {
                edges.Add(new JsonObject { ["from"] = ids[i - 1], ["to"] = ids[i], ["isDefaultForward"] = true, ["isPrimaryBackward"] = true });
            }
        }

        return new JsonObject { ["cells"] = cells, ["edges"] = edges, ["zones"] = new JsonArray() };
    }

    /// <summary>The same files as one pack JSON, as the admin's content editor sends it.</summary>
    private static JsonObject EventPack()
    {
        var (objectFiles, wheelFiles) = RepositoryFiles();
        var objects = new JsonArray([.. objectFiles.Select(j => JsonNode.Parse(j))]);
        var wheels = new JsonArray([.. wheelFiles.Select(j => JsonNode.Parse(j))]);
        return new JsonObject { ["objects"] = objects, ["wheels"] = wheels };
    }

    private static Task PublishRepositoryContentAsync(HttpClient admin) => ApiCalls.PostOkAsync(admin, Content, RepositoryContent());

    /// <summary>The events and decks of the repository (D-533) with the one item they give (forced-genre), as their files.</summary>
    private static object RepositoryContent()
    {
        var (objects, wheels) = RepositoryFiles();
        return new { objects, wheels, comment = "Ивенты" };
    }

    private static (string[] Objects, string[] Wheels) RepositoryFiles()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "content", "decks")))
        {
            directory = directory.Parent;
        }

        var content = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("No content/ found."), "content");
        string[] Read(string dir) =>
            [.. Directory.EnumerateFiles(Path.Combine(content, dir), "*.json").Order(StringComparer.Ordinal).Select(File.ReadAllText)];
        return ([.. Read("events"), File.ReadAllText(Path.Combine(content, "items", "forced-genre.json"))], Read("decks"));
    }

    private static async Task<JsonElement> MeAsync(HttpClient player) =>
        (await ApiCalls.OkAsync(await player.GetAsync(Season, Ct))).GetProperty("me");

    private sealed class AlwaysFirst : GameEvent.Engine.Kernel.IRandomSource
    {
        public int NextInt(int minInclusive, int maxExclusive) => minInclusive;
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Content;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Web.Content;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// EE1–EE8: the content editor's API (4.12, D-490…D-493) — the admin reads the content in force with its versions and
/// the deleted definitions, reads an older version, the schema, checks a pack (every problem with the path of its
/// field) and publishes a new version through the queue made from the version in force; nobody else can, a malformed
/// body is 400 and a pack past the size limit 413.
/// </summary>
public sealed class AdminContentApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Admin => $"/api/admin/seasons/{SiteFactory.SeasonId}/content";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    private static JsonObject Item(string id, string name, int steps) => new()
    {
        ["id"] = id,
        ["kind"] = "item",
        ["name"] = name,
        ["description"] = "Сдвинься вперёд.",
        ["rarity"] = "common",
        ["price"] = 10,
        ["window"] = "anytime",
        ["effect"] = new JsonObject
        {
            ["target"] = new JsonObject { ["selector"] = "self" },
            ["actions"] = new JsonArray(new JsonObject { ["type"] = "move", ["steps"] = steps }),
        },
    };

    private static JsonObject Pack(params JsonObject[] objects) => new()
    {
        ["objects"] = new JsonArray([.. objects]),
        ["wheels"] = new JsonArray(),
    };

    private async Task ItemsOnAsync()
    {
        await using var db = _site.NewDb();
        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
        await _site.SendAsync(new Engine.Rulesets.ChangeRuleset(state.Rules with { Features = state.Rules.Features with { Items = true } }));
    }

    private static Task<HttpResponseMessage> PublishAsync(HttpClient admin, JsonObject pack, int expectedVersion, string? comment = "Новые предметы") =>
        admin.PostAsJsonAsync($"{Admin}/publish", new { commandId = Guid.NewGuid(), expectedVersion, pack, comment }, Ct);

    private static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");

    // ---- Reading ----

    [Fact]
    public async Task A_season_without_content_has_version_0_an_empty_pack_and_says_items_are_off()
    {
        var admin = await _site.SignedInAsync("admin");

        var view = await ApiCalls.OkAsync(await admin.GetAsync(Admin, Ct));

        Assert.False(view.GetProperty("itemsEnabled").GetBoolean());
        Assert.Equal(0, view.GetProperty("version").GetInt32());
        Assert.Empty(view.GetProperty("pack").GetProperty("objects").EnumerateArray());
        Assert.Empty(view.GetProperty("history").EnumerateArray());
        Assert.Equal("active", view.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Published_versions_give_the_pack_in_force_the_history_newest_first_and_the_deleted_definitions()
    {
        var admin = await _site.SignedInAsync("admin");
        await ItemsOnAsync();
        await ApiCalls.OkAsync(await PublishAsync(admin, Pack(Item("orange", "Апельсин", 1), Item("banana", "Банан", 2)), 0, "Первые предметы"));
        await ApiCalls.OkAsync(await PublishAsync(admin, Pack(Item("orange", "Апельсин", 3), Item("cherry", "Вишня", 1)), 1, "Банан убран"));

        var view = await ApiCalls.OkAsync(await admin.GetAsync(Admin, Ct));

        Assert.True(view.GetProperty("itemsEnabled").GetBoolean());
        Assert.Equal(2, view.GetProperty("version").GetInt32());
        var objects = view.GetProperty("pack").GetProperty("objects").EnumerateArray().ToList();
        Assert.Equal(["orange", "cherry"], objects.Select(o => o.GetProperty("id").GetString()));
        // Null fields of the log are left out: the editor shows the content as it is written
        Assert.False(objects[0].TryGetProperty("proof", out _));
        Assert.Equal(3, objects[0].GetProperty("effect").GetProperty("actions")[0].GetProperty("steps").GetInt32());
        var history = view.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal([2, 1], history.Select(h => h.GetProperty("version").GetInt32()));
        Assert.Equal("Банан убран", history[0].GetProperty("comment").GetString());
        Assert.Equal("admin", history[0].GetProperty("authorName").GetString());
        Assert.Equal(["cherry"], Strings(history[0], "added"));
        Assert.Equal(["orange"], Strings(history[0], "changed"));
        Assert.Equal(["banana"], Strings(history[0], "removed"));
        Assert.Equal(["orange", "banana"], Strings(history[1], "added"));
        var deleted = Assert.Single(view.GetProperty("deleted").EnumerateArray());
        Assert.Equal(("banana", "Банан", "item", 2), (
            deleted.GetProperty("id").GetString(),
            deleted.GetProperty("name").GetString(),
            deleted.GetProperty("kind").GetString(),
            deleted.GetProperty("deletedIn").GetInt32()));
    }

    [Fact]
    public async Task An_older_version_is_read_whole_and_an_unknown_one_is_404()
    {
        var admin = await _site.SignedInAsync("admin");
        await ItemsOnAsync();
        await ApiCalls.OkAsync(await PublishAsync(admin, Pack(Item("orange", "Апельсин", 1)), 0));
        await ApiCalls.OkAsync(await PublishAsync(admin, Pack(Item("orange", "Апельсин", 2)), 1));

        var first = await ApiCalls.OkAsync(await admin.GetAsync($"{Admin}/versions/1", Ct));

        Assert.Equal(1, first.GetProperty("version").GetInt32());
        Assert.Equal(1, first.GetProperty("pack").GetProperty("objects")[0].GetProperty("effect").GetProperty("actions")[0].GetProperty("steps").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Admin}/versions/3", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_schema_is_the_committed_one()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.GetAsync("/api/admin/content/schema", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/schema+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ContentSchema.Generate(), await response.Content.ReadAsStringAsync(Ct));
    }

    // ---- Checking ----

    [Fact]
    public async Task The_repository_content_passes_the_check()
    {
        var admin = await _site.SignedInAsync("admin");

        var check = await ApiCalls.OkAsync(await admin.PostAsync($"{Admin}/check", Raw(RepositoryPack().ToJsonString()), Ct));

        Assert.True(check.GetProperty("valid").GetBoolean(), check.ToString());
        Assert.Empty(check.GetProperty("problems").EnumerateArray());
    }

    [Fact]
    public async Task Every_content_rule_broken_is_named_with_the_path_of_its_field()
    {
        var admin = await _site.SignedInAsync("admin");
        var broken = Item("orange", "", 1);
        broken.Remove("window");
        var pack = Pack(Item("banana", "Банан", 1), broken);
        pack["wheels"] = new JsonArray(new JsonObject
        {
            ["id"] = "box",
            ["name"] = "Коробка",
            ["entries"] = new JsonArray(new JsonObject { ["objectId"] = "nothing", ["weight"] = 1 }),
        });

        var check = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{Admin}/check", pack, Ct));

        Assert.False(check.GetProperty("valid").GetBoolean());
        var paths = check.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("path").GetString()).ToList();
        Assert.Contains("objects[1].name", paths);
        Assert.Contains("objects[1].window", paths);
        Assert.Contains("wheels[0].entries[0].objectId", paths);
    }

    [Theory]
    [InlineData("""{"objects":[{"id":"x","kind":"item","name":"X","description":"Y","window":"anytime","effect":{"actions":[{"type":"move","steps":"сто"}]}}]}""", "objects[0].effect.actions[0].steps")]
    [InlineData("""{"objects":[{"id":"x","kind":"wand","name":"X","description":"Y"}]}""", "objects[0].kind")]
    [InlineData("""{"objects":[{"id":"x","kind":"item","name":"X","description":"Y","colour":"red"}]}""", "objects[0]")]
    [InlineData("""{"objects":[{"id":"x","kind":"item","name":"X","description":"Y","window":"anytime","effect":{"actions":[{"type":"explode"}]}}]}""", "objects[0].effect.actions[0]")]
    [InlineData("""{"objects":[],"wheels":[{"id":"w","entries":[]}]}""", "wheels[0]")]
    [InlineData("""{"objects":[{"id":"x","kind":"item","name":"X","description":"Y","window":"anytime","effect":{"conditions":[],"actions":[]}}]}""", "objects[0].effect")]
    public async Task A_value_of_a_wrong_type_is_named_with_its_path(string pack, string path)
    {
        var admin = await _site.SignedInAsync("admin");

        var check = await ApiCalls.OkAsync(await admin.PostAsync($"{Admin}/check", Raw(pack), Ct));

        Assert.False(check.GetProperty("valid").GetBoolean());
        var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
        var actual = problem.GetProperty("path").GetString()!;
        Assert.True(actual.StartsWith(path, StringComparison.Ordinal), $"{actual}: {problem}");
        // The path stands apart from the message: the message says only what is wrong
        var message = problem.GetProperty("message").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.DoesNotContain("Path:", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Invalid list item", message, StringComparison.Ordinal);
        Assert.DoesNotContain("$.", message, StringComparison.Ordinal);
        // Our own words, never the parser's .NET type names (SEC-2)
        Assert.DoesNotContain("GameEvent.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"objects":[],"wheels":[{"id":"w","entries":[]}]}""", "wheels[0]", "Field «name» is required.")]
    [InlineData("""{"objects":[{"id":"x","kind":"item","name":"X","description":"Y","colour":"red"}]}""", "objects[0].colour", "Field «colour» is unknown.")]
    [InlineData("""{"objects":[{"id":"x","kind":"item","name":"X","description":"Y","price":"ten"}]}""", "objects[0].price", "A value of a wrong type.")]
    public async Task A_missing_unknown_or_mistyped_field_is_told_in_our_words(string pack, string path, string message)
    {
        var admin = await _site.SignedInAsync("admin");

        var check = await ApiCalls.OkAsync(await admin.PostAsync($"{Admin}/check", Raw(pack), Ct));

        var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
        Assert.Equal((path, message), (problem.GetProperty("path").GetString(), problem.GetProperty("message").GetString()));
    }

    [Theory]
    [InlineData("""{"objects":[null],"wheels":[]}""", "objects[0]")]
    [InlineData("""{"objects":[],"wheels":[null]}""", "wheels[0]")]
    [InlineData("""{"objects":[],"wheels":[{"id":"w","name":"W","entries":[null]}]}""", "wheels[0].entries[0]")]
    [InlineData("""{"objects":[{"id":"x","kind":"item","name":"X","description":"Y","window":"anytime","effect":{"actions":[null]}}]}""", "objects[0].effect.actions[0]")]
    public async Task A_null_in_a_list_of_the_pack_is_a_problem_at_its_place_not_a_500(string pack, string path)
    {
        var admin = await _site.SignedInAsync("admin");
        await ItemsOnAsync();

        var check = await ApiCalls.OkAsync(await admin.PostAsync($"{Admin}/check", Raw(pack), Ct));
        Assert.False(check.GetProperty("valid").GetBoolean());
        Assert.Equal(path, Assert.Single(check.GetProperty("problems").EnumerateArray()).GetProperty("path").GetString());

        var published = await admin.PostAsync(
            $"{Admin}/publish",
            Raw($$"""{"commandId":"{{Guid.NewGuid()}}","expectedVersion":0,"pack":{{pack}},"comment":"Пусто"}"""),
            Ct);
        Assert.Equal(HttpStatusCode.BadRequest, published.StatusCode);
        var problem = JsonDocument.Parse(await published.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal([path], problem.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("path").GetString()));
    }

    [Fact]
    public async Task A_pack_past_the_object_limit_is_named_at_its_objects()
    {
        var admin = await _site.SignedInAsync("admin");
        var pack = Pack([.. Enumerable.Range(0, AdminContentEndpoints.MaxObjects + 1).Select(i => Item($"i{i}", "П", 1))]);

        var check = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{Admin}/check", pack, Ct));

        Assert.Equal("objects", Assert.Single(check.GetProperty("problems").EnumerateArray()).GetProperty("path").GetString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("42")]
    public async Task A_check_of_something_that_is_not_a_pack_object_is_400(string body)
    {
        var admin = await _site.SignedInAsync("admin");

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"{Admin}/check", Raw(body), Ct)).StatusCode);
    }

    // ---- Publishing ----

    [Fact]
    public async Task A_published_pack_is_a_command_in_the_log_with_its_comment()
    {
        var admin = await _site.SignedInAsync("admin");
        await ItemsOnAsync();

        var body = await ApiCalls.OkAsync(await PublishAsync(admin, Pack(Item("orange", "Апельсин", 1)), 0, "Апельсин"));

        Assert.Equal("content-published", body.GetProperty("events")[0].GetProperty("type").GetString());
        await using var db = _site.NewDb();
        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
        Assert.Equal("Апельсин", state.Catalog.Live("orange")!.Name);
    }

    [Fact]
    public async Task An_invalid_pack_is_400_with_the_same_problems_as_the_check()
    {
        var admin = await _site.SignedInAsync("admin");
        await ItemsOnAsync();

        var response = await PublishAsync(admin, Pack(Item("orange", "", 1)), 0);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Contains("objects[0].name", problem.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("path").GetString()));
    }

    [Fact]
    public async Task A_pack_made_from_an_older_version_is_409_with_its_code()
    {
        var admin = await _site.SignedInAsync("admin");
        await ItemsOnAsync();
        await ApiCalls.OkAsync(await PublishAsync(admin, Pack(Item("orange", "Апельсин", 1)), 0));

        await AssertRejectedAsync(await PublishAsync(admin, Pack(Item("orange", "Апельсин", 2)), 0), "content.versionChanged");
    }

    [Fact]
    public async Task With_items_off_the_pack_is_checked_but_not_published()
    {
        var admin = await _site.SignedInAsync("admin");

        var check = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{Admin}/check", Pack(Item("orange", "Апельсин", 1)), Ct));
        Assert.True(check.GetProperty("valid").GetBoolean());

        await AssertRejectedAsync(await PublishAsync(admin, Pack(Item("orange", "Апельсин", 1)), 0), "feature.disabled");
    }

    [Fact]
    public async Task The_same_pack_again_is_409_unchanged()
    {
        var admin = await _site.SignedInAsync("admin");
        await ItemsOnAsync();
        await ApiCalls.OkAsync(await PublishAsync(admin, Pack(Item("orange", "Апельсин", 1)), 0));

        await AssertRejectedAsync(await PublishAsync(admin, Pack(Item("orange", "Апельсин", 1)), 1), "content.unchanged");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task A_publication_without_a_comment_is_400(string? comment)
    {
        var admin = await _site.SignedInAsync("admin");
        await ItemsOnAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await PublishAsync(admin, Pack(Item("orange", "Апельсин", 1)), 0, comment)).StatusCode);
    }

    [Theory]
    [InlineData("""{"expectedVersion":0,"pack":{"objects":[]},"comment":"Пусто"}""")]
    [InlineData("""{"commandId":"00000000-0000-0000-0000-000000000000","expectedVersion":0,"pack":{"objects":[]},"comment":"Пусто"}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000001","pack":{"objects":[]},"comment":"Пусто"}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000001","expectedVersion":1.5,"pack":{"objects":[]},"comment":"Пусто"}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000001","expectedVersion":0,"pack":[],"comment":"Пусто"}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000001","expectedVersion":0,"pack":{"objects":[]},"comment":7}""")]
    [InlineData("not json")]
    public async Task A_malformed_publication_is_400(string body)
    {
        var admin = await _site.SignedInAsync("admin");
        await ItemsOnAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"{Admin}/publish", Raw(body), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_comment_past_500_characters_is_400()
    {
        var admin = await _site.SignedInAsync("admin");
        await ItemsOnAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await PublishAsync(admin, Pack(Item("orange", "Апельсин", 1)), 0, new string('я', 501))).StatusCode);
    }

    // ---- Limits and rights ----

    [Fact]
    public async Task A_big_pack_fits_the_request_limit_and_one_past_it_is_413()
    {
        var admin = await _site.SignedInAsync("admin");
        var big = RepositoryPack();
        while (big.ToJsonString().Length <= Hosting.WebSecurity.ApiBodyLimitBytes)
        {
            var objects = big["objects"]!.AsArray();
            big = Pack([.. objects.Select(o => o!.DeepClone().AsObject()), .. objects.Select((o, i) => Renamed(o!.AsObject(), objects.Count + i))]);
        }

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{Admin}/check", big, Ct)).StatusCode);

        var pad = new string('x', (int)AdminContentEndpoints.MaxContentRequestBytes);
        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            (await admin.PostAsync($"{Admin}/check", Raw($"{{\"objects\":[],\"pad\":\"{pad}\"}}"), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            (await admin.PostAsync($"{Admin}/publish", Raw($"{{\"commandId\":\"{Guid.NewGuid()}\",\"expectedVersion\":0,\"comment\":\"{pad}\",\"pack\":{{}}}}"), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_player_gets_403_everywhere()
    {
        var vasya = await _site.SignedInAsync("vasya");

        Assert.Equal(HttpStatusCode.Forbidden, (await vasya.GetAsync(Admin, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vasya.GetAsync($"{Admin}/versions/1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vasya.GetAsync("/api/admin/content/schema", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vasya.PostAsJsonAsync($"{Admin}/check", Pack(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PublishAsync(vasya, Pack(Item("orange", "Апельсин", 1)), 0)).StatusCode);
    }

    [Fact]
    public async Task A_signed_out_visitor_gets_401_everywhere()
    {
        var anonymous = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Admin, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Admin}/versions/1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/content/schema", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{Admin}/check", Pack(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PublishAsync(anonymous, Pack(Item("orange", "Апельсин", 1)), 0)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_season_is_404()
    {
        var admin = await _site.SignedInAsync("admin");
        var unknown = $"/api/admin/seasons/{Guid.NewGuid()}/content";

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(unknown, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{unknown}/versions/1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"{unknown}/check", Pack(), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync($"{unknown}/publish", new { commandId = Guid.NewGuid(), expectedVersion = 0, pack = Pack(), comment = "Пусто" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Text_with_markup_is_kept_as_text()
    {
        // The editor and the player's screens render it as text (EE7); the API keeps it as it was typed
        var admin = await _site.SignedInAsync("admin");
        await ItemsOnAsync();
        var item = Item("orange", "<img src=x onerror=alert(1)>", 1);
        await ApiCalls.OkAsync(await PublishAsync(admin, Pack(item), 0));

        var view = await ApiCalls.OkAsync(await admin.GetAsync(Admin, Ct));

        Assert.Equal("<img src=x onerror=alert(1)>", view.GetProperty("pack").GetProperty("objects")[0].GetProperty("name").GetString());
    }

    private static List<string?> Strings(JsonElement e, string name) => [.. e.GetProperty(name).EnumerateArray().Select(x => x.GetString())];

    private static JsonObject Renamed(JsonObject o, int i)
    {
        var copy = o.DeepClone().AsObject();
        copy["id"] = $"{o["id"]!.GetValue<string>()}-{i}";
        return copy;
    }

    // The files of /content/items and /content/wheels as one pack, in the order of the file names
    private static JsonObject RepositoryPack()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GameEvent.slnx")))
        {
            dir = dir.Parent;
        }

        var root = Path.Combine(dir!.FullName, "content");
        JsonArray Read(string folder) => new([.. Directory.EnumerateFiles(Path.Combine(root, folder), "*.json")
            .Order(StringComparer.Ordinal)
            .Select(f => JsonNode.Parse(File.ReadAllText(f)))]);
        return new JsonObject { ["objects"] = Read("items"), ["wheels"] = Read("wheels") };
    }

    private static async Task AssertRejectedAsync(HttpResponseMessage response, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }
}

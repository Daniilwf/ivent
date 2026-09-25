using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Web.Tests.Api;
using NetVips;

namespace GameEvent.Web.Tests.Pool;

/// <summary>
/// The pool's API (SPEC «Пул игр», «Дубли», «Удаление мягкое»; D-119): everyone signed in reads and searches; players and the
/// admin add; the admin changes, deletes, restores and keeps the wheel. The seed has Silent Hill, Alan Wake and Outlast
/// (Horror, weight 1).
/// </summary>
public sealed class PoolApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- Reading ----

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    [InlineData("admin")]
    public async Task Everyone_signed_in_reads_the_pool(string login)
    {
        var client = await _site.SignedInAsync(login);

        var titles = (await JsonAsync(client, "/api/pool")).EnumerateArray().Select(g => g.GetProperty("title").GetString());

        Assert.Equal(["Alan Wake", "Outlast", "Silent Hill"], titles);
    }

    [Fact]
    public async Task Anonymous_does_not_read_the_pool()
    {
        var anonymous = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/pool", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_pool_is_searched_by_title_words_and_a_tag()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await AddAsync(vasya, "Tetris Effect", ["Puzzle"]);

        Assert.Equal(["Silent Hill"], Titles(await JsonAsync(vasya, "/api/pool?query=hill%20sil")));
        Assert.Equal(["Tetris Effect"], Titles(await JsonAsync(vasya, "/api/pool?tag=puzzle")));
        Assert.Empty(Titles(await JsonAsync(vasya, "/api/pool?query=wake&tag=Puzzle")));
    }

    [Fact]
    public async Task A_game_card_is_read_by_its_id()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var added = await AddAsync(vasya, "Dead Space", ["Horror"], hours: 12, year: 2008, note: "Любая концовка");

        var card = await JsonAsync(vasya, $"/api/pool/{added.GetProperty("id").GetGuid()}");

        Assert.Equal(("Dead Space", 12m, 2008, "Любая концовка", "vasya"), (card.GetProperty("title").GetString(), card.GetProperty("hours").GetDecimal(), card.GetProperty("year").GetInt32(), card.GetProperty("note").GetString(), card.GetProperty("author").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await vasya.GetAsync($"/api/pool/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Similar_titles_are_shown_before_adding()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await AddAsync(vasya, "Dice Fold", ["Puzzle"]);

        var similar = (await JsonAsync(vasya, "/api/pool/similar?title=Dice%20%26%20Fold")).EnumerateArray().ToList();
        var same = (await JsonAsync(vasya, "/api/pool/similar?title=silent%20hill")).EnumerateArray().ToList();

        Assert.Equal(("Dice Fold", false), (Assert.Single(similar).GetProperty("title").GetString(), similar[0].GetProperty("same").GetBoolean()));
        Assert.True(Assert.Single(same).GetProperty("same").GetBoolean());
        Assert.Empty((await JsonAsync(vasya, "/api/pool/similar?title=Celeste")).EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await vasya.GetAsync("/api/pool/similar?title=%20", Ct)).StatusCode);
    }

    [Fact]
    public async Task Categories_show_their_weights_and_how_many_games_carry_them()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var horror = Assert.Single((await JsonAsync(vasya, "/api/pool/categories")).EnumerateArray());

        Assert.Equal(("Horror", 1, 3), (horror.GetProperty("name").GetString(), horror.GetProperty("weight").GetInt32(), horror.GetProperty("games").GetInt32()));
    }

    // ---- Adding ----

    [Fact]
    public async Task A_player_adds_a_game_and_it_is_rolled_from_the_pool()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var added = await AddAsync(vasya, "Dead Space", ["Horror"], hours: 12);

        Assert.Equal("vasya", added.GetProperty("author").GetString()); // the account's name (the seed names accounts by login)
        Assert.Contains("Dead Space", Titles(await JsonAsync(vasya, "/api/pool?tag=Horror")));
    }

    [Fact]
    public async Task An_alike_title_is_refused_until_the_player_confirms()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var refused = await vasya.PostAsJsonAsync("/api/pool", new { commandId = Guid.NewGuid(), title = "Silent Hill 2", tags = new[] { "Horror" } }, Ct);
        var confirmed = await vasya.PostAsJsonAsync("/api/pool", new { commandId = Guid.NewGuid(), title = "Silent Hill 2", tags = new[] { "Horror" }, force = true }, Ct);

        await RefusedAsync(refused, HttpStatusCode.Conflict, "pool.similar");
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
    }

    [Fact]
    public async Task The_same_title_is_refused_even_confirmed()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync("/api/pool", new { commandId = Guid.NewGuid(), title = " alan wake ", tags = new[] { "Horror" }, force = true }, Ct);

        await RefusedAsync(response, HttpStatusCode.Conflict, "pool.duplicate");
    }

    [Fact]
    public async Task A_player_adds_a_game_with_his_own_cover_only()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        var own = await UploadAsync(vasya);
        var foreign = await UploadAsync(petya);

        var withOwn = await AddAsync(vasya, "Dead Space", ["Horror"], cover: own);
        var withForeign = await vasya.PostAsJsonAsync("/api/pool", new { commandId = Guid.NewGuid(), title = "Prey", tags = new[] { "Horror" }, coverFileId = foreign }, Ct);

        Assert.Equal(own, withOwn.GetProperty("cover").GetProperty("id").GetGuid());
        await RefusedAsync(withForeign, HttpStatusCode.Conflict, "pool.coverUnknown");
    }

    [Fact]
    public async Task A_repeated_add_request_adds_one_game()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var commandId = Guid.NewGuid();

        var first = await vasya.PostAsJsonAsync("/api/pool", new { commandId, title = "Dead Space", tags = new[] { "Horror" } }, Ct);
        var again = await vasya.PostAsJsonAsync("/api/pool", new { commandId, title = "Dead Space", tags = new[] { "Horror" } }, Ct);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, again.StatusCode));
        Assert.Single(Titles(await JsonAsync(vasya, "/api/pool?query=dead")));
    }

    [Theory]
    [InlineData("""{"commandId":"00000000-0000-0000-0000-000000000000","title":"Game","tags":["Horror"]}""")]
    [InlineData("""{"commandId":"7a1e0000-0000-0000-0000-000000000001","tags":["Horror"]}""")]
    [InlineData("""{"commandId":"7a1e0000-0000-0000-0000-000000000002","title":"Game"}""")]
    public async Task An_add_without_a_command_id_a_title_or_tags_is_invalid(string body)
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsync("/api/pool", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_invalid_card_is_a_conflict_with_its_reason()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync("/api/pool", new { commandId = Guid.NewGuid(), title = "Game", tags = new[] { "Horror" }, hours = 0.3 }, Ct);

        await RefusedAsync(response, HttpStatusCode.Conflict, "pool.cardInvalid");
    }

    [Fact]
    public async Task A_spectator_and_anonymous_do_not_add()
    {
        var zritel = await _site.SignedInAsync("zritel");
        var anonymous = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await zritel.PostAsJsonAsync("/api/pool", new { commandId = Guid.NewGuid(), title = "Game", tags = new[] { "Horror" } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/pool", new { commandId = Guid.NewGuid(), title = "Game", tags = new[] { "Horror" } }, Ct)).StatusCode);
    }

    [Fact]
    public async Task An_add_without_the_antiforgery_token_is_refused()
    {
        var vasya = await _site.SignedInAsync("vasya");
        vasya.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");

        Assert.Equal(HttpStatusCode.BadRequest, (await vasya.PostAsJsonAsync("/api/pool", new { commandId = Guid.NewGuid(), title = "Game", tags = new[] { "Horror" } }, Ct)).StatusCode);
    }

    // ---- The admin ----

    [Fact]
    public async Task The_admin_changes_deletes_and_restores_a_game()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        var id = (await AddAsync(vasya, "Dead Space", ["Horror"])).GetProperty("id").GetGuid();

        var changed = await OkAsync(await admin.PutAsJsonAsync($"/api/admin/pool/{id}", new { commandId = Guid.NewGuid(), title = "Dead Space", tags = new[] { "Horror", "Sci-fi" }, hours = 11.5 }, Ct));
        Assert.Equal(["Horror", "Sci-fi"], changed.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));

        await OkAsync(await admin.PostAsJsonAsync($"/api/admin/pool/{id}/delete", new { commandId = Guid.NewGuid() }, Ct));
        Assert.DoesNotContain("Dead Space", Titles(await JsonAsync(vasya, "/api/pool")));
        Assert.DoesNotContain("Dead Space", Titles(await JsonAsync(vasya, "/api/pool?deleted=true")));
        Assert.Contains("Dead Space", Titles(await JsonAsync(admin, "/api/pool?deleted=true")));

        await OkAsync(await admin.PostAsJsonAsync($"/api/admin/pool/{id}/restore", new { commandId = Guid.NewGuid() }, Ct));
        Assert.Contains("Dead Space", Titles(await JsonAsync(vasya, "/api/pool")));
    }

    [Fact]
    public async Task The_admin_keeps_the_wheel()
    {
        var admin = await _site.SignedInAsync("admin");

        var afterSet = await OkAsync(await admin.PutAsJsonAsync("/api/admin/pool/categories/Puzzle", new { commandId = Guid.NewGuid(), weight = 2 }, Ct));
        Assert.Contains(("Puzzle", 2), afterSet.EnumerateArray().Select(c => (c.GetProperty("name").GetString(), c.GetProperty("weight").GetInt32())));

        var afterRemove = await OkAsync(await admin.PostAsJsonAsync("/api/admin/pool/categories/Puzzle/remove", new { commandId = Guid.NewGuid() }, Ct));
        Assert.DoesNotContain("Puzzle", afterRemove.EnumerateArray().Select(c => c.GetProperty("name").GetString()));

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/admin/pool/categories/Nope/remove", new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
        await RefusedAsync(await admin.PutAsJsonAsync("/api/admin/pool/categories/Puzzle", new { commandId = Guid.NewGuid(), weight = 0 }, Ct), HttpStatusCode.Conflict, "pool.categoryInvalid");
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Players_and_spectators_do_not_run_the_pool(string login)
    {
        var client = await _site.SignedInAsync(login);
        var admin = await _site.SignedInAsync("admin");
        var id = (await JsonAsync(admin, "/api/pool")).EnumerateArray().First().GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/admin/pool/{id}", new { commandId = Guid.NewGuid(), title = "X", tags = new[] { "Horror" } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/admin/pool/{id}/delete", new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/admin/pool/categories/Horror", new { commandId = Guid.NewGuid(), weight = 5 }, Ct)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_game_is_not_found_for_the_admin()
    {
        var admin = await _site.SignedInAsync("admin");

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/admin/pool/{Guid.NewGuid()}/delete", new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/api/admin/pool/{Guid.NewGuid()}", new { commandId = Guid.NewGuid(), title = "X", tags = new[] { "Horror" } }, Ct)).StatusCode);
    }

    // ---- Helpers ----

    private static IEnumerable<string?> Titles(JsonElement games) => games.EnumerateArray().Select(g => g.GetProperty("title").GetString());

    private static async Task<JsonElement> AddAsync(HttpClient client, string title, string[] tags, decimal? hours = null, int? year = null, string? note = null, Guid? cover = null) =>
        await OkAsync(await client.PostAsJsonAsync("/api/pool", new { commandId = Guid.NewGuid(), title, tags, hours, year, note, coverFileId = cover, force = true }, Ct));

    private static async Task<Guid> UploadAsync(HttpClient client)
    {
        using var grey = Image.Black(30, 40, bands: 3) + 90;
        using var picture = grey.Cast(Enums.BandFormat.Uchar);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Guid.NewGuid().ToString()), "commandId");
        var file = new ByteArrayContent(picture.PngsaveBuffer());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "cover.png");
        return (await OkAsync(await client.PostAsync("/api/files", form, Ct))).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string url)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync(url, Ct));
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task RefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(code, doc.RootElement.GetProperty("code").GetString());
    }
}

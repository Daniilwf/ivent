using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Rulesets;
using GameEvent.Infrastructure.EventLog;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The economy over HTTP (EC17, D-440…D-443): my inventory with what each object allows now, «Использовать» with a target
/// and answers through the queue, the shop and the bets; the admin loads a content pack and corrects inventories. Each
/// endpoint: success, another role or another player's season (403), anonymous (401), invalid input (400), and the
/// engine's refusals as 409 with their codes. The pack is the repository's <c>content/items</c> and <c>content/wheels</c>.
/// </summary>
public sealed class EconomyApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    private static string AdminUrl(string action) => $"/api/admin/seasons/{SiteFactory.SeasonId}/{action}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- Setup ----

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "content", "items")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root with content/items is not found.");
    }

    /// <summary>The repository's content as one pack, as the admin's page sends it: {objects, wheels}.</summary>
    internal static JsonObject RepositoryPack()
    {
        var root = Path.Combine(RepositoryRoot(), "content");
        static JsonArray Read(string dir) =>
            [.. Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal).Select(f => JsonNode.Parse(File.ReadAllText(f)))];
        return new JsonObject { ["objects"] = Read(Path.Combine(root, "items")), ["wheels"] = Read(Path.Combine(root, "wheels")) };
    }

    private async Task FeaturesAsync(bool items = true, bool shop = true, bool bets = true)
    {
        await using var db = _site.NewDb();
        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
        await _site.SendAsync(new ChangeRuleset(state.Rules with { Features = state.Rules.Features with { Items = items, Shop = shop, Bets = bets } }));
    }

    private async Task<HttpClient> EconomyOnAsync()
    {
        await FeaturesAsync();
        var admin = await _site.SignedInAsync("admin");
        await ApiCalls.PostOkAsync(admin, AdminUrl("content"), new { commandId = Guid.NewGuid(), pack = RepositoryPack(), comment = "Первые предметы" });
        return admin;
    }

    private async Task GiveAsync(HttpClient admin, string login, string objectId) =>
        await ApiCalls.PostOkAsync(
            admin, AdminUrl($"players/{_site.Players[login]}/inventory"), new { commandId = Guid.NewGuid(), giveObjectId = objectId, comment = "Подарок" });

    private static async Task<JsonElement> EconomyAsync(HttpClient client) =>
        await ApiCalls.OkAsync(await client.GetAsync(Url("economy"), Ct));

    private static async Task<JsonElement> ItemAsync(HttpClient client, string objectId) =>
        (await EconomyAsync(client)).GetProperty("items").EnumerateArray().First(i => i.GetProperty("objectId").GetString() == objectId);

    private async Task<int> CoinsAsync(string login)
    {
        await using var db = _site.NewDb();
        return (await db.SeasonPlayers.AsNoTracking().SingleAsync(p => p.Id == _site.Players[login], Ct)).Coins;
    }

    private async Task SetCoinsAsync(string login, int delta) =>
        await _site.SendAsync(new Engine.Players.AdjustPlayer(_site.Players[login], "Монетки для теста", CoinsDelta: delta));

    private static async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    // ---- The inventory: success ----

    [Fact]
    public async Task My_inventory_names_each_object_and_offers_only_the_targets_the_engine_accepts()
    {
        var admin = await EconomyOnAsync();
        await GiveAsync(admin, "vasya", "bird-thief");
        await GiveAsync(admin, "vasya", "orange");
        var vasya = await _site.SignedInAsync("vasya");

        var economy = await EconomyAsync(vasya);

        Assert.Equal(0, economy.GetProperty("coins").GetInt32());
        Assert.Equal(5, economy.GetProperty("inventoryLimit").GetInt32());
        var thief = await ItemAsync(vasya, "bird-thief");
        Assert.Equal("Птичкерс", thief.GetProperty("name").GetString());
        Assert.Equal("Укради у выбранного игрока 1d4 монеток.", thief.GetProperty("description").GetString());
        Assert.True(thief.GetProperty("usable").GetBoolean());
        Assert.True(thief.GetProperty("choosesTarget").GetBoolean());
        var target = Assert.Single(thief.GetProperty("targets").EnumerateArray());
        Assert.Equal((_site.Players["petya"], "petya"), (target.GetProperty("playerId").GetGuid(), target.GetProperty("name").GetString()));
        var orange = await ItemAsync(vasya, "orange");
        Assert.False(orange.GetProperty("choosesTarget").GetBoolean());
        Assert.Equal(0, orange.GetProperty("targets").GetArrayLength());
        Assert.NotEqual(JsonValueKind.Null, economy.GetProperty("shop").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, economy.GetProperty("bets").ValueKind);
    }

    [Fact]
    public async Task The_season_view_says_which_economy_mechanics_are_on()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var off = await ApiCalls.OkAsync(await vasya.GetAsync(Url(""), Ct));
        Assert.False(off.GetProperty("economy").GetProperty("items").GetBoolean());

        await FeaturesAsync(items: true, shop: false, bets: true);

        var on = (await ApiCalls.OkAsync(await vasya.GetAsync(Url(""), Ct))).GetProperty("economy");
        Assert.Equal((true, false, true), (on.GetProperty("items").GetBoolean(), on.GetProperty("shop").GetBoolean(), on.GetProperty("bets").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, (await EconomyAsync(vasya)).GetProperty("shop").ValueKind);
    }

    [Fact]
    public async Task With_items_off_the_inventory_and_a_use_are_refused()
    {
        var vasya = await _site.SignedInAsync("vasya");

        await AssertConflictAsync(await vasya.GetAsync(Url("economy"), Ct), "feature.disabled");
        await AssertConflictAsync(
            await vasya.PostAsJsonAsync(Url($"items/{Guid.NewGuid()}/use"), new { commandId = Guid.NewGuid() }, Ct), "feature.disabled");
    }

    // ---- «Использовать» ----

    [Fact]
    public async Task Using_an_item_on_another_player_acts_once_per_command_id()
    {
        var admin = await EconomyOnAsync();
        await GiveAsync(admin, "vasya", "bird-thief");
        await SetCoinsAsync("petya", 10);
        var vasya = await _site.SignedInAsync("vasya");
        var thief = await ItemAsync(vasya, "bird-thief");
        var commandId = Guid.NewGuid();
        var body = new { commandId, targetPlayerId = _site.Players["petya"] };

        var response = await vasya.PostAsJsonAsync(Url($"items/{thief.GetProperty("instanceId").GetGuid()}/use"), body, Ct);
        var again = await vasya.PostAsJsonAsync(Url($"items/{thief.GetProperty("instanceId").GetGuid()}/use"), body, Ct);

        var types = (await ApiCalls.OkAsync(response)).GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToList();
        Assert.Contains("item-used", types);
        Assert.True((await ApiCalls.OkAsync(again)).GetProperty("duplicate").GetBoolean());
        var stolen = 10 - await CoinsAsync("petya");
        Assert.InRange(stolen, 1, 4);
        Assert.Equal(stolen, await CoinsAsync("vasya"));
        Assert.Equal(0, (await EconomyAsync(vasya)).GetProperty("items").GetArrayLength());
        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Events.CountAsync(e => e.SeasonId == SiteFactory.SeasonId && e.Type == "item-used", Ct));
    }

    [Fact]
    public async Task The_feed_names_the_item_used_and_its_target()
    {
        var admin = await EconomyOnAsync();
        await GiveAsync(admin, "vasya", "bird-thief");
        var vasya = await _site.SignedInAsync("vasya");
        var thief = await ItemAsync(vasya, "bird-thief");
        await ApiCalls.PostOkAsync(
            vasya, Url($"items/{thief.GetProperty("instanceId").GetGuid()}/use"), new { commandId = Guid.NewGuid(), targetPlayerId = _site.Players["petya"] });
        var petya = await _site.SignedInAsync("petya");

        var feed = await ApiCalls.OkAsync(await petya.GetAsync(Url("feed"), Ct));

        var used = feed.GetProperty("entries").EnumerateArray().First(e => e.GetProperty("type").GetString() == "item-used");
        Assert.Equal("bird-thief", used.GetProperty("data").GetProperty("objectId").GetString());
        Assert.Equal(_site.Players["petya"], Assert.Single(used.GetProperty("data").GetProperty("targets").EnumerateArray()).GetGuid());
        Assert.Contains(
            feed.GetProperty("objects").EnumerateArray(),
            o => o.GetProperty("id").GetString() == "bird-thief" && o.GetProperty("name").GetString() == "Птичкерс");
    }

    [Fact]
    public async Task A_choice_is_answered_with_the_use()
    {
        var admin = await EconomyOnAsync();
        await GiveAsync(admin, "vasya", "dirty-trick");
        await _site.SendAsync(new Engine.Players.AdjustPlayer(_site.Players["petya"], "Очки", PointsDelta: 3));
        var vasya = await _site.SignedInAsync("vasya");
        var trick = await ItemAsync(vasya, "dirty-trick");
        var step = Assert.Single(trick.GetProperty("steps").EnumerateArray());
        Assert.Equal("choice", step.GetProperty("kind").GetString());
        var option = Assert.Single(step.GetProperty("options").EnumerateArray());
        Assert.Equal(("Horror", "Horror"), (option.GetProperty("id").GetString(), option.GetProperty("label").GetString()));

        await ApiCalls.PostOkAsync(
            vasya,
            Url($"items/{trick.GetProperty("instanceId").GetGuid()}/use"),
            new { commandId = Guid.NewGuid(), targetPlayerId = _site.Players["petya"], choices = new[] { "Horror" } });

        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Events.CountAsync(e => e.SeasonId == SiteFactory.SeasonId && e.Type == "item-used", Ct));
    }

    [Fact]
    public async Task A_target_the_item_cannot_reach_is_a_conflict()
    {
        var admin = await EconomyOnAsync();
        await GiveAsync(admin, "vasya", "bird-thief");
        var vasya = await _site.SignedInAsync("vasya");
        var thief = await ItemAsync(vasya, "bird-thief");

        var response = await vasya.PostAsJsonAsync(
            Url($"items/{thief.GetProperty("instanceId").GetGuid()}/use"), new { commandId = Guid.NewGuid(), targetPlayerId = _site.Players["vasya"] }, Ct);

        await AssertConflictAsync(response, "item.invalidTarget");
    }

    [Fact]
    public async Task Another_players_item_is_not_mine_to_use()
    {
        var admin = await EconomyOnAsync();
        await GiveAsync(admin, "vasya", "orange");
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        var orange = await ItemAsync(vasya, "orange");

        var response = await petya.PostAsJsonAsync(Url($"items/{orange.GetProperty("instanceId").GetGuid()}/use"), new { commandId = Guid.NewGuid() }, Ct);

        await AssertConflictAsync(response, "item.unknown");
        Assert.Equal(1, (await EconomyAsync(vasya)).GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("{\"targetPlayerId\":null}")]
    [InlineData("{\"commandId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"commandId\":\"{id}\",\"choices\":[\"\"]}")]
    [InlineData("{\"commandId\":\"{id}\",\"choices\":[null]}")]
    [InlineData("{\"commandId\":\"{id}\",\"choices\":[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\",\"7\",\"8\",\"9\",\"10\",\"11\"]}")]
    [InlineData("{\"commandId\":\"{id}\",\"choices\":[\"{long}\"]}")]
    [InlineData("{\"commandId\":\"{id}\",\"targetPlayerId\":\"not-a-guid\"}")]
    public async Task A_use_with_invalid_input_is_a_bad_request(string json)
    {
        await EconomyOnAsync();
        var vasya = await _site.SignedInAsync("vasya");
        var body = json.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal).Replace("{long}", new string('я', 101), StringComparison.Ordinal);

        var response = await vasya.PostAsync(Url($"items/{Guid.NewGuid()}/use"), new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- The shop ----

    [Fact]
    public async Task A_shop_roll_shows_the_lots_and_a_lot_bought_goes_into_the_inventory()
    {
        await EconomyOnAsync();
        await SetCoinsAsync("vasya", 100);
        var vasya = await _site.SignedInAsync("vasya");
        var before = (await EconomyAsync(vasya)).GetProperty("shop");
        Assert.Equal(0, before.GetProperty("lots").GetArrayLength());
        var price = before.GetProperty("price").GetInt32();

        await ApiCalls.PostOkAsync(vasya, Url("shop/roll"));

        var shop = (await EconomyAsync(vasya)).GetProperty("shop");
        Assert.Equal(3, shop.GetProperty("lots").GetArrayLength());
        Assert.Equal(_site.Clock.UtcNow.AddMinutes(15), shop.GetProperty("expiresAt").GetDateTimeOffset());
        var lot = shop.GetProperty("lots")[0];
        Assert.False(string.IsNullOrEmpty(lot.GetProperty("name").GetString()));
        await ApiCalls.PostOkAsync(vasya, Url("shop/buy"), new { commandId = Guid.NewGuid(), lot = 0 });

        Assert.Equal(100 - price - lot.GetProperty("price").GetInt32(), await CoinsAsync("vasya"));
        var economy = await EconomyAsync(vasya);
        Assert.Equal(lot.GetProperty("objectId").GetString(), Assert.Single(economy.GetProperty("items").EnumerateArray()).GetProperty("objectId").GetString());
        Assert.True(economy.GetProperty("shop").GetProperty("lots")[0].GetProperty("sold").GetBoolean());
    }

    [Fact]
    public async Task In_debt_the_shop_is_refused_and_says_so_before_the_press()
    {
        await EconomyOnAsync();
        await SetCoinsAsync("vasya", -3);
        var vasya = await _site.SignedInAsync("vasya");

        Assert.Equal("coins.inDebt", (await EconomyAsync(vasya)).GetProperty("shop").GetProperty("refusal").GetString());
        await AssertConflictAsync(await vasya.PostAsJsonAsync(Url("shop/roll"), new { commandId = Guid.NewGuid() }, Ct), "coins.inDebt");
    }

    [Fact]
    public async Task A_lot_past_its_time_is_gone_and_not_bought()
    {
        await EconomyOnAsync();
        await SetCoinsAsync("vasya", 100);
        var vasya = await _site.SignedInAsync("vasya");
        await ApiCalls.PostOkAsync(vasya, Url("shop/roll"));

        _site.Clock.Advance(TimeSpan.FromMinutes(15));

        Assert.Equal(0, (await EconomyAsync(vasya)).GetProperty("shop").GetProperty("lots").GetArrayLength());
        await AssertConflictAsync(await vasya.PostAsJsonAsync(Url("shop/buy"), new { commandId = Guid.NewGuid(), lot = 0 }, Ct), "shop.offerExpired");
    }

    [Theory]
    [InlineData("shop/buy", "{\"commandId\":\"{id}\",\"lot\":-1}")]
    [InlineData("shop/buy", "{\"commandId\":\"{id}\"}")]
    [InlineData("shop/roll", "{}")]
    [InlineData("bets", "{\"commandId\":\"{id}\",\"days\":3,\"stake\":5}")]
    [InlineData("bets", "{\"commandId\":\"{id}\",\"onPlayerId\":\"{petya}\",\"days\":0,\"stake\":5}")]
    [InlineData("bets", "{\"commandId\":\"{id}\",\"onPlayerId\":\"{petya}\",\"days\":3,\"stake\":0}")]
    [InlineData("bets", "{\"onPlayerId\":\"{petya}\",\"days\":3,\"stake\":5}")]
    public async Task Shop_and_bet_actions_with_invalid_input_are_bad_requests(string action, string json)
    {
        await EconomyOnAsync();
        var vasya = await _site.SignedInAsync("vasya");
        var body = json.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal)
            .Replace("{petya}", _site.Players["petya"].ToString(), StringComparison.Ordinal);

        var response = await vasya.PostAsync(Url(action), new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Bets ----

    [Fact]
    public async Task A_bet_on_a_playing_player_is_listed_with_its_stake()
    {
        await EconomyOnAsync();
        await SetCoinsAsync("vasya", 20);
        var petya = await _site.SignedInAsync("petya");
        await ApiCalls.PostOkAsync(petya, Url("roll"));
        await ApiCalls.PostOkAsync(petya, Url("start"));
        var vasya = await _site.SignedInAsync("vasya");

        await ApiCalls.PostOkAsync(vasya, Url("bets"), new { commandId = Guid.NewGuid(), onPlayerId = _site.Players["petya"], days = 3, stake = 5 });

        var bets = (await EconomyAsync(vasya)).GetProperty("bets");
        var bet = Assert.Single(bets.GetProperty("bets").EnumerateArray());
        Assert.Equal(("petya", 5, "open"), (bet.GetProperty("onPlayer").GetString(), bet.GetProperty("stake").GetInt32(), bet.GetProperty("status").GetString()));
        Assert.Equal([1, 3, 7], bets.GetProperty("deadlineOptionsDays").EnumerateArray().Select(d => d.GetInt32()));
        Assert.Equal(15, await CoinsAsync("vasya"));
    }

    [Fact]
    public async Task A_bet_on_myself_is_a_conflict()
    {
        await EconomyOnAsync();
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(Url("bets"), new { commandId = Guid.NewGuid(), onPlayerId = _site.Players["vasya"], days = 3, stake = 5 }, Ct);

        await AssertConflictAsync(response, "bet.onSelf");
    }

    // ---- Roles: 403 and 401 for every player endpoint ----

    public static TheoryData<string> PlayerActions =>
    [
        "items/{item}/use",
        "shop/roll",
        "shop/buy",
        "bets",
    ];

    private static object BodyFor(string action, Guid petya) => action switch
    {
        "shop/buy" => new { commandId = Guid.NewGuid(), lot = 0 },
        "bets" => new { commandId = Guid.NewGuid(), onPlayerId = petya, days = 3, stake = 5 },
        _ => new { commandId = Guid.NewGuid() },
    };

    [Theory]
    [MemberData(nameof(PlayerActions))]
    public async Task A_spectator_and_the_admin_cannot_act_as_a_player(string action)
    {
        await EconomyOnAsync();
        var url = Url(action.Replace("{item}", Guid.NewGuid().ToString(), StringComparison.Ordinal));
        foreach (var login in new[] { "zritel", "admin" })
        {
            var client = await _site.SignedInAsync(login);
            var response = await client.PostAsJsonAsync(url, BodyFor(action, _site.Players["petya"]), Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{login} {action}: {response.StatusCode}");
        }
    }

    [Theory]
    [MemberData(nameof(PlayerActions))]
    public async Task A_player_of_another_season_is_forbidden(string action)
    {
        await EconomyOnAsync();
        var masha = await _site.SignedInAsync("masha");

        var response = await masha.PostAsJsonAsync(
            Url(action.Replace("{item}", Guid.NewGuid().ToString(), StringComparison.Ordinal)), BodyFor(action, _site.Players["petya"]), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_inventory_is_only_for_the_seasons_players()
    {
        await EconomyOnAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await (await _site.SignedInAsync("zritel")).GetAsync(Url("economy"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await _site.SignedInAsync("masha")).GetAsync(Url("economy"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await _site.SignedInAsync("vasya")).GetAsync($"/api/seasons/{Guid.NewGuid()}/economy", Ct)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_is_unauthorized_everywhere()
    {
        var client = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Url("economy"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(AdminUrl("content"), Ct)).StatusCode);
        foreach (var url in new[]
        {
            Url($"items/{Guid.NewGuid()}/use"), Url("shop/roll"), Url("shop/buy"), Url("bets"), AdminUrl("content"),
            AdminUrl($"players/{Guid.NewGuid()}/inventory"),
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(url, new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
        }
    }

    // ---- The admin: content and inventories ----

    [Fact]
    public async Task The_admin_loads_the_pack_and_reads_it_back()
    {
        var admin = await EconomyOnAsync();

        var content = await ApiCalls.OkAsync(await admin.GetAsync(AdminUrl("content"), Ct));

        Assert.Equal(1, content.GetProperty("version").GetInt32());
        var objects = content.GetProperty("objects").EnumerateArray().ToList();
        Assert.Equal(Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "content", "items"), "*.json").Count(), objects.Count);
        Assert.Contains(objects, o => o.GetProperty("id").GetString() == "bird-thief" && o.GetProperty("name").GetString() == "Птичкерс");
        Assert.Equal("lootbox", Assert.Single(content.GetProperty("wheels").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_wrong_pack_is_answered_with_every_problem_at_its_path()
    {
        await FeaturesAsync();
        var admin = await _site.SignedInAsync("admin");
        var pack = RepositoryPack();
        pack["objects"]![0]!["name"] = "";
        pack["wheels"]![0]!["entries"]![0]!["objectId"] = "no-such-object";

        var response = await admin.PostAsJsonAsync(AdminUrl("content"), new { commandId = Guid.NewGuid(), pack, comment = "Пакет" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("$.objects[0].name", out _), errors.ToString());
        Assert.True(errors.TryGetProperty("$.wheels[0].entries[0].objectId", out _), errors.ToString());
    }

    [Theory]
    [InlineData("{\"commandId\":\"{id}\",\"pack\":[],\"comment\":\"x\"}")]
    [InlineData("{\"commandId\":\"{id}\",\"pack\":{\"objects\":[{\"id\":\"x\"}]},\"comment\":\"x\"}")]
    [InlineData("{\"commandId\":\"{id}\",\"pack\":{\"items\":[]},\"comment\":\"x\"}")]
    [InlineData("{\"commandId\":\"{id}\",\"pack\":{}}")]
    [InlineData("{\"pack\":{},\"comment\":\"x\"}")]
    public async Task A_pack_that_does_not_parse_is_a_bad_request(string json)
    {
        await FeaturesAsync();
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsync(
            AdminUrl("content"), new StringContent(json.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal), System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task With_items_off_a_pack_is_refused_by_the_engine()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(AdminUrl("content"), new { commandId = Guid.NewGuid(), pack = RepositoryPack(), comment = "Пакет" }, Ct);

        await AssertConflictAsync(response, "feature.disabled");
    }

    [Fact]
    public async Task The_admin_gives_and_takes_an_object_with_a_comment()
    {
        var admin = await EconomyOnAsync();
        await GiveAsync(admin, "vasya", "orange");
        var vasya = await _site.SignedInAsync("vasya");
        var orange = await ItemAsync(vasya, "orange");

        await ApiCalls.PostOkAsync(
            admin,
            AdminUrl($"players/{_site.Players["vasya"]}/inventory"),
            new { commandId = Guid.NewGuid(), removeInstanceId = orange.GetProperty("instanceId").GetGuid(), comment = "Ошибка" });

        Assert.Equal(0, (await EconomyAsync(vasya)).GetProperty("items").GetArrayLength());
        await AssertConflictAsync(
            await admin.PostAsJsonAsync(
                AdminUrl($"players/{_site.Players["vasya"]}/inventory"), new { commandId = Guid.NewGuid(), giveObjectId = "no-such", comment = "x" }, Ct),
            "content.unknownObject");
    }

    [Theory]
    [InlineData("{\"commandId\":\"{id}\",\"giveObjectId\":\"orange\"}")]
    [InlineData("{\"commandId\":\"{id}\",\"giveObjectId\":\"\",\"comment\":\"x\"}")]
    [InlineData("{\"commandId\":\"{id}\",\"removeInstanceId\":\"00000000-0000-0000-0000-000000000000\",\"comment\":\"x\"}")]
    [InlineData("{\"giveObjectId\":\"orange\",\"comment\":\"x\"}")]
    public async Task An_inventory_correction_with_invalid_input_is_a_bad_request(string json)
    {
        var admin = await EconomyOnAsync();

        var response = await admin.PostAsync(
            AdminUrl($"players/{_site.Players["vasya"]}/inventory"),
            new StringContent(json.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal), System.Text.Encoding.UTF8, "application/json"),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Players_and_spectators_cannot_use_the_admin_endpoints(string login)
    {
        await FeaturesAsync();
        var client = await _site.SignedInAsync(login);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(AdminUrl("content"), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync(AdminUrl("content"), new { commandId = Guid.NewGuid(), pack = RepositoryPack(), comment = "Моё" }, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync(
                AdminUrl($"players/{_site.Players["vasya"]}/inventory"), new { commandId = Guid.NewGuid(), giveObjectId = "orange", comment = "Себе" }, Ct)).StatusCode);
    }
}

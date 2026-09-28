using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The map API (2.9): the season view gives the map in force — cells with their types, parameters, zones and places,
/// the arrows and the zones' rules — the player's pending branch choice with the steps left and the legs of the latest
/// move; the editor reads the map, checks a draft (every problem at once, readable) and publishes it through the queue.
/// </summary>
public sealed class MapApiTests : IAsyncLifetime
{
    // Every die shows 1 and the graph season throws three (SwitchToGraphAsync): a completion walks three steps
    private readonly SiteFactory _site = new() { Random = new AlwaysFirst() };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Season => $"/api/seasons/{SiteFactory.SeasonId}";

    private static string Admin => $"/api/admin/seasons/{SiteFactory.SeasonId}/map";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- The season view ----

    [Fact]
    public async Task A_linear_season_gives_its_chain_with_arrows_and_no_zones()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var season = await ApiCalls.OkAsync(await vasya.GetAsync(Season, Ct));

        Assert.Equal("linear", season.GetProperty("mapMode").GetString());
        var cells = season.GetProperty("cells").EnumerateArray().ToList();
        var edges = season.GetProperty("edges").EnumerateArray().ToList();
        Assert.Equal(cells.Count - 1, edges.Count);
        Assert.Equal(("start", "c1", true, true), Edge(edges[0]));
        Assert.Empty(season.GetProperty("zones").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, season.GetProperty("me").GetProperty("lastMove").ValueKind);
    }

    [Fact]
    public async Task The_season_view_gives_the_published_graph_with_places_parameters_and_zones()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchToGraphAsync(admin);

        await PublishAsync(admin, ForkMap());

        var season = await ApiCalls.OkAsync(await vasya.GetAsync(Season, Ct));
        Assert.Equal("graph", season.GetProperty("mapMode").GetString());
        var cells = season.GetProperty("cells").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!);
        Assert.Equal(["start", "f", "a1", "a2", "b1", "b2", "t", "finish"], cells.Keys);
        Assert.Equal("fork", cells["f"].GetProperty("type").GetString());
        Assert.Equal(120m, cells["f"].GetProperty("x").GetDecimal());
        Assert.Equal(40m, cells["f"].GetProperty("y").GetDecimal());
        Assert.Equal("swamp", cells["a1"].GetProperty("zone").GetString());
        Assert.Equal(3, cells["b1"].GetProperty("amount").GetInt32());
        Assert.Equal("a2", cells["t"].GetProperty("to").GetString());
        var edges = season.GetProperty("edges").EnumerateArray().Select(Edge).ToList();
        Assert.Contains(("f", "a1", true, true), edges);
        Assert.Contains(("f", "b1", false, true), edges);
        Assert.Contains(("b2", "finish", true, false), edges);
        Assert.Contains(("t", "b2", true, true), edges);
        var zone = Assert.Single(season.GetProperty("zones").EnumerateArray());
        Assert.Equal("Болото", zone.GetProperty("name").GetString());
        Assert.Equal(["Horror"], zone.GetProperty("rollFilter").GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("add", zone.GetProperty("diceModifier").GetProperty("stage").GetString());
        Assert.Equal(1, zone.GetProperty("diceModifier").GetProperty("value").GetInt32());
        Assert.Equal(1.5m, zone.GetProperty("dropPenaltyMultiplier").GetDecimal());
        // The leaderboard counts the way on the map in force: start → f → a1 → a2 → finish
        var row = season.GetProperty("leaderboard").EnumerateArray().First(r => r.GetProperty("playerId").GetGuid() == _site.Players["vasya"]);
        Assert.Equal(4, row.GetProperty("cellsToFinish").GetInt32());
    }

    [Fact]
    public async Task A_move_that_reaches_a_fork_waits_for_the_branch_with_its_steps()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchToGraphAsync(admin);
        await PublishAsync(admin, ForkMap());

        await CompleteOneAsync(vasya);

        var me = await MeAsync(vasya);
        var choice = me.GetProperty("choice");
        Assert.Equal("branch", choice.GetProperty("kind").GetString());
        // The default branch first; a branch option carries no game
        Assert.Equal(["a1", "b1"], choice.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("id").GetString()));
        Assert.All(choice.GetProperty("options").EnumerateArray(), o => Assert.Equal(JsonValueKind.Null, o.GetProperty("game").ValueKind));
        // Three dice of 1: one step to the fork, two left
        Assert.Equal(2, choice.GetProperty("steps").GetInt32());
        var leg = Assert.Single(me.GetProperty("lastMove").GetProperty("legs").EnumerateArray());
        Assert.Equal("start", leg.GetProperty("from").GetString());
        Assert.Equal(["f"], leg.GetProperty("path").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("completionRoll", leg.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_chosen_branch_is_walked_and_the_move_gives_its_legs()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchToGraphAsync(admin);
        await PublishAsync(admin, ForkMap());
        await CompleteOneAsync(vasya);
        var before = await MeAsync(vasya);
        var choice = before.GetProperty("choice");

        await ApiCalls.PostOkAsync(vasya, $"{Season}/choose", new { commandId = Guid.NewGuid(), choiceId = choice.GetProperty("id").GetGuid(), optionId = "b1" });

        // Two steps were left: b1, then the teleport t, whose stop transfers the token to a2
        var me = await MeAsync(vasya);
        Assert.Equal(JsonValueKind.Null, me.GetProperty("choice").ValueKind);
        var move = me.GetProperty("lastMove");
        Assert.True(move.GetProperty("sequence").GetInt64() > before.GetProperty("lastMove").GetProperty("sequence").GetInt64());
        Assert.Equal(
            [("f", "b1,t", "completionRoll"), ("t", "a2", "teleport")],
            move.GetProperty("legs").EnumerateArray().Select(l => (
                l.GetProperty("from").GetString(),
                string.Join(",", l.GetProperty("path").EnumerateArray().Select(c => c.GetString())),
                l.GetProperty("reason").GetString())));
        var season = await ApiCalls.OkAsync(await vasya.GetAsync(Season, Ct));
        Assert.Equal("a2", season.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == _site.Players["vasya"]).GetProperty("cellId").GetString());
    }

    [Fact]
    public async Task Another_players_move_is_not_my_last_move()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        await SwitchToGraphAsync(admin);
        await PublishAsync(admin, ForkMap());

        await CompleteOneAsync(vasya);

        Assert.Equal(JsonValueKind.Null, (await MeAsync(petya)).GetProperty("lastMove").ValueKind);
    }

    [Fact]
    public async Task An_undone_move_is_not_my_last_move()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchToGraphAsync(admin);
        await PublishAsync(admin, ForkMap());
        await CompleteOneAsync(vasya);
        var before = await MeAsync(vasya);
        var choose = Guid.NewGuid();
        await ApiCalls.PostOkAsync(vasya, $"{Season}/choose", new { commandId = choose, choiceId = before.GetProperty("choice").GetProperty("id").GetGuid(), optionId = "b1" });

        await ApiCalls.PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/undo", new { commandId = Guid.NewGuid(), targetCommandId = choose, comment = "Не та ветка" });

        // The move of the completion is the latest again, and the choice waits once more
        var me = await MeAsync(vasya);
        Assert.Equal(before.GetProperty("lastMove").GetProperty("sequence").GetInt64(), me.GetProperty("lastMove").GetProperty("sequence").GetInt64());
        Assert.Equal("branch", me.GetProperty("choice").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task The_players_section_says_who_is_choosing_a_branch()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchToGraphAsync(admin);
        await PublishAsync(admin, ForkMap());
        await CompleteOneAsync(vasya);

        var players = await ApiCalls.OkAsync(await admin.GetAsync($"/api/admin/seasons/{SiteFactory.SeasonId}/players", Ct));

        Assert.Equal(
            [("petya", false), ("vasya", true)],
            players.EnumerateArray().Select(p => (p.GetProperty("name").GetString(), p.GetProperty("choosingBranch").GetBoolean())));
    }

    // ---- The editor: read ----

    [Fact]
    public async Task The_admin_reads_the_map_the_mode_and_who_stands_where()
    {
        var admin = await _site.SignedInAsync("admin");

        var view = await ApiCalls.OkAsync(await admin.GetAsync(Admin, Ct));

        Assert.Equal("linear", view.GetProperty("mode").GetString());
        Assert.Equal("active", view.GetProperty("status").GetString());
        Assert.Equal("start", view.GetProperty("map").GetProperty("cells")[0].GetProperty("id").GetString());
        var players = view.GetProperty("players").EnumerateArray().ToList();
        Assert.Equal(["petya", "vasya"], players.Select(p => p.GetProperty("name").GetString()));
        Assert.All(players, p => Assert.Equal("start", p.GetProperty("cellId").GetString()));
        Assert.All(players, p => Assert.False(p.GetProperty("choosingBranch").GetBoolean()));
    }

    [Fact]
    public async Task The_editor_shows_who_is_choosing_a_branch()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchToGraphAsync(admin);
        await PublishAsync(admin, ForkMap());
        await CompleteOneAsync(vasya);

        var view = await ApiCalls.OkAsync(await admin.GetAsync(Admin, Ct));

        var player = view.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "vasya");
        Assert.Equal("f", player.GetProperty("cellId").GetString());
        Assert.True(player.GetProperty("choosingBranch").GetBoolean());
        Assert.Equal("graph", view.GetProperty("mode").GetString());
        Assert.Equal(8, view.GetProperty("map").GetProperty("cells").GetArrayLength());
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Only_the_admin_reads_checks_and_publishes_the_map(string login)
    {
        var other = await _site.SignedInAsync(login);

        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(Admin, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"{Admin}/check", ForkMap(), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await other.PostAsJsonAsync($"{Admin}/publish", new { commandId = Guid.NewGuid(), map = ForkMap(), comment = "Новая карта" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_signed_out_visitor_gets_401()
    {
        var anonymous = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Admin, Ct)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_season_is_404()
    {
        var admin = await _site.SignedInAsync("admin");
        var unknown = $"/api/admin/seasons/{Guid.NewGuid()}/map";

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(unknown, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"{unknown}/check", ForkMap(), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync($"{unknown}/publish", new { commandId = Guid.NewGuid(), map = ForkMap(), comment = "Новая карта" }, Ct)).StatusCode);
    }

    // ---- The editor: check ----

    [Fact]
    public async Task A_good_map_passes_the_check()
    {
        var admin = await _site.SignedInAsync("admin");
        await SwitchToGraphAsync(admin);

        var check = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{Admin}/check", ForkMap(), Ct));

        Assert.True(check.GetProperty("canPublish").GetBoolean());
        Assert.Empty(check.GetProperty("problems").EnumerateArray());
    }

    [Fact]
    public async Task The_check_lists_every_problem_at_once_with_its_subject()
    {
        var admin = await _site.SignedInAsync("admin");
        await SwitchToGraphAsync(admin);
        var map = ForkMap();
        // The finish removed (with its arrows) and a fork with no default branch
        map["cells"] = new JsonArray([.. map["cells"]!.AsArray().Where(c => c!["id"]!.GetValue<string>() != "finish").Select(c => c!.DeepClone())]);
        map["edges"] = new JsonArray([.. map["edges"]!.AsArray().Where(e => e!["to"]!.GetValue<string>() != "finish").Select(e => e!.DeepClone())]);
        foreach (var edge in map["edges"]!.AsArray().Where(e => e!["from"]!.GetValue<string>() == "f"))
        {
            edge!["isDefaultForward"] = false;
        }

        var check = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{Admin}/check", map, Ct));

        Assert.False(check.GetProperty("canPublish").GetBoolean());
        var problems = check.GetProperty("problems").EnumerateArray()
            .Select(p => (p.GetProperty("code").GetString(), p.GetProperty("subject").GetString())).ToList();
        Assert.Contains(("map.noFinish", "map"), problems);
        Assert.Contains(("map.defaultBranch", "f"), problems);
        Assert.Contains(("map.deadEnd", "a2"), problems);
        Assert.All(check.GetProperty("problems").EnumerateArray(), p => Assert.False(string.IsNullOrEmpty(p.GetProperty("message").GetString())));
    }

    [Fact]
    public async Task The_check_names_a_removed_cell_where_a_player_stands()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchToGraphAsync(admin);
        await PublishAsync(admin, ForkMap());
        await CompleteOneAsync(vasya);
        var choice = (await MeAsync(vasya)).GetProperty("choice");
        await ApiCalls.PostOkAsync(vasya, $"{Season}/choose", new { commandId = Guid.NewGuid(), choiceId = choice.GetProperty("id").GetGuid(), optionId = "b1" });

        var check = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{Admin}/check", LineMap(), Ct));

        Assert.False(check.GetProperty("canPublish").GetBoolean());
        var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
        Assert.Equal(("map.occupiedCellRemoved", "a2"), (problem.GetProperty("code").GetString(), problem.GetProperty("subject").GetString()));
    }

    [Fact]
    public async Task The_check_says_a_player_is_choosing_a_branch()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchToGraphAsync(admin);
        await PublishAsync(admin, ForkMap());
        await CompleteOneAsync(vasya);
        var map = ForkMap();
        map["cells"]![1]!["x"] = 130;

        var check = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{Admin}/check", map, Ct));

        Assert.Contains(
            ("map.branchChoicePending", "map"),
            check.GetProperty("problems").EnumerateArray().Select(p => (p.GetProperty("code").GetString(), p.GetProperty("subject").GetString())));
    }

    [Fact]
    public async Task The_check_warns_about_a_zone_with_few_games_without_blocking()
    {
        var admin = await _site.SignedInAsync("admin");
        await SwitchToGraphAsync(admin);

        var check = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{Admin}/check", ForkMap(), Ct));

        // Three games in the pool, the rules want 15
        var warning = Assert.Single(check.GetProperty("warnings").EnumerateArray());
        Assert.Equal(("swamp", 3, 15), (warning.GetProperty("zoneId").GetString(), warning.GetProperty("available").GetInt32(), warning.GetProperty("wanted").GetInt32()));
        Assert.True(check.GetProperty("canPublish").GetBoolean());
    }

    [Fact]
    public async Task In_the_linear_mode_a_good_map_cannot_be_published_yet()
    {
        var admin = await _site.SignedInAsync("admin");

        var check = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{Admin}/check", ForkMap(), Ct));

        Assert.Empty(check.GetProperty("problems").EnumerateArray());
        Assert.False(check.GetProperty("canPublish").GetBoolean());
    }

    [Fact]
    public async Task The_map_in_force_cannot_be_published_again()
    {
        var admin = await _site.SignedInAsync("admin");
        await SwitchToGraphAsync(admin);
        await PublishAsync(admin, ForkMap());

        var check = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{Admin}/check", ForkMap(), Ct));

        Assert.False(check.GetProperty("canPublish").GetBoolean());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"cells":null,"edges":[]}""")]
    [InlineData("""{"cells":[null],"edges":[]}""")]
    [InlineData("""{"cells":[{"id":"start","type":"volcano"}],"edges":[]}""")]
    [InlineData("""{"cells":[],"edges":[],"zones":[{"id":"z","name":"Z","rollFilter":{"tags":[null]}}]}""")]
    public async Task A_malformed_map_is_400(string body)
    {
        var admin = await _site.SignedInAsync("admin");
        await SwitchToGraphAsync(admin);

        var response = await admin.PostAsync($"{Admin}/check", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_big_map_fits_the_request_limit()
    {
        var admin = await _site.SignedInAsync("admin");
        await SwitchToGraphAsync(admin);
        var map = LongMap(600);
        Assert.True(map.ToJsonString().Length > Hosting.WebSecurity.ApiBodyLimitBytes);

        var check = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{Admin}/check", map, Ct));

        Assert.True(check.GetProperty("canPublish").GetBoolean(), check.ToString());
    }

    [Fact]
    public async Task A_map_past_the_limit_is_413_and_other_admin_bodies_keep_the_small_limit()
    {
        var admin = await _site.SignedInAsync("admin");
        await SwitchToGraphAsync(admin);
        var huge = new string('x', (int)Map.AdminMapEndpoints.MaxMapRequestBytes);
        var small = new string('x', (int)Hosting.WebSecurity.ApiBodyLimitBytes + 1);

        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            (await admin.PostAsync($"{Admin}/check", new StringContent($"{{\"cells\":[],\"edges\":[],\"pad\":\"{huge}\"}}", System.Text.Encoding.UTF8, "application/json"), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            (await admin.PostAsJsonAsync($"/api/admin/seasons/{SiteFactory.SeasonId}/undo", new { commandId = Guid.NewGuid(), targetCommandId = Guid.NewGuid(), comment = small }, Ct)).StatusCode);
    }

    // ---- The editor: publish ----

    [Fact]
    public async Task A_published_map_is_what_the_players_see_and_the_log_says_why()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchToGraphAsync(admin);

        var response = await admin.PostAsJsonAsync($"{Admin}/publish", new { commandId = Guid.NewGuid(), map = ForkMap(), comment = "Добавили болото" }, Ct);

        var body = await ApiCalls.OkAsync(response);
        Assert.Equal("map-published", body.GetProperty("events")[0].GetProperty("type").GetString());
        var season = await ApiCalls.OkAsync(await vasya.GetAsync(Season, Ct));
        Assert.Equal(8, season.GetProperty("cells").GetArrayLength());
        var read = await ApiCalls.OkAsync(await admin.GetAsync(Admin, Ct));
        Assert.Equal(8, read.GetProperty("map").GetProperty("cells").GetArrayLength());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task A_publication_without_a_comment_is_400(string? comment)
    {
        var admin = await _site.SignedInAsync("admin");
        await SwitchToGraphAsync(admin);

        var response = await admin.PostAsJsonAsync($"{Admin}/publish", new { commandId = Guid.NewGuid(), map = ForkMap(), comment }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_publication_with_a_too_long_comment_or_without_a_map_or_command_is_400()
    {
        var admin = await _site.SignedInAsync("admin");
        await SwitchToGraphAsync(admin);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"{Admin}/publish", new { commandId = Guid.NewGuid(), map = ForkMap(), comment = new string('я', 501) }, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"{Admin}/publish", new { commandId = Guid.NewGuid(), comment = "Новая карта" }, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"{Admin}/publish", new { commandId = Guid.Empty, map = ForkMap(), comment = "Новая карта" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task An_invalid_map_is_refused_with_its_code()
    {
        var admin = await _site.SignedInAsync("admin");
        await SwitchToGraphAsync(admin);
        var map = ForkMap();
        map["edges"] = new JsonArray([.. map["edges"]!.AsArray().Where(e => e!["from"]!.GetValue<string>() != "start").Select(e => e!.DeepClone())]);

        var response = await admin.PostAsJsonAsync($"{Admin}/publish", new { commandId = Guid.NewGuid(), map, comment = "Новая карта" }, Ct);

        await AssertRejectedAsync(response, "map.invalid");
    }

    [Fact]
    public async Task In_the_linear_mode_a_publication_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync($"{Admin}/publish", new { commandId = Guid.NewGuid(), map = ForkMap(), comment = "Новая карта" }, Ct);

        await AssertRejectedAsync(response, "feature.disabled");
    }

    [Fact]
    public async Task A_publication_that_removes_a_players_cell_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchToGraphAsync(admin);
        await PublishAsync(admin, ForkMap());
        await CompleteOneAsync(vasya);
        var choice = (await MeAsync(vasya)).GetProperty("choice");
        await ApiCalls.PostOkAsync(vasya, $"{Season}/choose", new { commandId = Guid.NewGuid(), choiceId = choice.GetProperty("id").GetGuid(), optionId = "b1" });

        var response = await admin.PostAsJsonAsync($"{Admin}/publish", new { commandId = Guid.NewGuid(), map = LineMap(), comment = "Короче" }, Ct);

        await AssertRejectedAsync(response, "map.occupiedCellRemoved");
    }

    // ---- Helpers ----

    private static (string?, string?, bool, bool) Edge(JsonElement e) =>
        (e.GetProperty("from").GetString(), e.GetProperty("to").GetString(), e.GetProperty("isDefaultForward").GetBoolean(), e.GetProperty("isPrimaryBackward").GetBoolean());

    /// <summary>
    /// start → f (a fork) → a1 → a2 → finish (the default, through the swamp) or f → b1 (+3 points) → t (a teleport to
    /// a2) → b2 → finish.
    /// </summary>
    private static JsonObject ForkMap() => new()
    {
        ["cells"] = new JsonArray(
            Cell("start", "start", 20, 40),
            Cell("f", "fork", 120, 40),
            Cell("a1", "empty", 220, 20, zone: "swamp"),
            Cell("a2", "empty", 320, 20, zone: "swamp"),
            Cell("b1", "pointsBonus", 220, 80, amount: 3),
            Cell("b2", "empty", 420, 80),
            Cell("t", "teleport", 320, 80, to: "a2"),
            Cell("finish", "finish", 520, 40)),
        ["edges"] = new JsonArray(
            Arrow("start", "f", true, true),
            Arrow("f", "a1", true, true),
            Arrow("f", "b1", false, true),
            Arrow("a1", "a2", true, true),
            Arrow("b1", "t", true, true),
            Arrow("t", "b2", true, true),
            Arrow("a2", "finish", true, true),
            Arrow("b2", "finish", true, false)),
        ["zones"] = new JsonArray(new JsonObject
        {
            ["id"] = "swamp",
            ["name"] = "Болото",
            ["rollFilter"] = new JsonObject { ["tags"] = new JsonArray("Horror") },
            ["diceModifier"] = new JsonObject { ["stage"] = "add", ["value"] = 1 },
            ["dropPenaltyMultiplier"] = 1.5,
        }),
    };

    /// <summary>A plain line start → finish with none of the fork's branches.</summary>
    private static JsonObject LineMap() => LongMap(3);

    private static JsonObject LongMap(int length)
    {
        var cells = new JsonArray(Cell("start", "start", 0, 0));
        var edges = new JsonArray();
        var previous = "start";
        for (var i = 1; i < length; i++)
        {
            var id = $"long-cell-number-{i}";
            cells.Add(Cell(id, "empty", i * 100, (i % 7) * 50));
            edges.Add(Arrow(previous, id, true, true));
            previous = id;
        }

        cells.Add(Cell("finish", "finish", length * 100, 0));
        edges.Add(Arrow(previous, "finish", true, true));
        return new JsonObject { ["cells"] = cells, ["edges"] = edges, ["zones"] = new JsonArray() };
    }

    private static JsonObject Cell(string id, string type, int x, int y, string? zone = null, string? to = null, int? amount = null)
    {
        var cell = new JsonObject { ["id"] = id, ["type"] = type, ["x"] = x, ["y"] = y };
        if (zone is not null)
        {
            cell["zone"] = zone;
        }

        if (to is not null)
        {
            cell["to"] = to;
        }

        if (amount is not null)
        {
            cell["amount"] = amount;
        }

        return cell;
    }

    private static JsonObject Arrow(string from, string to, bool isDefault, bool isPrimary) =>
        new() { ["from"] = from, ["to"] = to, ["isDefaultForward"] = isDefault, ["isPrimaryBackward"] = isPrimary };

    private static async Task SwitchToGraphAsync(HttpClient admin)
    {
        using var current = JsonDocument.Parse(await admin.GetStringAsync($"{Season}/rules", Ct));
        var ruleset = JsonNode.Parse(current.RootElement.GetProperty("ruleset").GetRawText())!;
        ruleset["features"]!["mapMode"] = "graph";
        // Three dice whatever the hours: every completion walks three steps
        ruleset["reward"]!["diceCount"]!["min"] = 3;
        ruleset["reward"]!["diceCount"]!["max"] = 3;
        var response = await admin.PutAsJsonAsync(
            $"/api/admin/seasons/{SiteFactory.SeasonId}/rules",
            new { commandId = Guid.NewGuid(), expectedVersion = current.RootElement.GetProperty("version").GetInt32(), ruleset },
            Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static Task PublishAsync(HttpClient admin, JsonObject map) =>
        ApiCalls.PostOkAsync(admin, $"{Admin}/publish", new { commandId = Guid.NewGuid(), map, comment = "Новая карта" });

    /// <summary>Rolls, starts and completes one game: three steps.</summary>
    private static async Task CompleteOneAsync(HttpClient player)
    {
        await ApiCalls.PostOkAsync(player, $"{Season}/roll");
        await ApiCalls.PostOkAsync(player, $"{Season}/start");
        await ApiCalls.PostOkAsync(player, $"{Season}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });
    }

    private static async Task<JsonElement> MeAsync(HttpClient player) =>
        (await ApiCalls.OkAsync(await player.GetAsync(Season, Ct))).GetProperty("me");

    private static async Task AssertRejectedAsync(HttpResponseMessage response, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private sealed class AlwaysFirst : GameEvent.Engine.Kernel.IRandomSource
    {
        public int NextInt(int minInclusive, int maxExclusive) => minInclusive;
    }
}

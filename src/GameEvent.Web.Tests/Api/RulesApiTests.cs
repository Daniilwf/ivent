using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Rulesets;
using GameEvent.Web.Rulesets;

namespace GameEvent.Web.Tests.Api;

/// <summary>C2 and C3 over HTTP: the admin changes the rules, everyone sees the rules and their history.</summary>
public sealed class RulesApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Rules => $"/api/seasons/{SiteFactory.SeasonId}/rules";

    private static string AdminRules => $"/api/admin/seasons/{SiteFactory.SeasonId}/rules";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    private static JsonObject DefaultWith(Action<JsonObject> change)
    {
        var node = JsonNode.Parse(RulesetJson.DefaultJson())!.AsObject();
        change(node);
        return node;
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string rulesetJson, int expectedVersion = 1, Guid? commandId = null) =>
        PutRawAsync(client, $$"""{"commandId":"{{commandId ?? Guid.NewGuid()}}","expectedVersion":{{expectedVersion}},"ruleset":{{rulesetJson}}}""");

    private static Task<HttpResponseMessage> PutRawAsync(HttpClient client, string body) =>
        client.PutAsync(AdminRules, new StringContent(body, Encoding.UTF8, "application/json"), Ct);

    [Fact]
    public async Task Everyone_signed_in_sees_the_rules_in_force()
    {
        var player = await _site.SignedInAsync("zritel");

        var rules = await player.GetFromJsonAsync<RulesView>(Rules, s_json, Ct);

        Assert.Equal(1, rules!.Version);
        Assert.Equal(10, rules.Ruleset.Reward.DiceCount.Max);
        var created = Assert.Single(rules.History);
        Assert.Equal(1, created.Version);
        Assert.Empty(created.Changes);
    }

    [Fact]
    public async Task Admin_changes_the_rules_and_the_history_shows_what_changed_and_who_did_it()
    {
        var admin = await _site.SignedInAsync("admin");
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(3);

        var response = await PutAsync(admin, DefaultWith(r => r["reward"]!["diceCount"]!["max"] = 12).ToJsonString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<RulesChangeResult>(s_json, Ct))!.Version);

        // The rules version is also projected into the Ruleset table with its author
        await using var db = _site.NewDb();
        var versions = db.Rulesets.Where(r => r.SeasonId == SiteFactory.SeasonId).OrderBy(r => r.Version).ToList();
        Assert.Equal([1, 2], versions.Select(v => v.Version));
        Assert.Null(versions[0].AuthorId);
        Assert.Equal(_site.Users["admin"], versions[1].AuthorId);
        Assert.Equal(2, db.Seasons.Single(s => s.Id == SiteFactory.SeasonId).RulesetVersion);

        var rules = await (await _site.SignedInAsync("vasya")).GetFromJsonAsync<RulesView>(Rules, s_json, Ct);
        Assert.Equal(2, rules!.Version);
        var latest = rules.History[0];
        Assert.Equal(2, latest.Version);
        Assert.Equal(_site.Users["admin"], latest.AuthorId);
        Assert.Equal(_site.Clock.UtcNow, latest.At);
        Assert.Equal([new RulesetChange("reward.diceCount.max", "10", "12")], latest.Changes);
    }

    [Fact]
    public async Task History_names_the_author_and_the_creation_has_none()
    {
        // H7 (D-170): the rules page says who changed the rules, by name
        var admin = await _site.SignedInAsync("admin");
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, DefaultWith(r => r["reward"]!["diceCount"]!["max"] = 12).ToJsonString())).StatusCode);

        var rules = await (await _site.SignedInAsync("vasya")).GetFromJsonAsync<RulesView>(Rules, s_json, Ct);

        Assert.Equal(["admin", null], rules!.History.Select(h => h.AuthorName));
    }

    [Fact]
    public async Task Rules_carry_the_season_deadline_once_it_is_set()
    {
        // H7 (D-170): the deadline is the season's, not the ruleset's; the rules page shows it among the numbers
        var client = await _site.SignedInAsync("vasya");
        Assert.Null((await client.GetFromJsonAsync<RulesView>(Rules, s_json, Ct))!.Deadline);

        var deadline = _site.Clock.UtcNow.AddDays(30);
        await _site.SendAsync(new Engine.Seasons.SetSeasonDeadline(deadline));

        Assert.Equal(deadline, (await client.GetFromJsonAsync<RulesView>(Rules, s_json, Ct))!.Deadline);
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Only_the_admin_changes_the_rules(string login)
    {
        var client = await _site.SignedInAsync(login);

        var response = await PutAsync(client, DefaultWith(r => r["reward"]!["diceCount"]!["max"] = 12).ToJsonString());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, (await client.GetFromJsonAsync<RulesView>(Rules, s_json, Ct))!.Version);
    }

    [Fact]
    public async Task Anonymous_cannot_read_or_change_the_rules()
    {
        var client = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Rules, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutAsync(client, RulesetJson.DefaultJson())).StatusCode);
    }

    // ---- The schema for the admin's editor (H8) ----

    private const string Schema = "/api/admin/rules/schema";

    [Fact]
    public async Task Admin_gets_the_ruleset_schema_as_committed_in_the_docs()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.GetAsync(Schema, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/schema+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(RulesetSchema.Generate(), await response.Content.ReadAsStringAsync(Ct));
        using var schema = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("season", out _));
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Only_the_admin_gets_the_schema(string login)
    {
        var client = await _site.SignedInAsync(login);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Schema, Ct)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_does_not_get_the_schema()
    {
        var client = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Schema, Ct)).StatusCode);
    }

    [Fact]
    public async Task Schema_takes_no_input_and_ignores_a_stray_query()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.GetAsync($"{Schema}?season=nonsense", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RulesetSchema.Generate(), await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Typo_in_a_field_is_refused_naming_the_field()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await PutAsync(admin, DefaultWith(r => r["reward"]!["diceCount"]!["maxx"] = 12).ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<RulesetProblem>(s_json, Ct);
        var error = Assert.Single(problem!.Errors);
        Assert.Equal("reward.diceCount.maxx", error.Path);
    }

    [Fact]
    public async Task Semantic_errors_are_all_reported_with_their_paths()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await PutAsync(admin, DefaultWith(r =>
        {
            r["reward"]!["diceCount"]!["min"] = 5;
            r["reward"]!["diceCount"]!["max"] = 4;
            r["features"]!["shop"] = true;
        }).ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<RulesetProblem>(s_json, Ct);
        Assert.Contains(problem!.Errors, e => e.Path == "reward.diceCount.min");
        Assert.Contains(problem.Errors, e => e.Path == "features.shop");
        Assert.Equal(1, (await admin.GetFromJsonAsync<RulesView>(Rules, s_json, Ct))!.Version);
    }

    [Fact]
    public async Task Same_rules_again_are_a_conflict_not_a_new_version()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await PutAsync(admin, RulesetJson.DefaultJson());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("ruleset.unchanged", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_season_is_not_found()
    {
        var admin = await _site.SignedInAsync("admin");

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/seasons/{Guid.NewGuid()}/rules", Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.PutAsync(
                $"/api/admin/seasons/{Guid.NewGuid()}/rules",
                new StringContent($$"""{"commandId":"{{Guid.NewGuid()}}","expectedVersion":1,"ruleset":{{RulesetJson.DefaultJson()}}}""", Encoding.UTF8, "application/json"),
                Ct)).StatusCode);
    }

    [Fact]
    public async Task Save_from_a_stale_editor_is_a_conflict()
    {
        var admin = await _site.SignedInAsync("admin");
        (await PutAsync(admin, DefaultWith(r => r["reward"]!["diceCount"]!["max"] = 12).ToJsonString())).EnsureSuccessStatusCode();

        var stale = await PutAsync(admin, DefaultWith(r => r["reward"]!["diceCount"]!["max"] = 8).ToJsonString(), expectedVersion: 1);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("ruleset.versionConflict", await stale.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Double_submit_with_one_command_id_changes_once()
    {
        var admin = await _site.SignedInAsync("admin");
        var id = Guid.NewGuid();
        var body = DefaultWith(r => r["reward"]!["diceCount"]!["max"] = 12).ToJsonString();

        var first = await PutAsync(admin, body, commandId: id);
        var second = await PutAsync(admin, body, commandId: id);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, (await second.Content.ReadFromJsonAsync<RulesChangeResult>(s_json, Ct))!.Version);
    }

    [Fact]
    public async Task Unknown_time_zone_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await PutAsync(admin, DefaultWith(r => r["season"]!["timezone"] = "Europe/Mosow").ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<RulesetProblem>(s_json, Ct);
        Assert.Contains(problem!.Errors, e => e.Path == "season.timezone");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000001","expectedVersion":1}""")]
    [InlineData("""{"commandId":"5b1e2f0a-0000-0000-0000-000000000001","expectedVersion":1,"ruleset":null}""")]
    [InlineData("""{"expectedVersion":1,"ruleset":{}}""")]
    public async Task Malformed_body_is_a_bad_request(string body)
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await PutRawAsync(admin, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Change_without_the_antiforgery_token_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");
        admin.DefaultRequestHeaders.Remove(Hosting.Csrf.HeaderName);

        var response = await PutAsync(admin, DefaultWith(r => r["reward"]!["diceCount"]!["max"] = 12).ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("csrf.invalid", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_body_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await PutRawAsync(admin, new string(' ', 70_000) + "{}");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}


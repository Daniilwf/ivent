using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;
using GameEvent.Web.Seasons;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Roll, start and complete over HTTP. Each endpoint: success, another role or player refused, invalid input
/// (CLAUDE.md «Тесты»). The player always acts as themselves: the player id comes from the session.
/// </summary>
public sealed class SeasonApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- Success ----

    [Fact]
    public async Task Player_rolls_starts_and_completes_through_the_api()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var roll = await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        var start = await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });

        // While playing, the season view shows the active run with its game
        var playing = await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct);
        Assert.Equal(TurnPhase.Playing, playing!.Me!.Phase);
        Assert.Null(playing.Me.Offer);
        Assert.Contains(playing.Me.ActiveRun!.Game.Title, new[] { "Silent Hill", "Alan Wake", "Outlast" });
        Assert.NotNull(playing.Me.ActiveRun.Game.Hours);
        Assert.Equal(_site.Clock.UtcNow, playing.Me.ActiveRun.StartedAt);

        var complete = await PostAsync(vasya, "complete", new { commandId = Guid.NewGuid(), difficulty = "hard" });

        Assert.Equal(["game-rolled"], await TypesAsync(roll));
        Assert.Equal(["run-started"], await TypesAsync(start));
        Assert.Equal(["run-completed", "completion-rolled", "points-changed", "player-moved"], await TypesAsync(complete));

        var season = await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct);
        var me = season!.Players.Single(p => p.Id == _site.Players["vasya"]);
        Assert.True(me.Points > 0);
        var last = season.Me!.LastCompleted!;
        Assert.Equal(me.Points, last.Total);
        Assert.Equal(last.Total, last.Dice.Sum(d => d.Value));
        Assert.All(last.Dice, d => Assert.Equal(6, d.Sides)); // hard
        Assert.Equal(Engine.Runs.Difficulty.Hard, last.Difficulty);
        Assert.NotEqual("start", me.CellId);
        Assert.Equal(TurnPhase.Idle, season.Me!.Phase);
        Assert.Equal(0, season.Players.Single(p => p.Id == _site.Players["petya"]).Points);
    }

    [Fact]
    public async Task Current_season_is_the_latest_one_the_player_plays_in_and_the_latest_for_others()
    {
        // Given a later season without Вася
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddDays(1);
        var later = await _site.CreateSeasonAsync();

        // Then Вася keeps his season, a spectator and a player outside both see the latest one
        Assert.Equal(SiteFactory.SeasonId, await CurrentAsync("vasya"));
        Assert.Equal(later, await CurrentAsync("zritel"));
        Assert.Equal(later, await CurrentAsync("masha"));
    }

    [Fact]
    public async Task Current_season_needs_a_session()
    {
        var client = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/seasons/current", Ct)).StatusCode);
    }

    private async Task<Guid> CurrentAsync(string login)
    {
        var client = await _site.SignedInAsync(login);
        return (await client.GetFromJsonAsync<CurrentSeasonView>("/api/seasons/current", s_json, Ct))!.Id;
    }

    [Fact]
    public async Task Rolled_game_is_shown_to_the_player()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });

        var season = await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct);

        Assert.Equal(TurnPhase.Rolling, season!.Me!.Phase);
        Assert.Contains(season.Me.Offer!.Title, new[] { "Silent Hill", "Alan Wake", "Outlast" });
    }

    [Fact]
    public async Task Same_command_id_twice_acts_once()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var commandId = Guid.NewGuid();

        var first = await PostAsync(vasya, "roll", new { commandId });
        var second = await PostAsync(vasya, "roll", new { commandId });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var repeat = await second.Content.ReadFromJsonAsync<CommandResponse>(s_json, Ct);
        Assert.True(repeat!.Duplicate);
        Assert.Equal((await first.Content.ReadFromJsonAsync<CommandResponse>(s_json, Ct))!.Events, repeat.Events);
    }

    [Fact]
    public async Task Spectator_and_outsider_can_view_the_season()
    {
        foreach (var login in new[] { "zritel", "masha", "admin" })
        {
            var client = await _site.SignedInAsync(login);

            var season = await client.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct);

            Assert.Equal(2, season!.Players.Count);
            Assert.Null(season.Me);
            Assert.Equal(Engine.Rulesets.RulesetJson.Default().Map.LinearLength + 1, season.Cells.Count);
        }
    }

    [Fact]
    public async Task Player_chooses_one_of_several_rolled_games_and_the_choice_survives_a_reload()
    {
        // Given a season where the wheel offers a choice of three games (D-91)
        var rules = RulesetJson.Default();
        await _site.SendAsync(new ChangeRuleset(rules with { Roll = rules.Roll with { ChoiceCount = 3 } }, ExpectedVersion: null));
        var vasya = await _site.SignedInAsync("vasya");

        var roll = await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });

        // The pending choice is in the season view: a reloaded page shows the same options
        Assert.Equal(["game-choice-rolled"], await TypesAsync(roll));
        var rolled = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!;
        Assert.Equal(TurnPhase.Rolling, rolled.Phase);
        Assert.Null(rolled.Offer);
        var choice = rolled.Choice!;
        Assert.Equal(ChoiceKind.Game, choice.Kind);
        Assert.Equal(["Alan Wake", "Outlast", "Silent Hill"], choice.Options.Select(o => o.Game!.Title).Order());
        Assert.All(choice.Options, o => Assert.Equal(o.Game!.Id.ToString("N"), o.Id));

        // When the player picks one, it starts at once (Choosing --> Playing, D-91)
        var picked = choice.Options[1];
        var choose = await PostAsync(vasya, "choose", new { commandId = Guid.NewGuid(), choiceId = choice.Id, optionId = picked.Id });

        Assert.Equal(["choice-made", "run-started"], await TypesAsync(choose));
        var chosen = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!;
        Assert.Equal(TurnPhase.Playing, chosen.Phase);
        Assert.Null(chosen.Choice);
        Assert.Null(chosen.Offer);
        Assert.Equal(picked.Game, chosen.ActiveRun!.Game);

        // A second tab answering the same choice is refused with the engine code
        var again = await vasya.PostAsJsonAsync(Url("choose"), new { commandId = Guid.NewGuid(), choiceId = choice.Id, optionId = picked.Id }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        using var problem = JsonDocument.Parse(await again.Content.ReadAsStringAsync(Ct));
        Assert.Equal("turn.noPendingChoice", problem.RootElement.GetProperty("code").GetString());
    }

    private async Task<ChoiceView> RollChoiceAsync(HttpClient player)
    {
        var rules = RulesetJson.Default();
        await _site.SendAsync(new ChangeRuleset(rules with { Roll = rules.Roll with { ChoiceCount = 3 } }, ExpectedVersion: null));
        await PostAsync(player, "roll", new { commandId = Guid.NewGuid() });
        return (await player.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Choice!;
    }

    [Fact]
    public async Task Repeating_a_choice_with_the_same_command_id_acts_once()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var choice = await RollChoiceAsync(vasya);
        var body = new { commandId = Guid.NewGuid(), choiceId = choice.Id, optionId = choice.Options[0].Id };

        var first = await vasya.PostAsJsonAsync(Url("choose"), body, Ct);
        var second = await vasya.PostAsJsonAsync(Url("choose"), body, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True((await second.Content.ReadFromJsonAsync<CommandResponse>(s_json, Ct))!.Duplicate);
        await using var db = _site.NewDb();
        Assert.Equal(1, db.Events.Count(e => e.Type == "choice-made"));
    }

    // ---- Refused: no session, another role, another player ----

    [Fact]
    public async Task A_player_cannot_answer_or_see_another_players_choice()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var choice = await RollChoiceAsync(vasya);

        // Petya sends Vasya's choice and option: the engine checks it against Petya's own (absent) choice
        var petya = await _site.SignedInAsync("petya");
        var response = await petya.PostAsJsonAsync(Url("choose"), new { commandId = Guid.NewGuid(), choiceId = choice.Id, optionId = choice.Options[0].Id }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("turn.noPendingChoice", problem.RootElement.GetProperty("code").GetString());
        var mine = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Choice!;
        Assert.Equal(choice.Id, mine.Id);
        Assert.Equal(choice.Options.Select(o => o.Id), mine.Options.Select(o => o.Id));

        // Nobody else sees the options: Petya has no choice, the spectator and the admin have no turn at all
        Assert.Null((await petya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Choice);
        foreach (var login in new[] { "zritel", "admin" })
        {
            var other = await _site.SignedInAsync(login);
            var text = await other.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct);
            Assert.Null(JsonSerializer.Deserialize<SeasonView>(text, s_json)!.Me);
            Assert.DoesNotContain(choice.Options[0].Id, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("roll")]
    [InlineData("start")]
    [InlineData("complete")]
    [InlineData("choose")]
    public async Task Anonymous_is_unauthorized(string action)
    {
        var client = await _site.AnonymousAsync();

        var response = await client.PostAsJsonAsync(Url(action), new { commandId = Guid.NewGuid(), difficulty = "normal" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("zritel", "roll")]
    [InlineData("zritel", "start")]
    [InlineData("zritel", "complete")]
    [InlineData("admin", "roll")]
    [InlineData("admin", "start")]
    [InlineData("admin", "complete")]
    [InlineData("masha", "roll")]
    [InlineData("masha", "start")]
    [InlineData("masha", "complete")]
    [InlineData("zritel", "choose")]
    [InlineData("admin", "choose")]
    [InlineData("masha", "choose")]
    public async Task Spectator_admin_and_player_outside_the_season_are_forbidden(string login, string action)
    {
        var client = await _site.SignedInAsync(login);

        var response = await client.PostAsJsonAsync(Url(action), new { commandId = Guid.NewGuid(), difficulty = "normal", choiceId = Guid.NewGuid(), optionId = "x" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = _site.NewDb();
        Assert.Equal(4, db.Events.Count()); // season created and started, two players, nothing more
    }

    [Fact]
    public async Task A_player_cannot_act_for_another_player()
    {
        var vasya = await _site.SignedInAsync("vasya");

        // Extra fields naming another player are ignored: the session decides who acts
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid(), playerId = _site.Players["petya"] });

        var season = await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct);
        Assert.Equal(TurnPhase.Rolling, season!.Players.Single(p => p.Id == _site.Players["vasya"]).Phase);
        Assert.Equal(TurnPhase.Idle, season.Players.Single(p => p.Id == _site.Players["petya"]).Phase);
    }

    [Theory]
    [InlineData("roll")]
    [InlineData("start")]
    [InlineData("complete")]
    [InlineData("choose")]
    public async Task Post_without_the_antiforgery_token_is_refused(string action)
    {
        var vasya = await _site.SignedInAsync("vasya");
        vasya.DefaultRequestHeaders.Remove(Hosting.Csrf.HeaderName);

        var response = await vasya.PostAsJsonAsync(Url(action), new { commandId = Guid.NewGuid(), difficulty = "normal", choiceId = Guid.NewGuid(), optionId = "x" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Invalid input and rule rejections ----

    [Theory]
    [InlineData("roll", """{"commandId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("roll", """{"commandId":"not-a-guid"}""")]
    [InlineData("complete", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000001","difficulty":"impossible"}""")]
    [InlineData("complete", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000002","difficulty":"hard","estimatedHours":-3}""")]
    [InlineData("complete", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000003","difficulty":"hard","estimatedHours":100000}""")]
    [InlineData("complete", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000004"}""")]
    [InlineData("complete", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000005","difficulty":null}""")]
    [InlineData("start", "not json")]
    [InlineData("choose", """{"commandId":"5b1e2f0a-0000-0000-0000-00000000000a","choiceId":"5b1e2f0a-0000-0000-0000-0000000000ff","optionId":"a\nb"}""")]
    [InlineData("choose", """{"commandId":"5b1e2f0a-0000-0000-0000-00000000000b","choiceId":"5b1e2f0a-0000-0000-0000-0000000000ff","optionId":"<script>"}""")]
    [InlineData("choose", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000006","choiceId":"00000000-0000-0000-0000-000000000000","optionId":"a"}""")]
    [InlineData("choose", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000007","choiceId":"5b1e2f0a-0000-0000-0000-0000000000ff"}""")]
    [InlineData("choose", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000008","choiceId":"5b1e2f0a-0000-0000-0000-0000000000ff","optionId":"  "}""")]
    [InlineData("choose", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000009","choiceId":"5b1e2f0a-0000-0000-0000-0000000000ff","optionId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    public async Task Invalid_input_is_a_bad_request(string action, string body)
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsync(Url(action), new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("roll")]
    [InlineData("start")]
    [InlineData("complete")]
    [InlineData("choose")]
    public async Task Unknown_season_is_not_found(string action)
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(
            $"/api/seasons/{Guid.NewGuid()}/{action}", new { commandId = Guid.NewGuid(), difficulty = "normal", choiceId = Guid.NewGuid(), optionId = "x" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await vasya.GetAsync($"/api/seasons/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Rule_rejection_is_a_conflict_with_the_engine_code()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(Url("start"), new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("turn.wrongPhase", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Choosing_without_a_pending_choice_is_a_conflict()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(Url("choose"), new { commandId = Guid.NewGuid(), choiceId = Guid.NewGuid(), optionId = "x" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("turn.noPendingChoice", problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string action, object body)
    {
        var response = await client.PostAsJsonAsync(Url(action), body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return response;
    }

    private static async Task<List<string>> TypesAsync(HttpResponseMessage response) =>
        [.. (await response.Content.ReadFromJsonAsync<CommandResponse>(s_json, Ct))!.Events.Select(e => e.Type)];
}

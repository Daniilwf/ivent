using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameEvent.Engine.Files;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Suggestions;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetVips;
using static GameEvent.Web.Tests.Api.PollSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The suggestion box over HTTP (5.20, D-527, D-571, D-572, D-575, D-579): players submit, only their own
/// suggestions are returned to them, the admin moderates and an approved game reaches the shared pool.
/// Written from the public engine contract and the specification before the endpoints.
/// </summary>
public sealed class SuggestionApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Mine => Url("suggestions");

    private static string All => AdminUrl("suggestions");

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        await RulesAsync(_site, Flags(f => f with { Suggestions = true }));
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    private static GameCard Card(string title = "Prey", Guid? cover = null) =>
        new(title, ["Horror"], 12, 2017, null, cover, null, IsCoop: false);

    private async Task<Guid> SubmittedAsync(string login = "vasya", SuggestionKind kind = SuggestionKind.Event)
    {
        var before = (await StateAsync(_site)).Suggestions.Keys.ToHashSet();
        await _site.SendAsync(new SubmitSuggestion(_site.Players[login], kind, $"Предложение {login}", kind == SuggestionKind.Game ? Card() : null));
        return (await StateAsync(_site)).Suggestions.Keys.Single(id => !before.Contains(id));
    }

    [Theory]
    [InlineData("event")]
    [InlineData("item")]
    [InlineData("game")]
    public async Task A_player_submits_each_kind_as_himself_and_the_projection_keeps_the_payload(string kind)
    {
        var vasya = await _site.SignedInAsync("vasya");
        var response = await vasya.PostAsJsonAsync(Mine, new
        {
            commandId = Guid.NewGuid(),
            playerId = _site.Players["petya"],
            kind,
            text = "Новая идея",
            game = kind == "game" ? Card() : null,
        }, Ct);

        Assert.Contains("suggestion-submitted", await TypesAsync(response));
        var suggestion = Assert.Single((await StateAsync(_site)).Suggestions.Values);
        Assert.Equal(_site.Players["vasya"], suggestion.PlayerId);
        Assert.Equal(_site.Users["vasya"], suggestion.UserId);
        Assert.Equal(kind, suggestion.Kind.ToString().ToLowerInvariant());
        Assert.Equal("Новая идея", suggestion.Text);
        await using var db = _site.NewDb();
        var row = Assert.Single(await db.Suggestions.AsNoTracking().ToListAsync(Ct));
        Assert.Equal((suggestion.SuggestionId, suggestion.PlayerId, suggestion.Kind), (row.Id, row.PlayerId, row.Kind));
        if (kind == "game") Assert.Equal("Prey", suggestion.Game!.Title);
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_player_reads_only_his_own_suggestions_and_the_admin_reads_every_author()
    {
        var own = await SubmittedAsync("vasya");
        var other = await SubmittedAsync("petya");
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");

        var mine = Assert.Single((await ApiCalls.OkAsync(await vasya.GetAsync(Mine, Ct))).EnumerateArray());
        Assert.Equal(own, mine.GetProperty("id").GetGuid());
        Assert.Equal(_site.Players["vasya"], mine.GetProperty("playerId").GetGuid());
        Assert.Equal("Предложение vasya", mine.GetProperty("text").GetString());
        Assert.Equal("pending", mine.GetProperty("status").GetString());
        var all = (await ApiCalls.OkAsync(await admin.GetAsync(All, Ct))).EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToHashSet();
        Assert.True(all.SetEquals([own, other]));
    }

    [Fact]
    public async Task Empty_lists_are_successful_and_an_unknown_season_is_not_found()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        Assert.Empty((await ApiCalls.OkAsync(await vasya.GetAsync(Mine, Ct))).EnumerateArray());
        Assert.Empty((await ApiCalls.OkAsync(await admin.GetAsync(All, Ct))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await vasya.GetAsync($"/api/seasons/{Guid.Empty}/suggestions", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/seasons/{Guid.Empty}/suggestions", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("zritel")]
    [InlineData("masha")]
    public async Task A_spectator_or_player_outside_the_season_neither_reads_private_suggestions_nor_submits(string login)
    {
        await SubmittedAsync();
        var client = await _site.SignedInAsync(login);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Mine, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Mine, new { commandId = Guid.NewGuid(), kind = "event", text = "Чужая идея" }, Ct)).StatusCode);
        Assert.Single((await StateAsync(_site)).Suggestions);
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Only_the_admin_reads_the_moderation_list(string login)
    {
        var client = await _site.SignedInAsync(login);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(All, Ct)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_reads_and_submissions_are_unauthorized()
    {
        var client = await _site.AnonymousAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Mine, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(All, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Mine, new { commandId = Guid.NewGuid(), kind = "event", text = "Идея" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Submission_requires_a_command_id_and_a_known_kind()
    {
        var client = await _site.SignedInAsync("vasya");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Mine, new { kind = "event", text = "Идея" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Mine, new { commandId = Guid.NewGuid(), kind = "unknown", text = "Идея" }, Ct)).StatusCode);
        Assert.Empty((await StateAsync(_site)).Suggestions);
    }

    [Fact]
    public async Task The_domain_refuses_empty_text_a_missing_game_and_an_invalid_card()
    {
        var client = await _site.SignedInAsync("vasya");
        await AssertConflictAsync(await client.PostAsJsonAsync(Mine, new { commandId = Guid.NewGuid(), kind = "event", text = "   " }, Ct), "suggestion.empty");
        await AssertConflictAsync(await client.PostAsJsonAsync(Mine, new { commandId = Guid.NewGuid(), kind = "game", text = "Игра" }, Ct), "suggestion.gameRequired");
        await AssertConflictAsync(await client.PostAsJsonAsync(Mine, new { commandId = Guid.NewGuid(), kind = "game", text = "Игра", game = Card() with { Hours = -1 } }, Ct), "suggestion.cardInvalid");
        Assert.Empty((await StateAsync(_site)).Suggestions);
    }

    [Fact]
    public async Task The_daily_limit_is_enforced_and_repeating_a_command_does_not_spend_it_twice()
    {
        await RulesAsync(_site, rules => rules with { Suggestions = new SuggestionRules { PerDay = 1 } });
        var client = await _site.SignedInAsync("vasya");
        var body = new { commandId = Guid.NewGuid(), kind = "item", text = "Предмет" };
        await ApiCalls.PostOkAsync(client, Mine, body);

        var repeated = await ApiCalls.OkAsync(await client.PostAsJsonAsync(Mine, body, Ct));
        Assert.True(repeated.GetProperty("duplicate").GetBoolean());
        await AssertConflictAsync(await client.PostAsJsonAsync(Mine, new { commandId = Guid.NewGuid(), kind = "item", text = "Ещё предмет" }, Ct), "suggestion.dailyLimit");
        Assert.Single((await StateAsync(_site)).Suggestions);
    }

    [Fact]
    public async Task The_admin_approves_a_game_and_the_queue_adds_it_to_the_pool_with_its_author()
    {
        var id = await SubmittedAsync(kind: SuggestionKind.Game);
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync($"{All}/{id}/review", new { commandId = Guid.NewGuid(), approve = true, comment = "Подходит" }, Ct);

        Assert.Contains("suggestion-reviewed", await TypesAsync(response));
        Assert.Equal(SuggestionStatus.Approved, (await StateAsync(_site)).Suggestions[id].Status);
        await using var db = _site.NewDb();
        var game = await db.Games.AsNoTracking().SingleAsync(game => game.Title == "Prey", Ct);
        Assert.Equal(_site.Users["vasya"], game.AuthorId);
        Assert.Equal(12m, game.Hours);
        Assert.Equal(2017, game.Year);
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_rejection_requires_a_comment_and_its_author_sees_the_reason()
    {
        var id = await SubmittedAsync();
        var admin = await _site.SignedInAsync("admin");
        await AssertConflictAsync(await admin.PostAsJsonAsync($"{All}/{id}/review", new { commandId = Guid.NewGuid(), approve = false }, Ct), "player.commentRequired");

        await ApiCalls.PostOkAsync(admin, $"{All}/{id}/review", new { commandId = Guid.NewGuid(), approve = false, comment = "Не подходит правилам" });

        var item = Assert.Single((await ApiCalls.OkAsync(await (await _site.SignedInAsync("vasya")).GetAsync(Mine, Ct))).EnumerateArray());
        Assert.Equal("rejected", item.GetProperty("status").GetString());
        Assert.Equal("Не подходит правилам", item.GetProperty("comment").GetString());
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("petya")]
    [InlineData("zritel")]
    public async Task A_player_or_spectator_cannot_moderate_even_his_own_suggestion(string login)
    {
        var id = await SubmittedAsync();
        var client = await _site.SignedInAsync(login);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{All}/{id}/review", new { commandId = Guid.NewGuid(), approve = true }, Ct)).StatusCode);
        Assert.Equal(SuggestionStatus.Pending, (await StateAsync(_site)).Suggestions[id].Status);
    }

    [Fact]
    public async Task Review_requires_a_command_id_and_does_not_moderate_a_suggestion_of_another_season()
    {
        var id = await SubmittedAsync();
        var admin = await _site.SignedInAsync("admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{All}/{id}/review", new { approve = true }, Ct)).StatusCode);
        await AssertConflictAsync(await admin.PostAsJsonAsync($"{All}/{Guid.NewGuid()}/review", new { commandId = Guid.NewGuid(), approve = true }, Ct), "suggestion.unknown");
        var other = await _site.CreateSeasonAsync();
        var bus = _site.Services.GetRequiredService<CommandBus>();
        var rules = (await StateAsync(_site)).Rules;
        var outcome = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), other, new ChangeRuleset(rules), null), Ct);
        Assert.True(outcome.IsAccepted, outcome.Rejection?.ToString());
        await AssertConflictAsync(await admin.PostAsJsonAsync($"/api/admin/seasons/{other}/suggestions/{id}/review", new { commandId = Guid.NewGuid(), approve = true }, Ct), "suggestion.unknown");
        Assert.Equal(SuggestionStatus.Pending, (await StateAsync(_site)).Suggestions[id].Status);
    }

    [Fact]
    public async Task The_same_review_command_is_idempotent_and_adds_the_game_once()
    {
        var id = await SubmittedAsync(kind: SuggestionKind.Game);
        var admin = await _site.SignedInAsync("admin");
        var body = new { commandId = Guid.NewGuid(), approve = true };
        await ApiCalls.PostOkAsync(admin, $"{All}/{id}/review", body);

        var repeated = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{All}/{id}/review", body, Ct));

        Assert.True(repeated.GetProperty("duplicate").GetBoolean());
        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Games.CountAsync(game => game.Title == "Prey", Ct));
    }

    [Theory]
    [InlineData("Silent Hill", "pool.duplicate")]
    [InlineData("Silent Hill 2", "pool.similar")]
    public async Task Approving_a_duplicate_or_similar_game_keeps_approval_but_reports_the_pool_refusal(string title, string warning)
    {
        // A valid suggestion can still name a title already in the global pool (D-572, D-813).
        await _site.SendAsync(new SubmitSuggestion(_site.Players["vasya"], SuggestionKind.Game, "Игра для пула", Card(title)));
        var id = Assert.Single((await StateAsync(_site)).Suggestions.Keys);
        int before;
        await using (var db = _site.NewDb())
        {
            before = await db.Games.CountAsync(Ct);
        }
        var admin = await _site.SignedInAsync("admin");

        var response = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{All}/{id}/review", new { commandId = Guid.NewGuid(), approve = true }, Ct));

        Assert.Equal(SuggestionStatus.Approved, (await StateAsync(_site)).Suggestions[id].Status);
        await using (var db = _site.NewDb())
        {
            Assert.Equal(before, await db.Games.CountAsync(Ct));
        }
        await AssertIntactAsync(_site);
        Assert.Contains(warning, response.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Undoing_approval_after_the_game_was_rolled_keeps_the_game_in_the_shared_pool()
    {
        var id = await SubmittedAsync(kind: SuggestionKind.Game);
        var admin = await _site.SignedInAsync("admin");
        var approval = Guid.NewGuid();
        await ApiCalls.PostOkAsync(admin, $"{All}/{id}/review", new { commandId = approval, approve = true });
        Guid gameId;
        List<Guid> others;
        await using (var db = _site.NewDb())
        {
            gameId = await db.Games.Where(game => game.Title == "Prey").Select(game => game.Id).SingleAsync(Ct);
            others = await db.Games.Where(game => game.Id != gameId && !game.IsDeleted).Select(game => game.Id).ToListAsync(Ct);
        }
        // The rest of the pool is unavailable, so the HTTP roll must select the approved game.
        var bus = _site.Services.GetRequiredService<CommandBus>();
        foreach (var other in others)
        {
            var removed = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), Guid.Empty, new DeleteGame(other, "Проверка отката предложки"), _site.Users["admin"]), Ct);
            Assert.True(removed.IsAccepted, removed.Rejection?.ToString());
        }
        var vasya = await _site.SignedInAsync("vasya");
        await ApiCalls.PostOkAsync(vasya, Url("roll"));
        await ApiCalls.PostOkAsync(vasya, Url("start"));
        var state = await StateAsync(_site);
        Assert.Equal(gameId, state.Runs[state.Players[_site.Players["vasya"]].ActiveRunId!.Value].GameId);

        await ApiCalls.PostOkAsync(admin, AdminUrl("undo"), new { commandId = Guid.NewGuid(), targetCommandId = approval, comment = "Одобрено ошибочно" });

        Assert.Equal(SuggestionStatus.Pending, (await StateAsync(_site)).Suggestions[id].Status);
        await using (var db = _site.NewDb())
        {
            var game = await db.Games.AsNoTracking().SingleAsync(game => game.Id == gameId, Ct);
            Assert.False(game.IsDeleted);
            Assert.Equal("Prey", game.Title);
        }
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task Switching_suggestions_off_hides_lists_and_refuses_submitting_and_reviewing()
    {
        var id = await SubmittedAsync();
        await RulesAsync(_site, Flags(f => f with { Suggestions = false }));
        var client = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        await AssertConflictAsync(await client.GetAsync(Mine, Ct), "feature.disabled");
        await AssertConflictAsync(await admin.GetAsync(All, Ct), "feature.disabled");
        await AssertConflictAsync(await client.PostAsJsonAsync(Mine, new { commandId = Guid.NewGuid(), kind = "event", text = "Идея" }, Ct), "feature.disabled");
        await AssertConflictAsync(await admin.PostAsJsonAsync($"{All}/{id}/review", new { commandId = Guid.NewGuid(), approve = true }, Ct), "feature.disabled");
    }

    [Fact]
    public async Task Suggestions_and_moderation_continue_after_the_deadline_until_results_are_fixed()
    {
        await _site.SendAsync(new SetSeasonDeadline(_site.Clock.UtcNow.AddHours(1)));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(2);
        await TickAsync(_site);
        var client = await _site.SignedInAsync("vasya");
        await ApiCalls.PostOkAsync(client, Mine, new { commandId = Guid.NewGuid(), kind = "event", text = "Идея после дедлайна" });
        var id = Assert.Single((await StateAsync(_site)).Suggestions.Keys);
        await ApiCalls.PostOkAsync(await _site.SignedInAsync("admin"), $"{All}/{id}/review", new { commandId = Guid.NewGuid(), approve = false, comment = "На другой сезон" });
    }

    [Fact]
    public async Task A_game_may_reference_its_authors_live_uploaded_cover()
    {
        var client = await _site.SignedInAsync("vasya");
        var cover = await UploadAsync(client);

        await ApiCalls.PostOkAsync(client, Mine, new { commandId = Guid.NewGuid(), kind = "game", text = "С обложкой", game = Card(cover: cover) });

        Assert.Equal(cover, Assert.Single((await StateAsync(_site)).Suggestions.Values).Game!.CoverFileId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unknown_or_another_players_cover_is_refused_before_any_suggestion_is_written(bool foreign)
    {
        var cover = foreign ? await UploadAsync(await _site.SignedInAsync("petya")) : Guid.NewGuid();
        var client = await _site.SignedInAsync("vasya");

        var response = await client.PostAsJsonAsync(Mine, new { commandId = Guid.NewGuid(), kind = "game", text = "С чужой обложкой", game = Card(cover: cover) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await StateAsync(_site)).Suggestions);
        Assert.Equal(0, await CountAsync(_site, "suggestion-submitted"));
    }

    [Fact]
    public async Task An_authors_deleted_upload_cannot_become_a_suggested_games_cover()
    {
        var client = await _site.SignedInAsync("vasya");
        var cover = await UploadAsync(client);
        // Fixture: an existing reference whose stored file has been soft-deleted, as in the map-background tests.
        await using (var db = _site.NewDb())
        {
            (await db.Files.SingleAsync(file => file.Id == cover, Ct)).IsDeleted = true;
            await db.SaveChangesAsync(Ct);
        }

        var response = await client.PostAsJsonAsync(Mine, new { commandId = Guid.NewGuid(), kind = "game", text = "С удалённой обложкой", game = Card(cover: cover) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await StateAsync(_site)).Suggestions);
    }

    [Fact]
    public async Task An_authors_private_bug_screenshot_cannot_become_a_public_game_cover()
    {
        var cover = Guid.NewGuid();
        var bus = _site.Services.GetRequiredService<CommandBus>();
        var stored = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), Guid.Empty,
            new RecordFile(cover, _site.Users["vasya"], FileNames.Webp, 100, 30, 40, 1, 60, FileKind.BugScreenshot), _site.Users["vasya"]), Ct);
        Assert.True(stored.IsAccepted, stored.Rejection?.ToString());
        var client = await _site.SignedInAsync("vasya");

        var response = await client.PostAsJsonAsync(Mine, new { commandId = Guid.NewGuid(), kind = "game", text = "С закрытой картинкой", game = Card(cover: cover) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await StateAsync(_site)).Suggestions);
    }

    private static async Task<Guid> UploadAsync(HttpClient client)
    {
        using var grey = Image.Black(30, 40, bands: 3) + 90;
        using var picture = grey.Cast(Enums.BandFormat.Uchar);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Guid.NewGuid().ToString()), "commandId");
        var file = new ByteArrayContent(picture.PngsaveBuffer());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "cover.png");
        return (await ApiCalls.OkAsync(await client.PostAsync("/api/files", form, Ct))).GetProperty("id").GetGuid();
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static GameEvent.Web.Tests.Api.EventSeason;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// A suspended event through the API (task 5.18, SPEC «Общий механизм выбора», D-511, D-567, D-569 (8), D-822): the
/// question is my pending choice of kind <c>effect</c> with the event's prompt and name; I answer it through the shared
/// <c>POST /api/seasons/{id}/choose</c>; the admin sees that a player waits on a question (<c>eventQuestion</c> in the
/// players list) and takes it off with the player correction <c>discardOffer</c>. The event is «Монетки или шаг» of
/// CONTENT.md, drawn by a drop.
/// </summary>
public sealed class SuspendedEventApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ChooseUrl => $"{Season}/choose";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task The_question_of_an_event_is_my_choice_with_its_prompt_name_and_options()
    {
        var (_, vasya) = await QuestionAsync();

        var choice = (await MeAsync(vasya)).GetProperty("choice");

        Assert.Equal("effect", choice.GetProperty("kind").GetString());
        Assert.Equal("Что берёшь?", choice.GetProperty("prompt").GetString());
        Assert.Equal(("coin-or-step", "Монетки или шаг"), (choice.GetProperty("objectId").GetString(), choice.GetProperty("name").GetString()));
        Assert.Equal(["монетки", "шаг"], choice.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("id").GetString()));
        Assert.All(choice.GetProperty("options").EnumerateArray(), o => Assert.Equal(JsonValueKind.Null, o.GetProperty("game").ValueKind));
    }

    [Fact]
    public async Task Others_do_not_see_my_question()
    {
        var (_, _) = await QuestionAsync();
        var petya = await _site.SignedInAsync("petya");

        Assert.Equal(JsonValueKind.Null, (await MeAsync(petya)).GetProperty("choice").ValueKind);
        Assert.DoesNotContain("Что берёшь?", await RawSeasonAsync(petya), StringComparison.Ordinal);
    }

    [Fact]
    public async Task My_answer_through_choose_resumes_the_event_and_closes_the_question()
    {
        var (_, vasya) = await QuestionAsync();
        var before = await MeAsync(vasya);
        var coins = before.GetProperty("coins").GetInt32();

        var response = await PostOkAsync(vasya, ChooseUrl, new { commandId = Guid.NewGuid(), choiceId = ChoiceId(before), optionId = "монетки" });

        Assert.Contains("effect-resumed", await TypesAsync(response));
        var after = await MeAsync(vasya);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("choice").ValueKind);
        Assert.Equal(coins + 3, after.GetProperty("coins").GetInt32());
    }

    [Fact]
    public async Task The_same_answer_twice_by_one_command_id_acts_once()
    {
        var (_, vasya) = await QuestionAsync();
        var before = await MeAsync(vasya);
        var coins = before.GetProperty("coins").GetInt32();
        var body = new { commandId = Guid.NewGuid(), choiceId = ChoiceId(before), optionId = "монетки" };

        await PostOkAsync(vasya, ChooseUrl, body);
        await PostOkAsync(vasya, ChooseUrl, body);

        Assert.Equal(coins + 3, (await MeAsync(vasya)).GetProperty("coins").GetInt32());
    }

    [Fact]
    public async Task An_option_that_is_not_offered_is_refused_and_the_question_stays()
    {
        var (_, vasya) = await QuestionAsync();
        var me = await MeAsync(vasya);

        var response = await vasya.PostAsJsonAsync(ChooseUrl, new { commandId = Guid.NewGuid(), choiceId = ChoiceId(me), optionId = "всё сразу" }, Ct);

        await AssertConflictAsync(response, "turn.unknownOption");
        Assert.Equal("effect", (await MeAsync(vasya)).GetProperty("choice").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Another_player_cannot_answer_my_question()
    {
        var (_, vasya) = await QuestionAsync();
        var petya = await _site.SignedInAsync("petya");
        var me = await MeAsync(vasya);

        var response = await petya.PostAsJsonAsync(ChooseUrl, new { commandId = Guid.NewGuid(), choiceId = ChoiceId(me), optionId = "монетки" }, Ct);

        await AssertConflictAsync(response, "turn.noPendingChoice");
        Assert.Equal("effect", (await MeAsync(vasya)).GetProperty("choice").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_spectator_cannot_answer_and_an_answer_without_an_option_is_invalid_input()
    {
        var (_, vasya) = await QuestionAsync();
        var zritel = await _site.SignedInAsync("zritel");
        var me = await MeAsync(vasya);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await zritel.PostAsJsonAsync(ChooseUrl, new { commandId = Guid.NewGuid(), choiceId = ChoiceId(me), optionId = "монетки" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await vasya.PostAsJsonAsync(ChooseUrl, new { commandId = Guid.NewGuid(), choiceId = ChoiceId(me) }, Ct)).StatusCode);
        Assert.Equal("effect", (await MeAsync(vasya)).GetProperty("choice").GetProperty("kind").GetString());
    }

    // ---- Questions of actions, of targets, and options of any text (D-822, D-827) ----

    [Fact]
    public async Task The_question_of_a_requestChoice_after_another_action_carries_that_actions_prompt()
    {
        var (_, vasya) = await QuestionAsync(GenrePick, ForcedGenre);

        var choice = (await MeAsync(vasya)).GetProperty("choice");

        Assert.Equal("effect", choice.GetProperty("kind").GetString());
        Assert.Equal("Какой жанр подкинуть?", choice.GetProperty("prompt").GetString());
        Assert.Equal(("genre-pick", "Жанр на выбор"), (choice.GetProperty("objectId").GetString(), choice.GetProperty("name").GetString()));
    }

    [Theory]
    [InlineData("Point & Click")]
    [InlineData("Action/Adventure")]
    public async Task A_category_whose_name_has_any_punctuation_is_answered_through_choose(string category)
    {
        await using (var db = _site.NewDb())
        {
            db.Categories.Add(new Infrastructure.Pool.CategoryRecord { Name = category, Weight = 1 });
            await db.SaveChangesAsync(Ct);
        }

        var (_, vasya) = await QuestionAsync(GenrePick, ForcedGenre);
        var me = await MeAsync(vasya);
        Assert.Contains(category, me.GetProperty("choice").GetProperty("options").EnumerateArray().Select(o => o.GetProperty("id").GetString()));

        var response = await PostOkAsync(vasya, ChooseUrl, new { commandId = Guid.NewGuid(), choiceId = ChoiceId(me), optionId = category });

        Assert.Contains("effect-resumed", await TypesAsync(response));
        Assert.Equal(JsonValueKind.Null, (await MeAsync(vasya)).GetProperty("choice").ValueKind);
    }

    [Fact]
    public async Task An_option_of_the_content_examples_with_a_plus_and_digits_is_answered_through_choose()
    {
        var (_, vasya) = await QuestionAsync(SugarEvent);
        var me = await MeAsync(vasya);
        Assert.Equal(["+1d6 к броску", "+3 монетки"], me.GetProperty("choice").GetProperty("options").EnumerateArray().Select(o => o.GetProperty("id").GetString()));

        var response = await PostOkAsync(vasya, ChooseUrl, new { commandId = Guid.NewGuid(), choiceId = ChoiceId(me), optionId = "+1d6 к броску" });

        Assert.Contains("effect-resumed", await TypesAsync(response));
    }

    [Theory]
    [InlineData("\\u0000монетки")]
    [InlineData("моне\\u200Bтки")]
    [InlineData(" монетки")]
    [InlineData("монетки ")]
    public async Task An_option_with_control_or_format_characters_or_blanks_around_is_invalid_input(string escaped)
    {
        var (_, vasya) = await QuestionAsync();
        var me = await MeAsync(vasya);
        var body = $$"""{"commandId":"{{Guid.NewGuid()}}","choiceId":"{{ChoiceId(me)}}","optionId":"{{escaped}}"}""";

        var response = await vasya.PostAsync(ChooseUrl, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("effect", (await MeAsync(vasya)).GetProperty("choice").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task An_option_longer_than_the_limit_is_invalid_input()
    {
        var (_, vasya) = await QuestionAsync();
        var me = await MeAsync(vasya);

        var response = await vasya.PostAsJsonAsync(
            ChooseUrl, new { commandId = Guid.NewGuid(), choiceId = ChoiceId(me), optionId = new string('я', Seasons.SeasonEndpoints.MaxOptionIdLength + 1) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_question_of_a_chosen_target_has_no_prompt_and_offers_the_players()
    {
        var (_, vasya) = await QuestionAsync(Gift);

        var choice = (await MeAsync(vasya)).GetProperty("choice");

        Assert.Equal("effect", choice.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, choice.GetProperty("prompt").ValueKind);
        Assert.Equal(("gift", "Подарок"), (choice.GetProperty("objectId").GetString(), choice.GetProperty("name").GetString()));
        Assert.Equal([_site.Players["petya"].ToString("N")], choice.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task The_question_of_a_move_in_a_chosen_direction_has_no_prompt()
    {
        var (_, vasya) = await QuestionAsync(Crossroads);

        var choice = (await MeAsync(vasya)).GetProperty("choice");

        Assert.Equal("effect", choice.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, choice.GetProperty("prompt").ValueKind);
        Assert.Equal(("crossroads", "Развилка"), (choice.GetProperty("objectId").GetString(), choice.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task The_question_keeps_its_prompt_when_the_admin_switches_events_off_while_it_waits()
    {
        var (admin, vasya) = await QuestionAsync();

        await SwitchEventsAsync(admin, on: false);

        var choice = (await MeAsync(vasya)).GetProperty("choice");
        Assert.Equal("effect", choice.GetProperty("kind").GetString());
        Assert.Equal("Что берёшь?", choice.GetProperty("prompt").GetString());
        Assert.Equal("coin-or-step", choice.GetProperty("objectId").GetString());
    }

    // ---- The admin: the mark in the players list and taking the question off (D-569 (3), (8)) ----

    [Fact]
    public async Task The_admin_players_list_marks_a_player_waiting_on_a_question_of_an_event()
    {
        var (admin, _) = await QuestionAsync();

        var players = await PlayersAsync(admin);

        Assert.True(players[_site.Players["vasya"]].GetProperty("eventQuestion").GetBoolean());
        Assert.False(players[_site.Players["petya"]].GetProperty("eventQuestion").GetBoolean());
    }

    [Fact]
    public async Task The_admin_takes_the_question_off_and_the_mark_goes()
    {
        var (admin, vasya) = await QuestionAsync();
        var coins = (await MeAsync(vasya)).GetProperty("coins").GetInt32();

        await PostOkAsync(
            admin,
            $"{Admin}/players/{_site.Players["vasya"]}/adjust",
            new { commandId = Guid.NewGuid(), comment = "Игрок пропал", discardOffer = true });

        var me = await MeAsync(vasya);
        Assert.Equal(JsonValueKind.Null, me.GetProperty("choice").ValueKind);
        Assert.Equal(coins, me.GetProperty("coins").GetInt32());
        Assert.False((await PlayersAsync(admin))[_site.Players["vasya"]].GetProperty("eventQuestion").GetBoolean());
    }

    [Theory]
    [InlineData("petya")]
    [InlineData("zritel")]
    public async Task Players_and_spectators_cannot_take_the_question_off(string login)
    {
        var (_, vasya) = await QuestionAsync();
        var client = await _site.SignedInAsync(login);

        var response = await client.PostAsJsonAsync(
            $"{Admin}/players/{_site.Players["vasya"]}/adjust", new { commandId = Guid.NewGuid(), comment = "Моё", discardOffer = true }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("effect", (await MeAsync(vasya)).GetProperty("choice").GetProperty("kind").GetString());
    }

    // ---- Helpers ----

    /// <summary>Events on with «Монетки или шаг» in the bad deck; Вася drops and his event waits for his answer.</summary>
    private async Task<(HttpClient Admin, HttpClient Vasya)> QuestionAsync(string? badEvent = null, params string[] extra)
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, badEvent ?? CoinOrStep, 5, extra);
        await DropAsync(vasya);
        return (admin, vasya);
    }

    /// <summary>CONTENT.md «Подлянка» on oneself, its question asked by the second action: a category of the pool.</summary>
    private const string GenrePick = """
        { "id": "genre-pick", "kind": "event", "name": "Жанр на выбор",
          "description": "Получи монетку и выбери жанр своей следующей игры.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "self" }, "actions": [
            { "type": "changeResource", "resource": "coins", "amount": 1 },
            { "type": "requestChoice", "prompt": "Какой жанр подкинуть?", "options": { "from": "categories" } },
            { "type": "giveObject", "objectId": "forced-genre", "params": { "tag": "$choice" } } ] } }
        """;

    /// <summary>CONTENT.md «Навязанный жанр», as it is written there.</summary>
    private const string ForcedGenre = """
        { "id": "forced-genre", "kind": "specialRoll", "name": "Навязанный жанр",
          "description": "Следующая игра будет из выбранного жанра.",
          "rarity": "common", "price": null, "hostile": true,
          "effect": { "trigger": "beforeRoll", "duration": { "uses": 1 }, "actions": [{ "type": "modifyNextRoll", "filter": { "tags": ["$tag"] } }] } }
        """;

    /// <summary>CONTENT.md «Кубик сахара» as an event: options with a plus, digits and a space.</summary>
    private const string SugarEvent = """
        { "id": "sugar-event", "kind": "event", "name": "Сахарок",
          "description": "На выбор: дополнительный d6 к следующему броску за прохождение или 3 монетки.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "self" }, "outcomes": {
            "choice": { "prompt": "Что берёшь?", "options": { "list": ["+1d6 к броску", "+3 монетки"] } },
            "cases": [
              { "option": "+1d6 к броску", "actions": [{ "type": "modifyDice", "when": "next", "stage": "count", "value": 1, "sides": 6 }] },
              { "option": "+3 монетки", "actions": [{ "type": "changeResource", "resource": "coins", "amount": 3 }] } ] } } }
        """;

    /// <summary>A target the player picks: the question is whom, without a prompt.</summary>
    private const string Gift = """
        { "id": "gift", "kind": "event", "name": "Подарок",
          "description": "Выбери другого игрока: он получит 2 монетки.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "chosen", "excludeSelf": true }, "actions": [{ "type": "changeResource", "resource": "coins", "amount": 2 }] } }
        """;

    /// <summary>CONTENT.md «Пистолет»'s move on oneself: the question is the direction, without a prompt.</summary>
    private const string Crossroads = """
        { "id": "crossroads", "kind": "event", "name": "Развилка",
          "description": "Шагни на 1 клетку вперёд или назад — на твой выбор.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "self" }, "actions": [{ "type": "move", "steps": 1, "direction": "chosen" }] } }
        """;

    private static Guid ChoiceId(JsonElement me) => me.GetProperty("choice").GetProperty("id").GetGuid();

    private static async Task<Dictionary<Guid, JsonElement>> PlayersAsync(HttpClient admin)
    {
        var list = await ApiCalls.OkAsync(await admin.GetAsync($"{Admin}/players", Ct));
        return list.EnumerateArray().ToDictionary(p => p.GetProperty("id").GetGuid());
    }
}

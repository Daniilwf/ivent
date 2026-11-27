using System.Net.Http.Json;
using System.Text.Json;
using static GameEvent.Web.Tests.Api.EventSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The public view needed by the event UI (5.21): the live feature flag and server-labelled choices.
/// A paused event keeps its question when disabled, but cannot continue until the feature returns.
/// Labels describe the source of an option; a literal UUID is still literal text, not a player reference.
/// </summary>
public sealed class EventUiFeatureApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task My_turn_reports_the_current_events_feature_flag(bool enabled)
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchEventsAsync(admin, enabled);

        Assert.Equal(enabled, (await MeAsync(vasya)).GetProperty("eventsEnabled").GetBoolean());
    }

    [Fact]
    public async Task Disabling_events_preserves_the_question_but_refuses_its_answer()
    {
        var (admin, vasya) = await QuestionAsync(CoinOrStep);
        var before = await MeAsync(vasya);
        var choice = before.GetProperty("choice");
        await SwitchEventsAsync(admin, on: false);

        var refused = await vasya.PostAsJsonAsync(
            $"{Season}/choose", new { commandId = Guid.NewGuid(), choiceId = choice.GetProperty("id").GetGuid(), optionId = "монетки" }, Ct);

        await AssertConflictAsync(refused, "feature.disabled");
        var disabled = await MeAsync(vasya);
        Assert.False(disabled.GetProperty("eventsEnabled").GetBoolean());
        Assert.Equal(choice.GetRawText(), disabled.GetProperty("choice").GetRawText());
        Assert.Equal("Что берёшь?", disabled.GetProperty("choice").GetProperty("prompt").GetString());
        Assert.Equal("Монетки или шаг", disabled.GetProperty("choice").GetProperty("name").GetString());
        Assert.Equal(before.GetProperty("coins").GetInt32(), disabled.GetProperty("coins").GetInt32());
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task Reenabling_events_reports_true_and_resumes_the_same_question_once()
    {
        var (admin, vasya) = await QuestionAsync(CoinOrStep);
        var before = await MeAsync(vasya);
        var choiceId = before.GetProperty("choice").GetProperty("id").GetGuid();
        await SwitchEventsAsync(admin, on: false);
        await SwitchEventsAsync(admin, on: true);
        var enabled = await MeAsync(vasya);
        Assert.True(enabled.GetProperty("eventsEnabled").GetBoolean());
        Assert.Equal(choiceId, enabled.GetProperty("choice").GetProperty("id").GetGuid());

        var response = await vasya.PostAsJsonAsync(
            $"{Season}/choose", new { commandId = Guid.NewGuid(), choiceId, optionId = "монетки" }, Ct);

        Assert.Contains("effect-resumed", await TypesAsync(response));
        var after = await MeAsync(vasya);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("choice").ValueKind);
        Assert.Equal(before.GetProperty("coins").GetInt32() + 3, after.GetProperty("coins").GetInt32());
        await AssertIntactAsync(_site);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_suspended_question_exposes_its_description_even_when_events_are_disabled(bool enabled)
    {
        var (admin, vasya) = await QuestionAsync(CoinOrStep);
        if (!enabled)
        {
            await SwitchEventsAsync(admin, on: false);
        }

        var me = await MeAsync(vasya);
        var choice = me.GetProperty("choice");

        Assert.Equal(enabled, me.GetProperty("eventsEnabled").GetBoolean());
        Assert.Equal("coin-or-step", choice.GetProperty("objectId").GetString());
        Assert.Equal("Монетки или шаг", choice.GetProperty("name").GetString());
        Assert.Equal("Что берёшь?", choice.GetProperty("prompt").GetString());
        await AssertIntactAsync(_site);
        Assert.Equal("Выбери: 3 монетки или шаг вперёд.", choice.GetProperty("description").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_question_description_belongs_to_its_definition_rather_than_a_different_last_draw(bool enabled)
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, CoinOrStep, 5, DoubleFortune);
        await ApiCalls.PostOkAsync(
            admin, $"{Admin}/players/{_site.Players["vasya"]}/inventory", new { commandId = Guid.NewGuid(), giveObjectId = "double-fortune", comment = "Два ивента" });
        var economy = await ApiCalls.OkAsync(await vasya.GetAsync($"{Season}/economy", Ct));
        var item = Assert.Single(economy.GetProperty("items").EnumerateArray());
        await ApiCalls.PostOkAsync(vasya, $"{Season}/items/{item.GetProperty("instanceId").GetGuid()}/use");
        var whileEnabled = await MeAsync(vasya);
        var otherDraw = whileEnabled.GetProperty("lastEvent");
        Assert.Equal("tailwind", otherDraw.GetProperty("objectId").GetString());
        Assert.Equal("В следующем броске за прохождение на один кубик больше.", otherDraw.GetProperty("description").GetString());
        if (!enabled)
        {
            await SwitchEventsAsync(admin, on: false);
        }

        var me = await MeAsync(vasya);
        var choice = me.GetProperty("choice");

        Assert.Equal(enabled, me.GetProperty("eventsEnabled").GetBoolean());
        Assert.Equal("coin-or-step", choice.GetProperty("objectId").GetString());
        Assert.Equal("Монетки или шаг", choice.GetProperty("name").GetString());
        await AssertIntactAsync(_site);
        Assert.Equal("Выбери: 3 монетки или шаг вперёд.", choice.GetProperty("description").GetString());
    }

    [Fact]
    public async Task A_chosen_target_option_carries_the_players_name_without_changing_its_id()
    {
        var (_, vasya) = await QuestionAsync(Gift);

        var option = Assert.Single((await MeAsync(vasya)).GetProperty("choice").GetProperty("options").EnumerateArray());

        Assert.Equal(_site.Players["petya"].ToString("N"), option.GetProperty("id").GetString());
        Assert.Equal("petya", option.GetProperty("label").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_literal_list_option_keeps_its_exact_text_even_when_it_is_a_players_uuid(bool playerUuid)
    {
        var literal = playerUuid ? _site.Players["petya"].ToString("N") : "Point & Click / Квест";
        var (_, vasya) = await QuestionAsync(RequestChoice($$"""{ "list": [{{JsonSerializer.Serialize(literal)}}, "другой вариант"] }"""));

        var option = (await MeAsync(vasya)).GetProperty("choice").GetProperty("options")[0];

        Assert.Equal(literal, option.GetProperty("id").GetString());
        Assert.Equal(literal, option.GetProperty("label").GetString());
    }

    [Fact]
    public async Task A_request_from_players_labels_each_player_option_with_the_players_name()
    {
        var (_, vasya) = await QuestionAsync(RequestChoice("""{ "from": "players" }"""));

        var options = (await MeAsync(vasya)).GetProperty("choice").GetProperty("options").EnumerateArray().ToArray();

        Assert.Equal(_site.Players.Values.Order(), options.Select(o => Guid.Parse(o.GetProperty("id").GetString()!)).Order());
        foreach (var (login, playerId) in _site.Players)
        {
            var option = Assert.Single(options, o => Guid.Parse(o.GetProperty("id").GetString()!) == playerId);
            Assert.Equal(login, option.GetProperty("label").GetString());
        }
    }

    [Fact]
    public async Task A_request_from_games_labels_each_game_option_with_the_pool_title()
    {
        var (_, vasya) = await QuestionAsync(RequestChoice("""{ "from": "games" }"""));
        var pool = await ApiCalls.OkAsync(await vasya.GetAsync("/api/pool", Ct));
        var titles = pool.EnumerateArray().ToDictionary(g => g.GetProperty("id").GetGuid(), g => g.GetProperty("title").GetString());

        var options = (await MeAsync(vasya)).GetProperty("choice").GetProperty("options").EnumerateArray().ToArray();

        Assert.Equal(titles.Keys.Order(), options.Select(o => Guid.Parse(o.GetProperty("id").GetString()!)).Order());
        Assert.All(options, o => Assert.Equal(titles[Guid.Parse(o.GetProperty("id").GetString()!)], o.GetProperty("label").GetString()));
    }

    [Fact]
    public async Task A_category_named_as_a_player_uuid_keeps_the_category_text_as_its_label()
    {
        var admin = await _site.SignedInAsync("admin");
        var category = _site.Players["petya"].ToString("N");
        await ApiCalls.OkAsync(await admin.PutAsJsonAsync(
            $"/api/admin/pool/categories/{category}", new { commandId = Guid.NewGuid(), weight = 1 }, Ct));
        var (_, vasya) = await QuestionAsync(RequestChoice("""{ "from": "categories" }"""));

        var option = Assert.Single(
            (await MeAsync(vasya)).GetProperty("choice").GetProperty("options").EnumerateArray(), o => o.GetProperty("id").GetString() == category);

        Assert.Equal(category, option.GetProperty("label").GetString());
    }

    [Fact]
    public async Task A_chosen_move_direction_carries_its_semantic_direction_for_translation()
    {
        var (_, vasya) = await QuestionAsync(Crossroads);

        var options = (await MeAsync(vasya)).GetProperty("choice").GetProperty("options").EnumerateArray().ToArray();

        Assert.Equal(["forward", "back"], options.Select(o => o.GetProperty("id").GetString()));
        Assert.All(options, o => Assert.Equal(o.GetProperty("id").GetString(), o.GetProperty("direction").GetString()));
    }

    [Fact]
    public async Task Literal_forward_and_back_are_labels_without_move_direction_semantics()
    {
        var (_, vasya) = await QuestionAsync(RequestChoice("""{ "list": ["forward", "back"] }"""));

        var options = (await MeAsync(vasya)).GetProperty("choice").GetProperty("options").EnumerateArray().ToArray();

        Assert.All(options, o =>
        {
            Assert.Equal(o.GetProperty("id").GetString(), o.GetProperty("label").GetString());
            if (o.TryGetProperty("direction", out var direction))
            {
                Assert.Equal(JsonValueKind.Null, direction.ValueKind);
            }
        });
    }

    [Fact]
    public async Task A_chosen_object_option_names_each_own_instance_without_offering_another_players_instance()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Sacrifice, 5, Orange);
        foreach (var login in new[] { "vasya", "vasya", "petya" })
        {
            await ApiCalls.PostOkAsync(
                admin, $"{Admin}/players/{_site.Players[login]}/inventory", new { commandId = Guid.NewGuid(), giveObjectId = "orange", comment = "Подарок" });
        }

        var inventory = await ApiCalls.OkAsync(await vasya.GetAsync($"{Season}/economy", Ct));
        var instances = inventory.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("instanceId").GetGuid()).Order().ToArray();
        Assert.Equal(2, instances.Length);
        await DropAsync(vasya);

        var options = (await MeAsync(vasya)).GetProperty("choice").GetProperty("options").EnumerateArray().ToArray();

        Assert.Equal(instances, options.Select(o => Guid.Parse(o.GetProperty("id").GetString()!)).Order());
        Assert.All(options, o => Assert.Equal("Апельсин", o.GetProperty("label").GetString()));
    }

    private async Task<(HttpClient Admin, HttpClient Vasya)> QuestionAsync(string badEvent)
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, badEvent);
        await DropAsync(vasya);
        return (admin, vasya);
    }

    private static string RequestChoice(string options) => $$"""
        { "id": "ui-choice", "kind": "event", "name": "Вопрос",
          "description": "Выбери вариант.", "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "self" }, "actions": [
            { "type": "requestChoice", "prompt": "Что выбираешь?", "options": {{options}} },
            { "type": "changeResource", "resource": "coins", "amount": 1 } ] } }
        """;

    private const string Gift = """
        { "id": "gift", "kind": "event", "name": "Подарок",
          "description": "Выбери другого игрока: он получит 2 монетки.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "chosen", "excludeSelf": true }, "actions": [{ "type": "changeResource", "resource": "coins", "amount": 2 }] } }
        """;

    private const string Crossroads = """
        { "id": "crossroads", "kind": "event", "name": "Развилка",
          "description": "Шагни на 1 клетку вперёд или назад — на твой выбор.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "self" }, "actions": [{ "type": "move", "steps": 1, "direction": "chosen" }] } }
        """;

    private const string Sacrifice = """
        { "id": "sacrifice", "kind": "event", "name": "Жертва",
          "description": "Выбери свой предмет, который потеряешь.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "self" }, "actions": [{ "type": "takeObject", "mode": "destroy", "target": "self", "pick": "chosen" }] } }
        """;

    private const string Orange = """
        { "id": "orange", "kind": "item", "name": "Апельсин", "description": "Иди на 1 клетку вперёд.",
          "rarity": "common", "price": 10, "window": "anytime", "hostile": false, "stackable": true,
          "effect": { "target": { "selector": "self" }, "actions": [{ "type": "move", "steps": 1 }] } }
        """;

    private const string DoubleFortune = """
        { "id": "double-fortune", "kind": "item", "name": "Двойное предсказание",
          "description": "Вытяни плохой и хороший ивенты.",
          "rarity": "common", "price": 10, "window": "anytime", "hostile": false,
          "effect": { "target": { "selector": "self" }, "actions": [
            { "type": "drawEvent", "deck": "bad" },
            { "type": "drawEvent", "deck": "good" } ] } }
        """;
}

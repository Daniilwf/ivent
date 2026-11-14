using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetVips;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// A season that plays events, for the API tests of task 5.18: the rules switched on (<c>features.events</c>, items, the
/// graph map, three dice a completion, <c>events.irlProof.maxFiles</c>), a long chain map, and a pack whose bad deck — the
/// one a drop draws from (SPEC «Ивенты»: «дроп (плохой обязателен)») — holds the one event the test needs. The events are
/// the examples of CONTENT.md «Примеры ивентов» as they are written there.
/// </summary>
internal static class EventSeason
{
    public const string Karaoke = """
        { "id": "karaoke", "kind": "event", "name": "Караоке",
          "description": "Спой песню на выбор остальных и скинь видео в галерею.",
          "rarity": "common", "price": null, "hostile": false, "manual": true, "proof": "media" }
        """;

    public const string NotEvenSweating = """
        { "id": "not-even-sweating", "kind": "event", "name": "Даже не вспотел",
          "description": "Следующую игру нужно пройти на сложной сложности или выше.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "self" }, "actions": [{ "type": "modifyNextRoll", "runCondition": { "difficultyAtLeast": "hard" } }] } }
        """;

    public const string CoinOrStep = """
        { "id": "coin-or-step", "kind": "event", "name": "Монетки или шаг",
          "description": "Выбери: 3 монетки или шаг вперёд.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": {
            "target": { "selector": "self" },
            "outcomes": {
              "choice": { "prompt": "Что берёшь?", "options": { "list": ["монетки", "шаг"] } },
              "cases": [
                { "option": "монетки", "actions": [{ "type": "changeResource", "resource": "coins", "amount": 3 }] },
                { "option": "шаг", "actions": [{ "type": "move", "steps": 1 }] }
              ]
            }
          } }
        """;

    public const string Tailwind = """
        { "id": "tailwind", "kind": "event", "name": "Попутный ветер",
          "description": "В следующем броске за прохождение на один кубик больше.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "self" }, "actions": [{ "type": "modifyDice", "when": "next", "stage": "count", "value": 1 }] } }
        """;

    /// <summary>A manual event with no proof: the player resolves it himself — not an IRL one.</summary>
    public const string Toast = """
        { "id": "toast", "kind": "event", "name": "Тост",
          "description": "Скажи тост в чате.",
          "rarity": "common", "price": null, "hostile": false, "manual": true }
        """;

    private const string Gamble = """
        { "id": "gamble", "kind": "event", "name": "Рискнёшь?",
          "description": "Кинь d6: на 1–3 назад на 3 клетки, на 4–6 вперёд на 3.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "self" }, "outcomes": { "roll": "1d6", "cases": [
            { "from": 1, "to": 3, "actions": [{ "type": "move", "steps": -3 }] },
            { "from": 4, "to": 6, "actions": [{ "type": "move", "steps": 3 }] } ] } } }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string Season => $"/api/seasons/{SiteFactory.SeasonId}";

    public static string Admin => $"/api/admin/seasons/{SiteFactory.SeasonId}";

    /// <summary>The rules on, the long map, and a pack whose bad deck holds <paramref name="badEvent"/>.</summary>
    public static async Task SetUpAsync(HttpClient admin, string badEvent, int maxFiles = 5)
    {
        await SwitchEventsAsync(admin, on: true, maxFiles);
        await ApiCalls.PostOkAsync(admin, $"{Admin}/map/publish", new { commandId = Guid.NewGuid(), map = LongMap(), comment = "Длинная карта" });
        await ApiCalls.PostOkAsync(admin, $"/api/test/seasons/{SiteFactory.SeasonId}/content", Pack(badEvent));
    }

    /// <summary>The objects and decks of <see cref="SetUpAsync"/> as the test endpoint takes them.</summary>
    public static object Pack(string badEvent)
    {
        var badId = JsonNode.Parse(badEvent)!["id"]!.GetValue<string>();
        var objects = new List<string> { badEvent, Gamble };
        if (badId != "tailwind")
        {
            objects.Add(Tailwind);
        }

        string[] wheels =
        [
            $$"""{ "id": "bad", "name": "Плохие ивенты", "deck": "bad", "entries": [{ "objectId": "{{badId}}", "weight": 1 }] }""",
            """{ "id": "good", "name": "Хорошие ивенты", "deck": "good", "entries": [{ "objectId": "tailwind", "weight": 1 }] }""",
            """{ "id": "risky", "name": "Рискованные ивенты", "deck": "risky", "entries": [{ "objectId": "gamble", "weight": 1 }] }""",
        ];
        return new { objects, wheels, comment = "Ивенты" };
    }

    /// <summary>Switches <c>features.events</c>; the rest as <see cref="SetUpAsync"/> wants it. Returns the answer.</summary>
    public static async Task<HttpResponseMessage> TrySwitchEventsAsync(HttpClient admin, bool on, int maxFiles = 5)
    {
        using var current = JsonDocument.Parse(await admin.GetStringAsync($"{Season}/rules", Ct));
        var ruleset = JsonNode.Parse(current.RootElement.GetProperty("ruleset").GetRawText())!;
        ruleset["features"]!["mapMode"] = "graph";
        ruleset["features"]!["items"] = true;
        ruleset["features"]!["events"] = on;
        ruleset["events"] = new JsonObject { ["defaultDeck"] = "risky", ["irlProof"] = new JsonObject { ["maxFiles"] = maxFiles } };
        ruleset["reward"]!["diceCount"]!["min"] = 3;
        ruleset["reward"]!["diceCount"]!["max"] = 3;
        return await admin.PutAsJsonAsync(
            $"{Admin}/rules",
            new { commandId = Guid.NewGuid(), expectedVersion = current.RootElement.GetProperty("version").GetInt32(), ruleset },
            Ct);
    }

    public static async Task SwitchEventsAsync(HttpClient admin, bool on, int maxFiles = 5)
    {
        var response = await TrySwitchEventsAsync(admin, on, maxFiles);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The player rolls, starts and drops: the drop draws the bad deck's event (D-504, D-553).</summary>
    public static async Task DropAsync(HttpClient player)
    {
        await ApiCalls.PostOkAsync(player, $"{Season}/roll");
        await ApiCalls.PostOkAsync(player, $"{Season}/start");
        await ApiCalls.PostOkAsync(player, $"{Season}/drop");
    }

    /// <summary>The player rolls, starts and completes on <paramref name="difficulty"/>; returns the run.</summary>
    public static async Task<Guid> CompleteAsync(HttpClient player, string difficulty = "normal")
    {
        await ApiCalls.PostOkAsync(player, $"{Season}/roll");
        await ApiCalls.PostOkAsync(player, $"{Season}/start");
        await ApiCalls.PostOkAsync(player, $"{Season}/complete", new { commandId = Guid.NewGuid(), difficulty });
        return (await MeAsync(player)).GetProperty("lastCompleted").GetProperty("id").GetGuid();
    }

    /// <summary>The one manual effect the player has, that the drop left.</summary>
    public static async Task<JsonElement> SingleEffectAsync(HttpClient player) =>
        Assert.Single((await MeAsync(player)).GetProperty("manualEffects").EnumerateArray());

    public static async Task<JsonElement> MeAsync(HttpClient player) =>
        (await ApiCalls.OkAsync(await player.GetAsync(Season, Ct))).GetProperty("me");

    public static async Task<string> RawSeasonAsync(HttpClient client) => await client.GetStringAsync(Season, Ct);

    /// <summary>An own picture through the site's upload (D-108, D-116), as the proof of a run takes it.</summary>
    public static async Task<Guid> UploadAsync(HttpClient client)
    {
        using var grey = Image.Black(40, 30, bands: 3) + 70;
        using var picture = grey.Cast(Enums.BandFormat.Uchar);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Guid.NewGuid().ToString()), "commandId");
        var file = new ByteArrayContent(picture.PngsaveBuffer());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "karaoke.png");
        var response = await client.PostAsync("/api/files", form, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>A 409 with the engine's (or the site's) code.</summary>
    public static async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Conflict, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(code, doc.RootElement.GetProperty("code").GetString());
    }

    /// <summary>Refused as bad input (400) or by a rule (409) — the test only needs that nothing happened.</summary>
    public static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(
            response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Conflict,
            $"{(int)response.StatusCode}: {body}");
    }

    public static async Task<List<string>> TypesAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. doc.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()!)];
    }

    public static async Task<HttpResponseMessage> PostOkAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return response;
    }

    /// <summary>start → c1 → … → c40: a chain long enough that no throw of the tests reaches an end.</summary>
    private static JsonObject LongMap()
    {
        var ids = new List<string> { "start" };
        ids.AddRange(Enumerable.Range(1, 40).Select(i => $"c{i}"));
        ids.Add("finish");
        var cells = new JsonArray();
        var edges = new JsonArray();
        for (var i = 0; i < ids.Count; i++)
        {
            var type = ids[i] switch { "start" => "start", "finish" => "finish", _ => "empty" };
            cells.Add(new JsonObject { ["id"] = ids[i], ["type"] = type, ["x"] = i * 100, ["y"] = 100 });
            if (i > 0)
            {
                edges.Add(new JsonObject { ["from"] = ids[i - 1], ["to"] = ids[i], ["isDefaultForward"] = true, ["isPrimaryBackward"] = true });
            }
        }

        return new JsonObject { ["cells"] = cells, ["edges"] = edges, ["zones"] = new JsonArray() };
    }
}

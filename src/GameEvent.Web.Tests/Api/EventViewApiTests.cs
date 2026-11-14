using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using static GameEvent.Web.Tests.Api.EventSeason;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Events in the season view and the admin's content (task 5.18, SPEC «Ивенты»: «ивенты копятся», D-505, D-823): the
/// events waiting to apply to my next steps are in my view (<c>me.waitingEvents</c>) until they apply; the admin's
/// content editor says whether events are on (<c>eventsEnabled</c>) and gives back the events and decks it published;
/// the admin's list of manual effects marks the IRL ones and lets the admin close one «не применимо» (D-683).
/// </summary>
public sealed class EventViewApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Editor => $"{Admin}/content";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- Accumulated events in my view ----

    [Fact]
    public async Task A_drawn_event_waiting_for_my_next_throw_is_in_my_view_until_it_applies()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Tailwind);
        await DropAsync(vasya);

        var waiting = Assert.Single((await MeAsync(vasya)).GetProperty("waitingEvents").EnumerateArray());
        Assert.Equal(("tailwind", "Попутный ветер", "bad"), (
            waiting.GetProperty("objectId").GetString(), waiting.GetProperty("name").GetString(), waiting.GetProperty("kind").GetString()));
        Assert.StartsWith("В следующем броске", waiting.GetProperty("description").GetString(), StringComparison.Ordinal);

        // The next completion throws one die more and the event is spent
        await CompleteAsync(vasya);
        var me = await MeAsync(vasya);
        Assert.Equal(4, me.GetProperty("lastCompleted").GetProperty("dice").GetArrayLength());
        Assert.Empty(me.GetProperty("waitingEvents").EnumerateArray());
    }

    [Fact]
    public async Task My_waiting_events_are_mine_alone_and_empty_before_any_draw()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        await SetUpAsync(admin, Tailwind);
        Assert.Empty((await MeAsync(vasya)).GetProperty("waitingEvents").EnumerateArray());

        await DropAsync(vasya);

        Assert.Empty((await MeAsync(petya)).GetProperty("waitingEvents").EnumerateArray());
    }

    [Fact]
    public async Task With_events_off_the_view_has_no_waiting_events_or_conditions()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SwitchEventsAsync(admin, on: false);

        var me = await MeAsync(vasya);

        Assert.Empty(me.GetProperty("waitingEvents").EnumerateArray());
        Assert.Empty(me.GetProperty("waitingConditions").EnumerateArray());
    }

    // ---- The admin's content: events and decks (on top of 4.10, 4.12) ----

    [Fact]
    public async Task The_content_editor_says_whether_events_are_on()
    {
        var admin = await _site.SignedInAsync("admin");

        await SwitchEventsAsync(admin, on: false);
        Assert.False((await ApiCalls.OkAsync(await admin.GetAsync(Editor, Ct))).GetProperty("eventsEnabled").GetBoolean());

        await SwitchEventsAsync(admin, on: true);
        Assert.True((await ApiCalls.OkAsync(await admin.GetAsync(Editor, Ct))).GetProperty("eventsEnabled").GetBoolean());
    }

    [Fact]
    public async Task The_editor_gives_back_the_published_events_and_decks_as_content_json()
    {
        var admin = await _site.SignedInAsync("admin");
        await SetUpAsync(admin, Karaoke);

        var pack = (await ApiCalls.OkAsync(await admin.GetAsync(Editor, Ct))).GetProperty("pack");

        var karaoke = Assert.Single(pack.GetProperty("objects").EnumerateArray(), o => o.GetProperty("id").GetString() == "karaoke");
        Assert.Equal(("event", true, "media"), (
            karaoke.GetProperty("kind").GetString(), karaoke.GetProperty("manual").GetBoolean(), karaoke.GetProperty("proof").GetString()));
        var bad = Assert.Single(pack.GetProperty("wheels").EnumerateArray(), w => w.GetProperty("id").GetString() == "bad");
        Assert.Equal("bad", bad.GetProperty("deck").GetString());
        Assert.Equal("karaoke", Assert.Single(bad.GetProperty("entries").EnumerateArray()).GetProperty("objectId").GetString());
    }

    [Fact]
    public async Task The_editors_check_names_a_deck_that_draws_an_item()
    {
        var admin = await _site.SignedInAsync("admin");
        await SwitchEventsAsync(admin, on: true);
        var pack = new JsonObject
        {
            ["objects"] = new JsonArray(
                JsonNode.Parse(Tailwind),
                JsonNode.Parse("""{ "id": "orange", "kind": "item", "name": "Апельсин", "description": "Ничего.", "rarity": "common", "price": 1, "hostile": false, "manual": true }""")),
            ["wheels"] = new JsonArray(JsonNode.Parse("""{ "id": "good", "name": "Хорошие", "deck": "good", "entries": [{ "objectId": "orange", "weight": 1 }] }""")),
        };

        var check = await ApiCalls.OkAsync(await admin.PostAsJsonAsync($"{Editor}/check", pack, Ct));

        Assert.False(check.GetProperty("valid").GetBoolean());
        Assert.Contains(check.GetProperty("problems").EnumerateArray(), p => p.GetProperty("path").GetString()!.StartsWith("wheels[0]", StringComparison.Ordinal));
    }

    // ---- The admin's list of manual effects and IRL ones ----

    [Fact]
    public async Task The_admins_effect_list_marks_an_irl_effect_and_the_admin_closes_it_not_applicable()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Karaoke);
        await DropAsync(vasya);

        var effect = Assert.Single((await ApiCalls.OkAsync(await admin.GetAsync($"{Admin}/effects", Ct))).EnumerateArray());
        Assert.Equal("karaoke", effect.GetProperty("objectId").GetString());
        Assert.True(effect.GetProperty("irl").GetBoolean());

        await PostOkAsync(
            admin,
            $"{Admin}/effects/{effect.GetProperty("id").GetGuid()}/resolve",
            new { commandId = Guid.NewGuid(), outcome = "notApplicable", comment = "Караоке не будет" });
        Assert.Empty((await MeAsync(vasya)).GetProperty("manualEffects").EnumerateArray());
    }

    [Fact]
    public async Task Only_the_admin_reads_the_content_editor()
    {
        foreach (var login in new[] { "vasya", "zritel" })
        {
            var client = await _site.SignedInAsync(login);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Editor, Ct)).StatusCode);
        }
    }
}

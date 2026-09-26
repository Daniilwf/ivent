using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The wheel of the current roll in the season view (D-136, H3): the categories that were on it, the pick, the misses
/// with the game and who holds it, and a sequence that tells one roll from the next. Only my own offer carries it.
/// </summary>
public sealed class WheelRollApiTests : IAsyncLifetime
{
    // Always the first: the wheel's first category, the draw's first game by id — Вася takes the first game, so Петя's
    // draw meets it before any other (the engine sorts the pool by id)
    private readonly SiteFactory _site = new() { Random = new AlwaysFirst() };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Season => $"/api/seasons/{SiteFactory.SeasonId}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_roll_shows_its_wheel_and_pick()
    {
        var vasya = await _site.SignedInAsync("vasya");

        await PostOkAsync(vasya, $"{Season}/roll");

        var roll = (await MeAsync(vasya)).GetProperty("roll");
        Assert.Equal("Horror", roll.GetProperty("category").GetString());
        Assert.Equal(["Horror"], roll.GetProperty("sectors").EnumerateArray().Select(c => c.GetString()));
        Assert.Empty(roll.GetProperty("misses").EnumerateArray());
        Assert.True(roll.GetProperty("sequence").GetInt64() > 0);
    }

    [Fact]
    public async Task Misses_name_the_game_and_who_holds_it()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        var completed = await PlayOneAsync(vasya);
        await PostOkAsync(vasya, $"{Season}/roll");
        var held = (await MeAsync(vasya)).GetProperty("offer").GetProperty("title").GetString();

        await PostOkAsync(petya, $"{Season}/roll");

        // The draw meets the completed game first, then the held one, and takes the third
        var misses = (await MeAsync(petya)).GetProperty("roll").GetProperty("misses").EnumerateArray().ToList();
        Assert.Equal(
            [(completed, "completedInSeason", "vasya"), (held, "beingPlayed", "vasya")],
            misses.Select(m => (m.GetProperty("game").GetString(), m.GetProperty("reason").GetString(), m.GetProperty("player").GetString())));
        // SPEC «Уже прошёл Вася, 12.10»: the completed miss says when; the one being played has no date
        Assert.Equal(_site.Clock.UtcNow, misses[0].GetProperty("at").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, misses[1].GetProperty("at").ValueKind);
    }

    [Fact]
    public async Task A_new_roll_after_already_played_has_a_new_sequence()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, $"{Season}/roll");
        var first = await MeAsync(vasya);

        await PostOkAsync(vasya, $"{Season}/already-played", new { commandId = Guid.NewGuid(), gameId = first.GetProperty("offer").GetProperty("id").GetGuid() });

        var second = (await MeAsync(vasya)).GetProperty("roll");
        Assert.True(second.GetProperty("sequence").GetInt64() > first.GetProperty("roll").GetProperty("sequence").GetInt64());
    }

    [Fact]
    public async Task A_choice_of_games_carries_its_wheel_too()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        await ChangeRulesAsync(admin, ruleset => ruleset["roll"]!["choiceCount"] = 2);

        await PostOkAsync(vasya, $"{Season}/roll");

        var me = await MeAsync(vasya);
        Assert.Equal(JsonValueKind.Object, me.GetProperty("choice").ValueKind);
        var roll = me.GetProperty("roll");
        Assert.Equal("Horror", roll.GetProperty("category").GetString());
        Assert.Equal(["Horror"], roll.GetProperty("sectors").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task No_wheel_after_the_deadline()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, $"{Season}/roll");
        await _site.SendAsync(new GameEvent.Engine.Seasons.SetSeasonDeadline(_site.Clock.UtcNow.AddHours(1)));

        _site.Clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal(JsonValueKind.Null, (await MeAsync(vasya)).GetProperty("roll").ValueKind);
    }

    [Fact]
    public async Task No_wheel_while_idle_or_playing()
    {
        var vasya = await _site.SignedInAsync("vasya");
        Assert.Equal(JsonValueKind.Null, (await MeAsync(vasya)).GetProperty("roll").ValueKind);

        await PostOkAsync(vasya, $"{Season}/roll");
        await PostOkAsync(vasya, $"{Season}/start");

        Assert.Equal(JsonValueKind.Null, (await MeAsync(vasya)).GetProperty("roll").ValueKind);
    }

    [Fact]
    public async Task My_wheel_stays_when_another_player_rolls_after_me()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        await PostOkAsync(vasya, $"{Season}/roll");
        var mine = (await MeAsync(vasya)).GetProperty("roll").GetProperty("sequence").GetInt64();

        await PostOkAsync(petya, $"{Season}/roll");

        Assert.Equal(mine, (await MeAsync(vasya)).GetProperty("roll").GetProperty("sequence").GetInt64());
    }

    [Fact]
    public async Task The_admins_tech_reroll_brings_a_wheel_too()
    {
        // The roll after the admin's tech reroll is the admin's command, the offer is mine: the wheel follows the offer
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(vasya, $"{Season}/roll");
        await PostOkAsync(vasya, $"{Season}/start");
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(100);

        await PostOkAsync(
            admin,
            $"/api/admin/seasons/{SiteFactory.SeasonId}/players/{_site.Players["vasya"]}/tech-reroll",
            new { commandId = Guid.NewGuid(), reason = "paidUnavailable", comment = "Игру убрали из Steam" });

        var me = await MeAsync(vasya);
        Assert.Equal(JsonValueKind.Object, me.GetProperty("offer").ValueKind);
        Assert.Equal("Horror", me.GetProperty("roll").GetProperty("category").GetString());
    }

    [Fact]
    public async Task Undoing_a_new_roll_brings_back_the_earlier_roll_and_its_wheel()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(vasya, $"{Season}/roll");
        var first = await MeAsync(vasya);
        var again = Guid.NewGuid();
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(5);
        await PostOkAsync(vasya, $"{Season}/already-played", new { commandId = again, gameId = first.GetProperty("offer").GetProperty("id").GetGuid() });

        await PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/undo", new { commandId = Guid.NewGuid(), targetCommandId = again, comment = "Ошибся кнопкой" });

        var back = await MeAsync(vasya);
        Assert.Equal(first.GetProperty("offer").GetProperty("id").GetGuid(), back.GetProperty("offer").GetProperty("id").GetGuid());
        Assert.Equal(first.GetProperty("roll").GetProperty("sequence").GetInt64(), back.GetProperty("roll").GetProperty("sequence").GetInt64());
    }

    [Fact]
    public async Task Another_players_roll_is_not_in_my_view()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");

        await PostOkAsync(vasya, $"{Season}/roll");

        Assert.Equal(JsonValueKind.Null, (await MeAsync(petya)).GetProperty("roll").ValueKind);
    }

    // ---- Helpers ----

    private static async Task ChangeRulesAsync(HttpClient admin, Action<JsonNode> change)
    {
        using var current = JsonDocument.Parse(await admin.GetStringAsync($"{Season}/rules", Ct));
        var ruleset = JsonNode.Parse(current.RootElement.GetProperty("ruleset").GetRawText())!;
        change(ruleset);
        var response = await admin.PutAsJsonAsync(
            $"/api/admin/seasons/{SiteFactory.SeasonId}/rules",
            new { commandId = Guid.NewGuid(), expectedVersion = current.RootElement.GetProperty("version").GetInt32(), ruleset },
            Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Rolls, starts and completes one game; returns its title</summary>
    private static async Task<string> PlayOneAsync(HttpClient player)
    {
        await PostOkAsync(player, $"{Season}/roll");
        var title = (await MeAsync(player)).GetProperty("offer").GetProperty("title").GetString()!;
        await PostOkAsync(player, $"{Season}/start");
        await PostOkAsync(player, $"{Season}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });
        return title;
    }

    private static async Task<JsonElement> MeAsync(HttpClient player) =>
        (await OkAsync(await player.GetAsync(Season, Ct))).GetProperty("me");

    private static async Task PostOkAsync(HttpClient client, string url, object? body = null)
    {
        var response = await client.PostAsJsonAsync(url, body ?? new { commandId = Guid.NewGuid() }, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private sealed class AlwaysFirst : GameEvent.Engine.Kernel.IRandomSource
    {
        public int NextInt(int minInclusive, int maxExclusive) => minInclusive;
    }
}

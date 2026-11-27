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
        var heldView = await MeAsync(vasya);
        var held = heldView.GetProperty("offer").GetProperty("title").GetString();
        await PostOkAsync(vasya, $"{Season}/roll/reveal", new
        {
            commandId = Guid.NewGuid(),
            revealId = heldView.GetProperty("roll").GetProperty("revealId").GetGuid(),
        });

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

    [Fact]
    public async Task The_result_is_private_in_the_feed_and_pool_until_the_owner_shows_it()
    {
        var owner = await _site.SignedInAsync("vasya");
        var other = await _site.SignedInAsync("petya");
        var spectator = await _site.SignedInAsync("zritel");
        var rollCommand = Guid.NewGuid();
        await PostOkAsync(owner, $"{Season}/roll", new { commandId = rollCommand });

        var me = await MeAsync(owner);
        var game = me.GetProperty("offer").GetProperty("id").GetGuid();
        var roll = me.GetProperty("roll");
        var reveal = roll.GetProperty("revealId").GetGuid();
        Assert.False(roll.GetProperty("revealed").GetBoolean());
        Assert.Contains(roll.GetProperty("wheelGames").EnumerateArray(), item => item.GetProperty("id").GetGuid() == game);

        foreach (var reader in new[] { owner, other, spectator })
        {
            var feed = await OkAsync(await reader.GetAsync($"{Season}/feed", Ct));
            var entry = feed.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("commandId").GetGuid() == rollCommand);
            Assert.Equal("game-roll-hidden", entry.GetProperty("type").GetString());
            Assert.False(entry.GetProperty("data").TryGetProperty("gameId", out _));
            Assert.DoesNotContain(feed.GetProperty("games").EnumerateArray(), item => item.GetProperty("id").GetGuid() == game);
        }

        var hiddenPool = await OkAsync(await spectator.GetAsync($"{Season}/games", Ct));
        Assert.DoesNotContain(hiddenPool.EnumerateArray(), item => item.GetProperty("gameId").GetGuid() == game);

        using var forbidden = await spectator.PostAsJsonAsync($"{Season}/roll/reveal", new { commandId = Guid.NewGuid(), revealId = reveal }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var stale = await other.PostAsJsonAsync($"{Season}/roll/reveal", new { commandId = Guid.NewGuid(), revealId = reveal }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var invalid = await owner.PostAsJsonAsync($"{Season}/roll/reveal", new { commandId = Guid.NewGuid(), revealId = Guid.Empty }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var command = new { commandId = Guid.NewGuid(), revealId = reveal };
        await PostOkAsync(owner, $"{Season}/roll/reveal", command);
        var repeated = await OkAsync(await owner.PostAsJsonAsync($"{Season}/roll/reveal", command, Ct));
        Assert.True(repeated.GetProperty("duplicate").GetBoolean());

        var shown = (await MeAsync(owner)).GetProperty("roll");
        Assert.True(shown.GetProperty("revealed").GetBoolean());
        Assert.Equal(roll.GetProperty("sequence").GetInt64(), shown.GetProperty("sequence").GetInt64());
        var visible = await OkAsync(await spectator.GetAsync($"{Season}/feed", Ct));
        var visibleEntry = visible.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("commandId").GetGuid() == rollCommand);
        Assert.Equal("game-rolled", visibleEntry.GetProperty("type").GetString());
        Assert.Equal(game, visibleEntry.GetProperty("data").GetProperty("gameId").GetGuid());
        var visiblePool = await OkAsync(await spectator.GetAsync($"{Season}/games", Ct));
        Assert.Contains(visiblePool.EnumerateArray(), item => item.GetProperty("gameId").GetGuid() == game);
    }

    [Fact]
    public async Task An_unshown_reservation_does_not_leak_through_another_players_wheel()
    {
        var owner = await _site.SignedInAsync("vasya");
        var other = await _site.SignedInAsync("petya");
        await PostOkAsync(owner, $"{Season}/roll");
        var reserved = (await MeAsync(owner)).GetProperty("offer").GetProperty("id").GetGuid();

        await PostOkAsync(other, $"{Season}/roll");

        var me = await MeAsync(other);
        Assert.NotEqual(reserved, me.GetProperty("offer").GetProperty("id").GetGuid());
        var roll = me.GetProperty("roll");
        Assert.DoesNotContain(roll.GetProperty("wheelGames").EnumerateArray(), item => item.GetProperty("id").GetGuid() == reserved);
        Assert.DoesNotContain(roll.GetProperty("misses").EnumerateArray(), item => item.GetProperty("gameId").GetGuid() == reserved);

        await ApiCalls.ShowCurrentRollAsync(owner);

        var reloaded = (await MeAsync(other)).GetProperty("roll");
        Assert.Equal(roll.GetProperty("sequence").GetInt64(), reloaded.GetProperty("sequence").GetInt64());
        Assert.Equal(roll.GetProperty("wheelGames").GetRawText(), reloaded.GetProperty("wheelGames").GetRawText());
    }

    [Fact]
    public async Task An_admin_discard_keeps_an_unshown_result_private_until_its_disclosure_time()
    {
        var owner = await _site.SignedInAsync("vasya");
        var spectator = await _site.SignedInAsync("zritel");
        var admin = await _site.SignedInAsync("admin");
        var rollCommand = Guid.NewGuid();
        await PostOkAsync(owner, $"{Season}/roll", new { commandId = rollCommand });
        var game = (await MeAsync(owner)).GetProperty("offer").GetProperty("id").GetGuid();
        await DiscardOfferAsync(admin);

        _site.Clock.Advance(TimeSpan.FromSeconds(119));
        var hidden = await OkAsync(await spectator.GetAsync($"{Season}/feed", Ct));
        var entries = hidden.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal("game-roll-hidden", entries.Single(entry => entry.GetProperty("commandId").GetGuid() == rollCommand).GetProperty("type").GetString());
        var discard = entries.Single(entry => entry.GetProperty("type").GetString() == "offer-discarded");
        Assert.False(discard.GetProperty("data").TryGetProperty("gameId", out _));
        Assert.DoesNotContain(hidden.GetProperty("games").EnumerateArray(), item => item.GetProperty("id").GetGuid() == game);

        _site.Clock.Advance(TimeSpan.FromSeconds(1));
        var due = await OkAsync(await spectator.GetAsync($"{Season}/feed", Ct));
        var disclosed = due.GetProperty("entries").EnumerateArray().Single(entry => entry.GetProperty("commandId").GetGuid() == rollCommand);
        Assert.Equal("game-rolled", disclosed.GetProperty("type").GetString());
        Assert.Equal(game, disclosed.GetProperty("data").GetProperty("gameId").GetGuid());
    }

    [Fact]
    public async Task Undoing_a_discard_and_its_roll_keeps_the_discard_private_until_disclosure_time()
    {
        var owner = await _site.SignedInAsync("vasya");
        var spectator = await _site.SignedInAsync("zritel");
        var admin = await _site.SignedInAsync("admin");
        var rollCommand = Guid.NewGuid();
        await PostOkAsync(owner, $"{Season}/roll", new { commandId = rollCommand });
        var game = (await MeAsync(owner)).GetProperty("offer").GetProperty("id").GetGuid();
        var discardCommand = Guid.NewGuid();
        await DiscardOfferAsync(admin, discardCommand);
        foreach (var target in new[] { discardCommand, rollCommand })
        {
            await PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/undo", new
            {
                commandId = Guid.NewGuid(),
                targetCommandId = target,
                comment = "Отменить правку",
            });
        }

        _site.Clock.Advance(TimeSpan.FromSeconds(119));
        var hidden = await OkAsync(await spectator.GetAsync($"{Season}/feed", Ct));
        var entries = hidden.GetProperty("entries").EnumerateArray().ToArray();
        var roll = Assert.Single(entries, entry => entry.GetProperty("commandId").GetGuid() == rollCommand);
        Assert.True(roll.GetProperty("undone").GetBoolean());
        Assert.Equal("game-roll-hidden", roll.GetProperty("type").GetString());
        Assert.Equal(1000, roll.GetProperty("data").GetProperty("disclosureInMilliseconds").GetInt64());
        var discard = Assert.Single(entries, entry => entry.GetProperty("type").GetString() == "offer-discarded");
        Assert.True(discard.GetProperty("undone").GetBoolean());
        Assert.False(discard.GetProperty("data").TryGetProperty("gameId", out _));
        Assert.DoesNotContain(game.ToString("D"), hidden.GetRawText(), StringComparison.OrdinalIgnoreCase);

        _site.Clock.Advance(TimeSpan.FromSeconds(1));
        var disclosed = await OkAsync(await spectator.GetAsync($"{Season}/feed", Ct));
        var dueEntries = disclosed.GetProperty("entries").EnumerateArray().ToArray();
        var dueRoll = Assert.Single(dueEntries, entry => entry.GetProperty("commandId").GetGuid() == rollCommand);
        Assert.True(dueRoll.GetProperty("undone").GetBoolean());
        Assert.Equal("game-rolled", dueRoll.GetProperty("type").GetString());
        Assert.Equal(game, dueRoll.GetProperty("data").GetProperty("gameId").GetGuid());
        var dueDiscard = Assert.Single(dueEntries, entry => entry.GetProperty("type").GetString() == "offer-discarded");
        Assert.True(dueDiscard.GetProperty("undone").GetBoolean());
        Assert.Equal(game, dueDiscard.GetProperty("data").GetProperty("gameId").GetGuid());
    }

    [Fact]
    public async Task Discarding_a_hidden_offer_does_not_unmask_another_players_saved_miss()
    {
        var owner = await _site.SignedInAsync("vasya");
        var other = await _site.SignedInAsync("petya");
        var spectator = await _site.SignedInAsync("zritel");
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(owner, $"{Season}/roll");
        var hiddenGame = (await MeAsync(owner)).GetProperty("offer").GetProperty("id").GetGuid();
        var otherRoll = Guid.NewGuid();
        await PostOkAsync(other, $"{Season}/roll", new { commandId = otherRoll });
        await DiscardOfferAsync(admin);

        var wheel = (await MeAsync(other)).GetProperty("roll");
        Assert.Empty(wheel.GetProperty("misses").EnumerateArray());
        Assert.Equal(1, wheel.GetProperty("missCount").GetInt32());

        await ApiCalls.ShowCurrentRollAsync(other);
        var feed = await OkAsync(await spectator.GetAsync($"{Season}/feed", Ct));
        var shown = feed.GetProperty("entries").EnumerateArray().Single(entry => entry.GetProperty("commandId").GetGuid() == otherRoll);
        Assert.Equal("game-rolled", shown.GetProperty("type").GetString());
        Assert.Empty(shown.GetProperty("data").GetProperty("misses").EnumerateArray());
        Assert.DoesNotContain(feed.GetProperty("games").EnumerateArray(), item => item.GetProperty("id").GetGuid() == hiddenGame);
    }

    [Fact]
    public async Task A_repeated_game_at_the_same_clock_time_keeps_the_new_roll_identity_after_disclosure()
    {
        var owner = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(owner, $"{Season}/roll");
        var first = await MeAsync(owner);
        await DiscardOfferAsync(admin);
        await PostOkAsync(owner, $"{Season}/roll");
        var current = await MeAsync(owner);

        Assert.Equal(first.GetProperty("offer").GetProperty("id").GetGuid(), current.GetProperty("offer").GetProperty("id").GetGuid());
        var firstRoll = first.GetProperty("roll");
        var secondRoll = current.GetProperty("roll");
        Assert.Equal(firstRoll.GetProperty("rolledAt").GetDateTimeOffset(), secondRoll.GetProperty("rolledAt").GetDateTimeOffset());
        Assert.NotEqual(firstRoll.GetProperty("revealId").GetGuid(), secondRoll.GetProperty("revealId").GetGuid());
        Assert.True(secondRoll.GetProperty("sequence").GetInt64() > firstRoll.GetProperty("sequence").GetInt64());
        Assert.False(secondRoll.GetProperty("revealed").GetBoolean());

        await ApiCalls.ShowCurrentRollAsync(owner);

        var shown = (await MeAsync(owner)).GetProperty("roll");
        Assert.Equal(secondRoll.GetProperty("revealId").GetGuid(), shown.GetProperty("revealId").GetGuid());
        Assert.Equal(secondRoll.GetProperty("sequence").GetInt64(), shown.GetProperty("sequence").GetInt64());
        Assert.True(shown.GetProperty("revealed").GetBoolean());
    }

    // ---- Helpers ----

    private Task DiscardOfferAsync(HttpClient admin, Guid? commandId = null) =>
        PostOkAsync(admin, $"/api/admin/seasons/{SiteFactory.SeasonId}/players/{_site.Players["vasya"]}/adjust", new
        {
            commandId = commandId ?? Guid.NewGuid(),
            comment = "Снять предложение",
            discardOffer = true,
        });

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

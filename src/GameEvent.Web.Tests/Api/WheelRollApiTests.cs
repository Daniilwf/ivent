using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
        Assert.Equal(["Horror"], roll.GetProperty("wheel").EnumerateArray().Select(c => c.GetString()));
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
    public async Task No_wheel_while_idle_or_playing()
    {
        var vasya = await _site.SignedInAsync("vasya");
        Assert.Equal(JsonValueKind.Null, (await MeAsync(vasya)).GetProperty("roll").ValueKind);

        await PostOkAsync(vasya, $"{Season}/roll");
        await PostOkAsync(vasya, $"{Season}/start");

        Assert.Equal(JsonValueKind.Null, (await MeAsync(vasya)).GetProperty("roll").ValueKind);
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

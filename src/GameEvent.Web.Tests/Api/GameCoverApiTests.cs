using System.Text.Json;
using GameEvent.Engine.Rulesets;
using Microsoft.EntityFrameworkCore;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The pool's covers on the season screen and in the feed (D-222; a deferred finding of the H3 and H4 design reviews):
/// the offered game, the options of a choice, the active run, the last completed run and the feed's games carry the
/// cover's links when the pool game has one, and none otherwise. The files themselves are not needed: the links are.
/// </summary>
public sealed class GameCoverApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    /// <summary>The seed pool's covers: two games have one, Outlast has none.</summary>
    private readonly Dictionary<string, Guid?> _covers = new()
    {
        ["Silent Hill"] = Guid.Parse("c0000000-0000-0000-0000-000000000001"),
        ["Alan Wake"] = Guid.Parse("c0000000-0000-0000-0000-000000000002"),
        ["Outlast"] = null,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Season => $"/api/seasons/{SiteFactory.SeasonId}";

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        await using var db = _site.NewDb();
        foreach (var game in await db.Games.ToListAsync(Ct))
        {
            game.CoverFileId = _covers[game.Title];
        }

        await db.SaveChangesAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task The_offered_game_carries_its_cover()
    {
        var vasya = await _site.SignedInAsync("vasya");

        await PostOkAsync(vasya, $"{Season}/roll");

        var offer = (await MeAsync(vasya)).GetProperty("offer");
        AssertCover(offer);
    }

    [Fact]
    public async Task Each_option_of_a_choice_the_run_and_the_completed_run_carry_their_covers()
    {
        var rules = RulesetJson.Default();
        await _site.SendAsync(new ChangeRuleset(rules with { Roll = rules.Roll with { ChoiceCount = 3 } }, ExpectedVersion: null));
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, $"{Season}/roll");

        var choice = (await MeAsync(vasya)).GetProperty("choice");
        var options = choice.GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(3, options.Count);
        foreach (var option in options)
        {
            AssertCover(option.GetProperty("game"));
        }

        // Pick the one with a cover: the run and later the completed run show it too
        var picked = options.Single(o => o.GetProperty("game").GetProperty("title").GetString() == "Silent Hill");
        await PostOkAsync(vasya, $"{Season}/choose", new
        {
            commandId = Guid.NewGuid(),
            choiceId = choice.GetProperty("id").GetGuid(),
            optionId = picked.GetProperty("id").GetString(),
        });
        AssertCover((await MeAsync(vasya)).GetProperty("activeRun").GetProperty("game"));

        await PostOkAsync(vasya, $"{Season}/complete", new { commandId = Guid.NewGuid(), difficulty = "hard" });
        AssertCover((await MeAsync(vasya)).GetProperty("lastCompleted").GetProperty("game"));
    }

    [Fact]
    public async Task A_game_without_a_cover_has_none_on_the_screen()
    {
        var rules = RulesetJson.Default();
        await _site.SendAsync(new ChangeRuleset(rules with { Roll = rules.Roll with { ChoiceCount = 3 } }, ExpectedVersion: null));
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, $"{Season}/roll");

        var outlast = (await MeAsync(vasya)).GetProperty("choice").GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("game").GetProperty("title").GetString() == "Outlast");

        Assert.Equal(JsonValueKind.Null, outlast.GetProperty("game").GetProperty("cover").ValueKind);
    }

    [Fact]
    public async Task The_feeds_games_carry_their_covers_a_deleted_one_too()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(vasya, $"{Season}/roll");
        await PostOkAsync(vasya, $"{Season}/start");
        await PostOkAsync(vasya, $"{Season}/complete", new { commandId = Guid.NewGuid(), difficulty = "hard" });

        var games = await FeedGamesAsync(vasya);
        Assert.NotEmpty(games);
        foreach (var game in games)
        {
            AssertCover(game);
        }

        // A deleted game has no page for a player, but its cover stays, as on the season screen: covers are not secret
        // (any signed-in user reads a file by its id, D-222)
        var gameId = (await MeAsync(vasya)).GetProperty("lastCompleted").GetProperty("game").GetProperty("id").GetGuid();
        await PostOkAsync(admin, $"/api/admin/pool/{gameId}/delete", new { commandId = Guid.NewGuid(), reason = "Дубль" });

        var forPlayer = (await FeedGamesAsync(vasya)).Single(g => g.GetProperty("id").GetGuid() == gameId);
        Assert.False(forPlayer.GetProperty("hasPage").GetBoolean());
        AssertCover(forPlayer);
        AssertCover((await MeAsync(vasya)).GetProperty("lastCompleted").GetProperty("game"));
    }

    // ---- Helpers ----

    /// <summary>A game view's cover: the thumbnail and the file of the pool's cover, or null when the game has none.</summary>
    private void AssertCover(JsonElement game)
    {
        var expected = _covers[game.GetProperty("title").GetString()!];
        var cover = game.GetProperty("cover");
        if (expected is not { } id)
        {
            Assert.Equal(JsonValueKind.Null, cover.ValueKind);
            return;
        }

        Assert.Equal(id, cover.GetProperty("id").GetGuid());
        Assert.Equal($"/api/files/{id}/thumbnail", cover.GetProperty("thumbnailUrl").GetString());
        Assert.Equal($"/api/files/{id}", cover.GetProperty("url").GetString());
    }

    private static async Task<JsonElement> MeAsync(HttpClient client) =>
        (await OkAsync(await client.GetAsync(Season, Ct))).GetProperty("me");

    private static async Task<List<JsonElement>> FeedGamesAsync(HttpClient client) =>
        [.. (await OkAsync(await client.GetAsync($"{Season}/feed", Ct))).GetProperty("games").EnumerateArray()];
}

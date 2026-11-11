using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Accounts;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Lists of players by name follow the Russian alphabet (K1O-1, D-631): «Ёж» right after «Енот», not before «Аист». The
/// season screen (its players feed the proof form's «Свидетель» list), the feed and the admin's «Игроки» page; the token
/// colours follow the same order (D-150).
/// </summary>
public sealed class PlayerNameOrderTests : IAsyncLifetime
{
    private static readonly string[] s_names = ["Ёж", "Барсук", "заяц", "Енот", "Бобр", "Жук", "Ящер", "Аист"];

    // The Russian alphabet: Ё is its own letter right after Е, case does not come first
    private static readonly string[] s_ordered = ["Аист", "Барсук", "Бобр", "Енот", "Ёж", "Жук", "заяц", "Ящер"];

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync(withSeason: false);
        await _site.SendAsync(new CreateSeason(SiteFactory.SeasonId, "Тестовый сезон", RulesetJson.Default()));
        await _site.SendAsync(new ChangeSeasonStatus(SeasonStatus.Active));
        await using var db = _site.NewDb();
        var hash = db.Users.First(u => u.Login == "vasya").PasswordHash;
        var ids = new List<(Guid User, string Name)>();
        foreach (var (name, i) in s_names.Select((n, i) => (n, i)))
        {
            var user = new UserRecord
            {
                Id = Guid.NewGuid(),
                Login = $"p{i}",
                NormalizedLogin = $"p{i}",
                Name = name,
                PasswordHash = hash,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                Role = Role.Player,
                CreatedAt = _site.Clock.UtcNow,
            };
            db.Users.Add(user);
            ids.Add((user.Id, name));
        }

        await db.SaveChangesAsync(Ct);
        foreach (var (user, name) in ids)
        {
            await _site.SendAsync(new AddSeasonPlayer(Guid.NewGuid(), user, name));
        }
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task The_season_screen_lists_the_players_in_russian_alphabetical_order()
    {
        var player = await _site.SignedInAsync("p0");

        var season = await OkAsync(await player.GetAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));

        var players = season.GetProperty("players").EnumerateArray().ToList();
        Assert.Equal(s_ordered, players.Select(p => p.GetProperty("name").GetString()));
        Assert.Equal(Enumerable.Range(0, s_ordered.Length), players.Select(p => p.GetProperty("token").GetInt32()));
    }

    [Fact]
    public async Task The_feed_lists_the_players_in_russian_alphabetical_order()
    {
        var player = await _site.SignedInAsync("p0");

        var feed = await OkAsync(await player.GetAsync($"/api/seasons/{SiteFactory.SeasonId}/feed", Ct));

        Assert.Equal(s_ordered, feed.GetProperty("players").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task The_admins_players_page_lists_the_players_in_russian_alphabetical_order()
    {
        var admin = await _site.SignedInAsync("admin");

        var players = await OkAsync(await admin.GetAsync($"/api/admin/seasons/{SiteFactory.SeasonId}/players", Ct));

        Assert.Equal(s_ordered, players.EnumerateArray().Select(p => p.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task The_admins_map_lists_the_players_in_russian_alphabetical_order()
    {
        var admin = await _site.SignedInAsync("admin");

        var map = await OkAsync(await admin.GetAsync($"/api/admin/seasons/{SiteFactory.SeasonId}/map", Ct));

        Assert.Equal(s_ordered, map.GetProperty("players").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
    }
}

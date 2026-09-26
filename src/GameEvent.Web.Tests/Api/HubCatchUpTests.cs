using System.Collections.Concurrent;
using System.Net.Http.Json;
using GameEvent.Engine.Seasons;
using GameEvent.Web.Realtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Real-time updates without losses (E3, A14, D-122): watchers get a season's updates in log order, a client that lost
/// its connection resumes from the last sequence it saw and gets exactly what it missed, and the pool has a group of its
/// own. Accounts, files and bug reports are never broadcast.
/// </summary>
public sealed class HubCatchUpTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Two_watchers_get_every_update_in_log_order()
    {
        var (petya, petyaUpdates) = await WatcherAsync("petya");
        var (admin, adminUpdates) = await WatcherAsync("admin");
        var start = (await petya.InvokeAsync<SeasonJoin>(nameof(SeasonHub.Join), SiteFactory.SeasonId, Ct)).LastSequence;
        await admin.InvokeAsync<SeasonJoin>(nameof(SeasonHub.Join), SiteFactory.SeasonId, Ct);

        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, Url("roll"));
        await PostOkAsync(vasya, Url("start"));
        await PostOkAsync(vasya, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        var last = await LastSequenceAsync();

        foreach (var updates in new[] { petyaUpdates, adminUpdates })
        {
            var got = await UntilAsync(updates, last);
            Assert.Equal(start + 1, got[0].FromSequence);
            Assert.All(got.Zip(got.Skip(1)), pair => Assert.Equal(pair.First.ToSequence + 1, pair.Second.FromSequence));
            Assert.Equal(last, got[^1].ToSequence);
        }

        await petya.DisposeAsync();
        await admin.DisposeAsync();
    }

    [Fact]
    public async Task A_client_back_after_a_lost_connection_gets_exactly_what_it_missed()
    {
        var (first, _) = await WatcherAsync("petya");
        var seen = (await first.InvokeAsync<SeasonJoin>(nameof(SeasonHub.Join), SiteFactory.SeasonId, Ct)).LastSequence;
        await first.DisposeAsync();

        // While Петя is away, Вася rolls and starts, and the admin sets a deadline
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, Url("roll"));
        await PostOkAsync(vasya, Url("start"));
        await _site.SendAsync(new SetSeasonDeadline(_site.Clock.UtcNow.AddDays(7)));
        var last = await LastSequenceAsync();

        var (again, _) = await WatcherAsync("petya");
        var resumed = await again.InvokeAsync<SeasonJoin>(nameof(SeasonHub.Resume), SiteFactory.SeasonId, seen, Ct);

        Assert.False(resumed.Reload);
        Assert.Equal(last, resumed.LastSequence);
        Assert.Equal(3, resumed.Missed.Count);
        Assert.Equal(seen + 1, resumed.Missed[0].FromSequence);
        Assert.All(resumed.Missed.Zip(resumed.Missed.Skip(1)), pair => Assert.Equal(pair.First.ToSequence + 1, pair.Second.FromSequence));
        Assert.Equal(last, resumed.Missed[^1].ToSequence);
        Assert.Equal(["game-rolled"], resumed.Missed[0].Types);
        Assert.Contains("season-deadline-set", resumed.Missed[^1].Types);
        await again.DisposeAsync();
    }

    [Fact]
    public async Task A_resumed_client_gets_the_updates_that_follow()
    {
        var (connection, updates) = await WatcherAsync("petya");
        var join = await connection.InvokeAsync<SeasonJoin>(nameof(SeasonHub.Resume), SiteFactory.SeasonId, 0L, Ct);

        await PostOkAsync(await _site.SignedInAsync("vasya"), Url("roll"));

        var got = await UntilAsync(updates, join.LastSequence + 1);
        Assert.Equal(join.LastSequence + 1, got[^1].FromSequence);
        Assert.Equal(join.LastSequence, join.Missed[^1].ToSequence);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Nothing_missed_is_an_empty_list()
    {
        var (connection, _) = await WatcherAsync("petya");
        var last = await LastSequenceAsync();

        var resumed = await connection.InvokeAsync<SeasonJoin>(nameof(SeasonHub.Resume), SiteFactory.SeasonId, last, Ct);
        var joined = await connection.InvokeAsync<SeasonJoin>(nameof(SeasonHub.Join), SiteFactory.SeasonId, Ct);

        Assert.Equal((last, 0, false), (resumed.LastSequence, resumed.Missed.Count, resumed.Reload));
        Assert.Equal((last, 0, false), (joined.LastSequence, joined.Missed.Count, joined.Reload));
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Too_much_missed_says_reload_instead_of_listing_it()
    {
        var (connection, _) = await WatcherAsync("petya");
        var seen = await LastSequenceAsync();
        for (var i = 0; i <= SeasonHub.MaxMissedCommands; i++)
        {
            await _site.SendAsync(new SetSeasonDeadline(_site.Clock.UtcNow.AddDays(7).AddMinutes(i)));
        }

        var resumed = await connection.InvokeAsync<SeasonJoin>(nameof(SeasonHub.Resume), SiteFactory.SeasonId, seen, Ct);

        Assert.True(resumed.Reload);
        Assert.Empty(resumed.Missed);
        Assert.Equal(await LastSequenceAsync(), resumed.LastSequence);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Exactly_the_limit_of_missed_commands_is_still_listed()
    {
        var (connection, _) = await WatcherAsync("petya");
        var seen = await LastSequenceAsync();
        for (var i = 0; i < SeasonHub.MaxMissedCommands; i++)
        {
            await _site.SendAsync(new SetSeasonDeadline(_site.Clock.UtcNow.AddDays(7).AddMinutes(i)));
        }

        var resumed = await connection.InvokeAsync<SeasonJoin>(nameof(SeasonHub.Resume), SiteFactory.SeasonId, seen, Ct);

        Assert.False(resumed.Reload);
        Assert.Equal(SeasonHub.MaxMissedCommands, resumed.Missed.Count);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task A_client_ahead_of_the_log_is_told_to_reload()
    {
        // A season restored from a backup: the client saw more than the log holds now
        var (connection, _) = await WatcherAsync("petya");
        var last = await LastSequenceAsync();

        var resumed = await connection.InvokeAsync<SeasonJoin>(nameof(SeasonHub.Resume), SiteFactory.SeasonId, last + 50, Ct);

        Assert.Equal((last, 0, true), (resumed.LastSequence, resumed.Missed.Count, resumed.Reload));
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task An_unknown_season_has_an_empty_log()
    {
        var (connection, _) = await WatcherAsync("petya");

        var joined = await connection.InvokeAsync<SeasonJoin>(nameof(SeasonHub.Join), Guid.NewGuid(), Ct);
        var resumed = await connection.InvokeAsync<SeasonJoin>(nameof(SeasonHub.Resume), Guid.NewGuid(), 0L, Ct);

        Assert.Equal((0L, 0, false), (joined.LastSequence, joined.Missed.Count, joined.Reload));
        Assert.Equal((0L, 0, false), (resumed.LastSequence, resumed.Missed.Count, resumed.Reload));
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task The_global_log_is_not_a_season_to_join()
    {
        var (connection, _) = await WatcherAsync("petya");

        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<SeasonJoin>(nameof(SeasonHub.Join), Guid.Empty, Ct));
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<SeasonJoin>(nameof(SeasonHub.Resume), Guid.Empty, 0L, Ct));
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Pool_watchers_get_pool_changes_and_nothing_of_accounts()
    {
        var (poolWatcher, _) = await WatcherAsync("petya");
        var pool = new ConcurrentQueue<PoolUpdate>();
        poolWatcher.On<PoolUpdate>(SeasonHub.PoolUpdateMethod, pool.Enqueue);
        await poolWatcher.InvokeAsync(nameof(SeasonHub.JoinPool), Ct);
        var (seasonOnly, seasonUpdates) = await WatcherAsync("vasya");
        var strayPool = new ConcurrentQueue<PoolUpdate>();
        seasonOnly.On<PoolUpdate>(SeasonHub.PoolUpdateMethod, strayPool.Enqueue);
        await seasonOnly.InvokeAsync<SeasonJoin>(nameof(SeasonHub.Join), SiteFactory.SeasonId, Ct);

        var admin = await _site.SignedInAsync("admin");
        (await admin.PostAsJsonAsync("/api/admin/accounts", new { commandId = Guid.NewGuid(), login = "kolya", name = "Коля", role = "player" }, Ct)).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/pool", new { commandId = Guid.NewGuid(), title = "Dead Space", tags = new[] { "Horror" }, hours = 10 }, Ct)).EnsureSuccessStatusCode();

        var update = await WaitForAsync(pool);
        Assert.Equal(["game-added"], update.Types);
        Assert.Equal(update.FromSequence, update.ToSequence);

        // One more pool change and one season change as markers: whatever the account change sent would have come before
        (await admin.PostAsJsonAsync("/api/pool", new { commandId = Guid.NewGuid(), title = "Soma", tags = new[] { "Horror" }, hours = 10 }, Ct)).EnsureSuccessStatusCode();
        await _site.SendAsync(new SetSeasonDeadline(_site.Clock.UtcNow.AddDays(7)));
        await UntilAsync(seasonUpdates, await LastSequenceAsync());
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (pool.Count < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "The second pool update did not come.");
            await Task.Delay(20, Ct);
        }

        Assert.Equal([["game-added"], ["game-added"]], pool.Select(p => p.Types));
        Assert.Empty(strayPool);
        Assert.All(seasonUpdates, u => Assert.Equal(SiteFactory.SeasonId, u.SeasonId));
        Assert.DoesNotContain(seasonUpdates, u => u.Types.Any(t => t.StartsWith("account-", StringComparison.Ordinal)));
        await poolWatcher.DisposeAsync();
        await seasonOnly.DisposeAsync();
    }

    // ---- Helpers ----

    private async Task<(HubConnection Connection, ConcurrentQueue<SeasonUpdate> Updates)> WatcherAsync(string login)
    {
        var cookie = await SessionCookieAsync(login);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_site.Server.BaseAddress, SeasonHub.Path.TrimStart('/')), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => _site.Server.CreateHandler();
                o.Headers["Cookie"] = cookie;
            })
            .Build();
        var updates = new ConcurrentQueue<SeasonUpdate>();
        connection.On<SeasonUpdate>(SeasonHub.UpdateMethod, updates.Enqueue);
        await connection.StartAsync(Ct);
        return (connection, updates);
    }

    private static async Task<List<SeasonUpdate>> UntilAsync(ConcurrentQueue<SeasonUpdate> updates, long last)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!updates.Any(u => u.ToSequence >= last))
        {
            Assert.True(DateTime.UtcNow < deadline, $"No update up to {last}; got {string.Join(", ", updates.Select(u => u.ToSequence))}.");
            await Task.Delay(20, Ct);
        }

        return [.. updates];
    }

    private static async Task<T> WaitForAsync<T>(ConcurrentQueue<T> queue)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (queue.IsEmpty)
        {
            Assert.True(DateTime.UtcNow < deadline, "No update came.");
            await Task.Delay(20, Ct);
        }

        return queue.First();
    }

    private async Task<long> LastSequenceAsync()
    {
        await using var db = _site.NewDb();
        return db.Events.Where(e => e.SeasonId == SiteFactory.SeasonId).Max(e => e.Sequence);
    }

    private static async Task PostOkAsync(HttpClient client, string url, object? body = null)
    {
        var response = await client.PostAsJsonAsync(url, body ?? new { commandId = Guid.NewGuid() }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<string> SessionCookieAsync(string login)
    {
        var client = _site.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        var tokenResponse = await client.GetAsync("/api/auth/antiforgery", Ct);
        var csrfCookie = tokenResponse.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var token = await tokenResponse.Content.ReadFromJsonAsync<Accounts.AntiforgeryToken>(Ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { login, password = SiteFactory.Password }),
        };
        request.Headers.Add("Cookie", csrfCookie);
        request.Headers.Add(token!.HeaderName, token.Token);
        var response = await client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ge.session=", StringComparison.Ordinal)).Split(';')[0];
    }
}

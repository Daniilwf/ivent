using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Web.Realtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Shared steps of the task 5.19 API tests (polls, global events, the weekly challenge; D-830…D-834). Written by
/// <c>test-author</c> from SPEC «Голосования», «Общие ивенты», «Челлендж недели», D-513…D-518, D-526, D-541, D-726 and the
/// public contract of the engine, before the endpoints exist: requests are plain JSON, so the tests compile and fail on
/// the missing routes.
/// </summary>
internal static class PollSeason
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    public static string AdminUrl(string action) => $"/api/admin/seasons/{SiteFactory.SeasonId}/{action}";

    /// <summary>The body of the admin's «start a poll»: the CONTENT.md poll as JSON (D-512).</summary>
    public static object NominationJson(bool anonymous = false, string voters = "players", bool? showLiveCounts = null, string[]? options = null) =>
        showLiveCounts is { } live
            ? new { question = "Лучший момент недели?", options = new { list = options ?? ["Финиш", "Дроп", "Реролл"] }, voters, anonymous, closesInHours = 24, showLiveCounts = live }
            : new { question = "Лучший момент недели?", options = new { list = options ?? ["Финиш", "Дроп", "Реролл"] }, voters, anonymous, closesInHours = 24 };

    /// <summary>The admin starts a poll over HTTP; its id is read from the season's state, not from the list under test.</summary>
    public static async Task<Guid> StartPollAsync(SiteFactory site, HttpClient admin, object poll)
    {
        var before = (await Queue.EventsTestSupport.StateAsync(site)).Polls.Keys.ToHashSet();
        await ApiCalls.PostOkAsync(admin, AdminUrl("polls"), new { commandId = Guid.NewGuid(), poll });
        return (await Queue.EventsTestSupport.StateAsync(site)).Polls.Keys.Single(id => !before.Contains(id));
    }

    public static Task<HttpResponseMessage> VoteAsync(HttpClient client, Guid pollId, string optionId, Guid? commandId = null) =>
        client.PostAsJsonAsync(Url($"polls/{pollId}/vote"), new { commandId = commandId ?? Guid.NewGuid(), optionId }, Ct);

    public static async Task VoteOkAsync(HttpClient client, Guid pollId, string optionId, Guid? commandId = null)
    {
        var response = await VoteAsync(client, pollId, optionId, commandId);
        Assert.True(response.IsSuccessStatusCode, $"vote {optionId}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    public static async Task<JsonElement> CardAsync(HttpClient client, Guid pollId) =>
        await ApiCalls.OkAsync(await client.GetAsync(Url($"polls/{pollId}"), Ct));

    public static async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    public static async Task<IReadOnlyList<string>> TypesAsync(HttpResponseMessage response) =>
        [.. (await ApiCalls.OkAsync(response)).GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()!)];

    public static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, string json) =>
        client.PostAsync(url, new StringContent(json.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal), System.Text.Encoding.UTF8, "application/json"), Ct);

    /// <summary>A watcher of the season's hub signed in as <paramref name="login"/>, collecting every update it gets.</summary>
    public static async Task<(HubConnection Connection, List<SeasonUpdate> Updates)> WatchAsync(SiteFactory site, string login)
    {
        var cookie = await SessionCookieAsync(site, login);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(site.Server.BaseAddress, SeasonHub.Path.TrimStart('/')), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => site.Server.CreateHandler();
                o.Headers["Cookie"] = cookie;
            })
            .Build();
        var updates = new List<SeasonUpdate>();
        connection.On<SeasonUpdate>(SeasonHub.UpdateMethod, u =>
        {
            lock (updates)
            {
                updates.Add(u);
            }
        });
        await connection.StartAsync(Ct);
        await connection.InvokeAsync<SeasonJoin>(nameof(SeasonHub.Join), SiteFactory.SeasonId, Ct);
        return (connection, updates);
    }

    /// <summary>Waits until the watcher got an update with <paramref name="type"/> (the marker sent after what is checked).</summary>
    public static async Task WaitForAsync(List<SeasonUpdate> updates, string type)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            lock (updates)
            {
                if (updates.Any(u => u.Types.Contains(type)))
                {
                    return;
                }
            }

            Assert.True(DateTime.UtcNow < until, $"No hub update with {type} within 10 s.");
            await Task.Delay(50, Ct);
        }
    }

    private static async Task<string> SessionCookieAsync(SiteFactory site, string login)
    {
        var client = site.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
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

/// <summary>The game clock that throws while <see cref="Armed"/>: an unhandled failure inside a request, for the «Ошибки» page.</summary>
internal sealed class FailingClock(IClock inner) : IClock
{
    public bool Armed { get; set; }

    public DateTimeOffset UtcNow => Armed ? throw new InvalidOperationException("A test failure of the clock.") : inner.UtcNow;
}

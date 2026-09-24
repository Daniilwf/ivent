using System.Net.Http.Json;
using GameEvent.Web.Realtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace GameEvent.Web.Tests.Api;

/// <summary>Another player's browser learns about a committed command without reloading (B3, E3 groundwork).</summary>
public sealed class SeasonHubTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Watcher_receives_the_update_after_a_roll()
    {
        // Given Петя watching the season
        var petyaCookie = await SessionCookieAsync("petya");
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_site.Server.BaseAddress, SeasonHub.Path.TrimStart('/')), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => _site.Server.CreateHandler();
                o.Headers["Cookie"] = petyaCookie;
            })
            .Build();
        var received = new TaskCompletionSource<SeasonUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<SeasonUpdate>(SeasonHub.UpdateMethod, u => received.TrySetResult(u));
        await connection.StartAsync(Ct);
        await connection.InvokeAsync(nameof(SeasonHub.Join), SiteFactory.SeasonId, Ct);

        // When Вася rolls
        var vasya = await _site.SignedInAsync("vasya");
        (await vasya.PostAsJsonAsync($"/api/seasons/{SiteFactory.SeasonId}/roll", new { commandId = Guid.NewGuid() }, Ct)).EnsureSuccessStatusCode();

        // Then Петя gets the update with the new place in the log
        var update = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(SiteFactory.SeasonId, update.SeasonId);
        Assert.Equal(4, update.FromSequence);
        Assert.Equal(["game-rolled"], update.Types);
    }

    [Fact]
    public async Task Anonymous_cannot_connect_to_the_hub()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_site.Server.BaseAddress, SeasonHub.Path.TrimStart('/')), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => _site.Server.CreateHandler();
            })
            .Build();

        await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync(Ct));
    }

    private async Task<string> SessionCookieAsync(string login)
    {
        var client = _site.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        var tokenResponse = await client.GetAsync("/api/auth/antiforgery", Ct);
        var csrfCookie = tokenResponse.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var token = await tokenResponse.Content.ReadFromJsonAsync<Accounts.AntiforgeryToken>(Ct);

        using var login_ = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { login, password = SiteFactory.Password }),
        };
        login_.Headers.Add("Cookie", csrfCookie);
        login_.Headers.Add(token!.HeaderName, token.Token);
        var response = await client.SendAsync(login_, Ct);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ge.session=", StringComparison.Ordinal)).Split(';')[0];
    }
}

using System.Net;

namespace GameEvent.Web.Tests.Api;

/// <summary>A fresh site with accounts but no seasons yet.</summary>
public sealed class EmptySiteTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    public async ValueTask InitializeAsync() => await _site.SeedAsync(withSeason: false);

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Current_season_is_not_found_when_there_are_none()
    {
        var client = await _site.SignedInAsync("vasya");

        var response = await client.GetAsync("/api/seasons/current", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

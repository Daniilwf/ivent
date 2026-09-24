using System.Net;

namespace GameEvent.Web.Tests.Api;

/// <summary>The backend serves the built frontend: files as themselves, client routes as the SPA, API stays API.</summary>
public sealed class FrontendHostingTests : IAsyncLifetime
{
    private readonly string _dist = Path.Combine(Path.GetTempPath(), "game-event-tests", Guid.NewGuid().ToString("N"), "dist");
    private readonly SiteFactory _site;

    public FrontendHostingTests()
    {
        Directory.CreateDirectory(Path.Combine(_dist, "assets"));
        File.WriteAllText(Path.Combine(_dist, "index.html"), "<!doctype html><title>spa</title>");
        File.WriteAllText(Path.Combine(_dist, "assets", "app.js"), "console.log('app');");
        _site = new SiteFactory(loginAttemptsPerMinute: 1000, frontendPath: _dist);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _site.DisposeAsync();
        Directory.Delete(Path.GetDirectoryName(_dist)!, recursive: true);
    }

    [Fact]
    public async Task Script_is_served_as_javascript_not_as_the_page()
    {
        var response = await _site.CreateClient().GetAsync("/assets/app.js", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/season/some-client-route")]
    public async Task Pages_get_the_spa(string path)
    {
        var response = await _site.CreateClient().GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<title>spa</title>", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/no-such-endpoint")]
    [InlineData("/api")]
    [InlineData("/hubs/no-such-hub")]
    [InlineData("/hubs")]
    public async Task Unknown_api_and_hub_paths_are_not_the_spa(string path)
    {
        var response = await _site.CreateClient().GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

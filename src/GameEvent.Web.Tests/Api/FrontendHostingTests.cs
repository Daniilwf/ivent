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

    [Theory]
    [InlineData("/")]
    [InlineData("/season/some-client-route")]
    [InlineData("/assets/app.js")]
    public async Task The_page_runs_only_the_sites_own_scripts(string path)
    {
        // SEC-1: the full policy D-72 promised with the frontend — a slip that lets user text into the page runs nothing
        var response = await _site.CreateClient().GetAsync(path, Ct);

        var policy = Directives(Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("'self'", policy["default-src"]);
        Assert.Equal("'self'", policy["script-src"]);
        Assert.Equal("'none'", policy["object-src"]);
        Assert.Equal("'self'", policy["base-uri"]);
        Assert.Equal("'self'", policy["form-action"]);
        Assert.Equal("'none'", policy["frame-ancestors"]);
        Assert.DoesNotContain("unsafe-eval", policy["script-src"], StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Directives(string policy) =>
        policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Split(' ', 2))
            .ToDictionary(d => d[0], d => d.Length > 1 ? d[1] : "");
}

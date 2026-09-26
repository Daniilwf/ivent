using System.Net;
using System.Text.Json;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The API description the frontend's client is generated from (D-150): two records of one name share one OpenAPI schema,
/// and the client then gets one of them for both — the pool card lost its tags and cover that way.
/// </summary>
public sealed class OpenApiContractTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public void Views_and_requests_of_the_api_have_unique_names()
    {
        var types = typeof(Seasons.FeedEndpoints).Assembly.GetExportedTypes()
            .Where(t => t.Name.EndsWith("View", StringComparison.Ordinal) || t.Name.EndsWith("Request", StringComparison.Ordinal));

        var twins = types.GroupBy(t => t.Name).Where(g => g.Count() > 1).Select(g => string.Join(", ", g.Select(t => t.FullName)));

        Assert.Empty(twins);
    }

    [Fact]
    public async Task The_pool_card_schema_is_the_whole_card()
    {
        var response = await _site.CreateClient().GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var properties = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("GameView").GetProperty("properties");

        foreach (var name in new[] { "title", "tags", "cover", "year", "isDeleted" })
        {
            Assert.True(properties.TryGetProperty(name, out _), name);
        }
    }
}

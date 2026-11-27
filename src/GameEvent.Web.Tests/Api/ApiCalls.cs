using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Calls of the API that a test expects to succeed, shared by the test classes instead of a private copy in each
/// (D-202). The failure message carries the server's answer.
/// </summary>
internal static class ApiCalls
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A command that must be taken: a POST with the body (a new command id by default) and a success.</summary>
    public static async Task PostOkAsync(HttpClient client, string url, object? body = null)
    {
        var response = await client.PostAsJsonAsync(url, body ?? new { commandId = Guid.NewGuid() }, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    public static async Task ShowCurrentRollAsync(HttpClient client, Guid? seasonId = null)
    {
        var season = $"/api/seasons/{seasonId ?? SiteFactory.SeasonId}";
        var view = await OkAsync(await client.GetAsync(season, Ct));
        var revealId = view.GetProperty("me").GetProperty("roll").GetProperty("revealId").GetGuid();
        await PostOkAsync(client, $"{season}/roll/reveal", new { commandId = Guid.NewGuid(), revealId });
    }

    /// <summary>A 200 answer's JSON.</summary>
    public static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}

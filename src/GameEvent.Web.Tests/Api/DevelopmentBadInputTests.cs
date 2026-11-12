using System.Net;
using System.Text;
using System.Text.Json;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Invalid input is a 400 in every environment (D-68, K1O-2, D-630): in Development minimal APIs throw on a bad request
/// by default, and the exception handler turned that into a 500 and an entry on the admin's «Ошибки» page.
/// </summary>
public sealed class DevelopmentBadInputTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new(loginAttemptsPerMinute: 1000, environment: "Development");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Theory]
    [InlineData("roll", "{")]
    [InlineData("roll", """{"commandId":5}""")]
    [InlineData("complete", """{"commandId":"00000000-0000-0000-0000-000000000001","difficulty":"nightmare","hours":3}""")]
    public async Task A_broken_body_is_a_bad_request_and_not_an_error(string action, string body)
    {
        var vasya = await _site.SignedInAsync("vasya");

        var answer = await vasya.PostAsync($"/api/seasons/{SiteFactory.SeasonId}/{action}", new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        await AssertNoErrorsAsync();
    }

    [Fact]
    public async Task A_body_not_in_utf8_is_a_bad_request_and_not_an_error()
    {
        var vasya = await _site.SignedInAsync("vasya");
        // «Вася» in Windows-1251, as curl from Git Bash sends it
        byte[] bytes = [.. "{\"commandId\":\"00000000-0000-0000-0000-000000000002\",\"difficulty\":\"normal\",\"hoursSource\":\""u8.ToArray(), 0xC2, 0xE0, 0xF1, 0xFF, .. "\"}"u8.ToArray()];
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var answer = await vasya.PostAsync($"/api/seasons/{SiteFactory.SeasonId}/complete", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        await AssertNoErrorsAsync();
    }

    private async Task AssertNoErrorsAsync()
    {
        var admin = await _site.SignedInAsync("admin");
        using var doc = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/errors", Ct));
        Assert.Empty(doc.RootElement.EnumerateArray());
    }
}

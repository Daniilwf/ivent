using System.Net;
using System.Text.Json;
using GameEvent.Web.Tests.Api;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.PlayerPreview;

public sealed partial class PlayerPreviewApiTests
{
    [Fact]
    public async Task An_active_run_keeps_the_selected_players_complete_private_turn()
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        await PostOkAsync(vasya, $"{Season}/roll");
        await PostOkAsync(vasya, $"{Season}/start");
        var own = await ReadJsonAsync(vasya, Season);
        Assert.Equal("playing", own.GetProperty("me").GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Object, own.GetProperty("me").GetProperty("activeRun").ValueKind);

        using var response = await SendScopedAsync(admin, HttpMethod.Get, Preview(_site.Players["vasya"]));
        var preview = await OkAsync(response);

        Assert.Equal(own.GetProperty("me").GetRawText(), preview.GetProperty("season").GetProperty("me").GetRawText());
        AssertNoAccountProperties(preview);
    }

    [Fact]
    public async Task An_archived_season_can_be_read_without_reopening_it()
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        foreach (var status in new[] { "closing", "finished", "archived" })
        {
            await PostOkAsync(admin, $"{AdminSeason}/status", new { commandId = Guid.NewGuid(), to = status });
        }
        var before = await ReadJsonAsync(vasya, Season);

        using var response = await SendScopedAsync(admin, HttpMethod.Get, Preview(_site.Players["vasya"]));
        var preview = await OkAsync(response);

        Assert.Equal("archived", preview.GetProperty("season").GetProperty("status").GetString());
        Assert.Equal(before.GetProperty("me").GetRawText(), preview.GetProperty("season").GetProperty("me").GetRawText());
        Assert.Equal(before.GetRawText(), (await ReadJsonAsync(vasya, Season)).GetRawText());
    }

    [Fact]
    public async Task Preview_does_not_reveal_the_targets_anonymous_vote()
    {
        await GameEvent.Web.Tests.Queue.EventsTestSupport.PollsOnAsync(_site);
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        var pollId = await PollSeason.StartPollAsync(_site, admin, PollSeason.NominationJson(anonymous: true));
        await PollSeason.VoteOkAsync(vasya, pollId, "Реролл");
        Assert.Equal("Реролл", (await PollSeason.CardAsync(vasya, pollId)).GetProperty("myVote").GetString());

        using var response = await SendScopedAsync(admin, HttpMethod.Get, Preview(_site.Players["vasya"]));
        var preview = await OkAsync(response);

        AssertAnonymousChoicesAreRedacted(preview);
        using var fallback = await SendScopedAsync(admin, HttpMethod.Get, $"{Season}/polls/{pollId}");
        Assert.Equal(HttpStatusCode.Forbidden, fallback.StatusCode);
    }

    [Theory]
    [InlineData("sessions")]
    [InlineData("accounts")]
    [InlineData("economy")]
    [InlineData("polls")]
    public async Task Preview_context_never_falls_back_to_an_unscoped_read(string read)
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        var url = read switch
        {
            "sessions" => "/api/auth/sessions",
            "accounts" => "/api/admin/accounts",
            _ => $"{Season}/{read}",
        };

        using var response = await SendScopedAsync(admin, HttpMethod.Get, url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Every_unsafe_method_is_refused_in_preview_context(string method)
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");

        using var response = await SendScopedAsync(admin, new HttpMethod(method), "/api/admin/maintenance", new { on = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("playerPreview.readOnly", (await ReadBodyAsync(response)).GetProperty("code").GetString());
        Assert.False((await ReadJsonAsync(admin, "/api/status")).GetProperty("maintenance").GetBoolean());
    }

    [Fact]
    public async Task A_revoked_real_admin_session_cannot_keep_reading_through_preview()
    {
        using var host = WithPreview();
        using var first = await SignInAsync(host, "admin");
        var session = Assert.Single((await ReadJsonAsync(first, "/api/auth/sessions")).EnumerateArray(),
            item => item.GetProperty("current").GetBoolean()).GetProperty("id").GetGuid();
        using var second = await SignInAsync(host, "admin");
        await PostOkAsync(second, $"/api/auth/sessions/{session}/end");

        using var response = await SendScopedAsync(first, HttpMethod.Get, Preview(_site.Players["vasya"]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(_site.Users["admin"], (await ReadJsonAsync(second, "/api/auth/me")).GetProperty("id").GetGuid());
    }

    private static void AssertAnonymousChoicesAreRedacted(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "myVote" or "myBallot" or "votedOptionId" or "selectedOptionId")
                {
                    Assert.Equal(JsonValueKind.Null, property.Value.ValueKind);
                }
                AssertAnonymousChoicesAreRedacted(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray())
            {
                AssertAnonymousChoicesAreRedacted(value);
            }
        }
    }
}

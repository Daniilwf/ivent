using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Web.Tests.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.PlayerPreview;

public sealed partial class PlayerPreviewApiTests
{
    [Fact]
    public async Task The_site_advertises_preview_only_when_it_is_explicitly_enabled()
    {
        using var ordinary = _site.CreateClient();
        using var enabled = WithPreview();
        using var enabledClient = enabled.CreateClient();

        var off = await ReadJsonAsync(ordinary, "/api/status");
        var on = await ReadJsonAsync(enabledClient, "/api/status");

        Assert.False(off.TryGetProperty("playerPreview", out _));
        Assert.True(on.TryGetProperty("playerPreview", out var flag));
        Assert.True(flag.GetBoolean());
    }

    [Theory]
    [InlineData("unknown-season")]
    [InlineData("empty-season")]
    [InlineData("malformed-season")]
    [InlineData("unknown-player")]
    [InlineData("empty-player")]
    [InlineData("malformed-player")]
    [InlineData("account-instead-of-player")]
    public async Task A_missing_or_invalid_season_participant_is_not_found(string invalid)
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        var seasonId = invalid switch
        {
            "unknown-season" => Guid.NewGuid().ToString(),
            "empty-season" => Guid.Empty.ToString(),
            "malformed-season" => "not-a-season-id",
            _ => SiteFactory.SeasonId.ToString(),
        };
        var playerId = invalid switch
        {
            "unknown-player" => Guid.NewGuid().ToString(),
            "empty-player" => Guid.Empty.ToString(),
            "malformed-player" => "not-a-player-id",
            "account-instead-of-player" => _site.Users["vasya"].ToString(),
            _ => _site.Players["vasya"].ToString(),
        };

        using var response = await SendScopedAsync(admin, HttpMethod.Get, $"/api/admin/seasons/{seasonId}/players/{playerId}/preview");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_same_account_in_two_seasons_has_two_distinct_preview_scopes()
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        var otherSeason = await _site.CreateSeasonAsync();
        await PostOkAsync(admin, $"/api/admin/seasons/{otherSeason}/players", new
        {
            commandId = Guid.NewGuid(),
            userId = _site.Users["vasya"],
        });
        var otherPlayer = Assert.Single((await ReadJsonAsync(admin, $"/api/admin/seasons/{otherSeason}/players")).EnumerateArray())
            .GetProperty("id").GetGuid();
        Assert.NotEqual(_site.Players["vasya"], otherPlayer);

        using var wrongSeason = await SendScopedAsync(admin, HttpMethod.Get,
            $"/api/admin/seasons/{SiteFactory.SeasonId}/players/{otherPlayer}/preview");
        using var wrongPlayer = await SendScopedAsync(admin, HttpMethod.Get,
            $"/api/admin/seasons/{otherSeason}/players/{_site.Players["vasya"]}/preview");
        using var correct = await SendScopedAsync(admin, HttpMethod.Get,
            $"/api/admin/seasons/{otherSeason}/players/{otherPlayer}/preview");

        Assert.Equal(HttpStatusCode.NotFound, wrongSeason.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongPlayer.StatusCode);
        var preview = await OkAsync(correct);
        Assert.Equal(otherPlayer, preview.GetProperty("playerId").GetGuid());
        Assert.Equal(otherSeason, preview.GetProperty("season").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Selecting_another_participant_never_reuses_the_previous_private_offer()
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        using var petya = await SignInAsync(host, "petya");
        await PostOkAsync(vasya, $"{Season}/roll");

        using var firstResponse = await SendScopedAsync(admin, HttpMethod.Get, Preview(_site.Players["vasya"]));
        var first = await OkAsync(firstResponse);
        using var secondResponse = await SendScopedAsync(admin, HttpMethod.Get, Preview(_site.Players["petya"]));
        var second = await OkAsync(secondResponse);

        Assert.Equal(JsonValueKind.Object, first.GetProperty("season").GetProperty("me").GetProperty("offer").ValueKind);
        Assert.Equal(_site.Players["petya"], second.GetProperty("playerId").GetGuid());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("season").GetProperty("me").GetProperty("offer").ValueKind);
        Assert.Equal((await ReadJsonAsync(petya, Season)).GetProperty("me").GetRawText(),
            second.GetProperty("season").GetProperty("me").GetRawText());
    }

    [Fact]
    public async Task The_selected_inventory_is_the_same_private_inventory_the_player_reads()
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        await EnableItemsAsync(admin);
        await PostOkAsync(admin, $"{AdminSeason}/content/publish", new
        {
            commandId = Guid.NewGuid(),
            expectedVersion = 0,
            pack = EconomyApiTests.RepositoryPack(),
            comment = "Preview fixture content",
        });
        await PostOkAsync(admin, $"{AdminSeason}/players/{_site.Players["vasya"]}/inventory", new
        {
            commandId = Guid.NewGuid(),
            giveObjectId = "bird-thief",
            comment = "The selected player's item",
        });
        var ownEconomy = await ReadJsonAsync(vasya, $"{Season}/economy");
        Assert.NotEmpty(ownEconomy.GetProperty("items").EnumerateArray());

        using var response = await SendScopedAsync(admin, HttpMethod.Get, Preview(_site.Players["vasya"]));
        var preview = await OkAsync(response);

        Assert.Equal(ownEconomy.GetRawText(), preview.GetProperty("economy").GetRawText());
        AssertNoAccountProperties(preview);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    public async Task An_empty_or_false_looking_context_still_cannot_mutate(string marker)
    {
        var probe = new HeaderProbe(deliverEmptyValue: marker.Length == 0);
        using var host = WithPreview().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(probe)));
        using var vasya = await SignInAsync(host, "vasya");
        var before = await ReadJsonAsync(vasya, Season);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Season}/roll")
        {
            Content = JsonContent.Create(new { commandId = Guid.NewGuid() }),
        };
        request.Headers.TryAddWithoutValidation(ContextHeader, marker);

        using var response = await vasya.SendAsync(request, Ct);

        Assert.True(probe.Present, $"The scoped fixture did not deliver {ContextHeader}={JsonSerializer.Serialize(marker)}; received: {probe.Description}");
        Assert.Equal(marker, Assert.Single(probe.Values));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("playerPreview.readOnly", (await ReadBodyAsync(response)).GetProperty("code").GetString());
        Assert.Equal(before.GetRawText(), (await ReadJsonAsync(vasya, Season)).GetRawText());
    }

    [Fact]
    public async Task Read_only_refusal_precedes_body_binding_and_missing_antiforgery()
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        admin.DefaultRequestHeaders.Clear();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AdminSeason}/players/{_site.Players["vasya"]}/adjust")
        {
            Content = new StringContent("not-json", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(ContextHeader, "1");

        using var response = await admin.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("playerPreview.readOnly", (await ReadBodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_scope_cannot_end_the_real_admin_session()
    {
        using var host = WithPreview();
        using var admin = await SignInAsync(host, "admin");
        var before = await ReadJsonAsync(admin, "/api/auth/me");

        using var response = await SendScopedAsync(admin, HttpMethod.Post, "/api/auth/logout");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("playerPreview.readOnly", (await ReadBodyAsync(response)).GetProperty("code").GetString());
        Assert.Equal(before.GetRawText(), (await ReadJsonAsync(admin, "/api/auth/me")).GetRawText());
    }

    private static async Task EnableItemsAsync(HttpClient admin)
    {
        var current = await ReadJsonAsync(admin, $"{Season}/rules");
        var ruleset = JsonNode.Parse(current.GetProperty("ruleset").GetRawText())!;
        ruleset["features"]!["items"] = true;
        using var response = await admin.PutAsJsonAsync($"{AdminSeason}/rules", new
        {
            commandId = Guid.NewGuid(),
            expectedVersion = current.GetProperty("version").GetInt32(),
            ruleset,
        }, Ct);
        await OkAsync(response);
    }

    private sealed class HeaderProbe(bool deliverEmptyValue) : IStartupFilter
    {
        public bool Present { get; private set; }
        public string?[] Values { get; private set; } = [];
        public string Description { get; private set; } = "Not observed";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                if (context.Request.Path == $"{Season}/roll" && context.Request.Method == "POST")
                {
                    // TestServer drops an empty HttpRequestMessage header. Supply that exact request input at its
                    // HttpContext boundary so this case tests a present-empty header, not an ordinary unmarked tab.
                    if (deliverEmptyValue)
                    {
                        context.Request.Headers[ContextHeader] = new Microsoft.Extensions.Primitives.StringValues(string.Empty);
                    }

                    Present = context.Request.Headers.ContainsKey(ContextHeader);
                    Values = context.Request.Headers[ContextHeader].ToArray();
                    Description = $"present={Present}, count={Values.Length}, values={JsonSerializer.Serialize(Values)}";
                }

                await following(context);
            });
            next(app);
        };
    }
}

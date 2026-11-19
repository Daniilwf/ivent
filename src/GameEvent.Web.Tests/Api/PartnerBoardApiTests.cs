using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Api.PollSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The partner board over HTTP (5.20, D-524, D-742, D-790, D-794): a roller asks for a partner, another
/// player responds by an invitation, and the author closes the request. Written before the endpoints.
/// </summary>
public sealed class PartnerBoardApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Board => Url("partner-requests");

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        await CoopOnAsync(_site, partnerBoard: true);
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    private async Task<Guid> PostedAsync()
    {
        await PlayingAsync(_site, _site.Players["vasya"]);
        await _site.SendAsync(new PostPartnerRequest(_site.Players["vasya"]));
        return Assert.Single((await StateAsync(_site)).PartnerRequests.Keys);
    }

    [Fact]
    public async Task The_roller_posts_a_request_and_it_is_projected_with_the_real_actor_and_run()
    {
        var runId = await PlayingAsync(_site, _site.Players["vasya"]);
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(Board, new { commandId = Guid.NewGuid(), playerId = _site.Players["petya"] }, Ct);

        Assert.Contains("partner-request-posted", await TypesAsync(response));
        var request = Assert.Single((await StateAsync(_site)).PartnerRequests.Values);
        Assert.Equal(_site.Players["vasya"], request.PlayerId);
        Assert.Equal(runId, request.RunId);
        await using var db = _site.NewDb();
        var row = Assert.Single(await db.PartnerRequests.AsNoTracking().ToListAsync(Ct));
        Assert.Equal((request.RequestId, request.PlayerId, runId), (row.Id, row.PlayerId, row.RunId));
        await AssertIntactAsync(_site);
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("petya")]
    [InlineData("zritel")]
    public async Task Anyone_signed_in_reads_open_requests_with_their_terms(string login)
    {
        var id = await PostedAsync();
        var expected = (await StateAsync(_site)).PartnerRequests[id];
        var client = await _site.SignedInAsync(login);

        var item = Assert.Single((await ApiCalls.OkAsync(await client.GetAsync(Board, Ct))).EnumerateArray());

        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal(expected.PlayerId, item.GetProperty("playerId").GetGuid());
        Assert.Equal(expected.RunId, item.GetProperty("runId").GetGuid());
        Assert.Equal(expected.CreatedAt, item.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(expected.ExpiresAt, item.GetProperty("expiresAt").GetDateTimeOffset());
        var owner = await _site.SignedInAsync("vasya");
        var turn = (await ApiCalls.OkAsync(await owner.GetAsync(Url(""), Ct))).GetProperty("me");
        var game = turn.GetProperty("activeRun").GetProperty("game");
        Assert.True(game.GetProperty("isCoop").GetBoolean());
        Assert.Equal(game.GetProperty("title").GetString(), item.GetProperty("gameTitle").GetString());
    }

    [Fact]
    public async Task An_empty_board_is_a_successful_empty_list()
    {
        var client = await _site.SignedInAsync("zritel");
        Assert.Empty((await ApiCalls.OkAsync(await client.GetAsync(Board, Ct))).EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_and_expired_requests_are_not_shown_even_before_the_timer_runs(bool expired)
    {
        var id = await PostedAsync();
        if (expired)
        {
            _site.Clock.UtcNow = (await StateAsync(_site)).PartnerRequests[id].ExpiresAt;
        }
        else
        {
            await _site.SendAsync(new ClosePartnerRequest(_site.Players["vasya"], id));
        }
        var client = await _site.SignedInAsync("petya");

        Assert.Empty((await ApiCalls.OkAsync(await client.GetAsync(Board, Ct))).EnumerateArray());
    }

    [Fact]
    public async Task Anonymous_does_not_read_the_board_and_an_unknown_season_is_not_found()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await (await _site.AnonymousAsync()).GetAsync(Board, Ct)).StatusCode);
        var client = await _site.SignedInAsync("vasya");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/seasons/{Guid.Empty}/partner-requests", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("zritel")]
    [InlineData("masha")]
    public async Task A_spectator_or_player_outside_the_season_cannot_post(string login)
    {
        var client = await _site.SignedInAsync(login);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Board, new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Empty((await StateAsync(_site)).PartnerRequests);
    }

    [Fact]
    public async Task Posting_requires_a_command_id_and_an_active_coop_game()
    {
        var client = await _site.SignedInAsync("vasya");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Board, new { }, Ct)).StatusCode);
        await AssertConflictAsync(await client.PostAsJsonAsync(Board, new { commandId = Guid.NewGuid() }, Ct), "coop.runNotActive");
        Assert.Empty((await StateAsync(_site)).PartnerRequests);
    }

    [Fact]
    public async Task Repeating_a_post_command_creates_only_one_request()
    {
        await PlayingAsync(_site, _site.Players["vasya"]);
        var client = await _site.SignedInAsync("vasya");
        var body = new { commandId = Guid.NewGuid() };
        await ApiCalls.PostOkAsync(client, Board, body);

        var repeated = await ApiCalls.OkAsync(await client.PostAsJsonAsync(Board, body, Ct));

        Assert.True(repeated.GetProperty("duplicate").GetBoolean());
        Assert.Single((await StateAsync(_site)).PartnerRequests);
    }

    [Fact]
    public async Task A_response_is_an_invitation_from_the_responder_to_the_author()
    {
        var id = await PostedAsync();
        var client = await _site.SignedInAsync("petya");
        var body = new { commandId = Guid.NewGuid(), playerId = _site.Players["vasya"] };

        var response = await client.PostAsJsonAsync($"{Board}/{id}/respond", body, Ct);

        Assert.Contains("interaction-created", await TypesAsync(response));
        var invite = Assert.Single((await StateAsync(_site)).Interactions.Values);
        Assert.Equal(InteractionKind.Coop, invite.Kind);
        Assert.Equal(_site.Players["petya"], invite.FromPlayerId);
        Assert.Equal(_site.Players["vasya"], invite.ToPlayerId);
        var repeated = await ApiCalls.OkAsync(await client.PostAsJsonAsync($"{Board}/{id}/respond", body, Ct));
        Assert.True(repeated.GetProperty("duplicate").GetBoolean());
        Assert.Single((await StateAsync(_site)).Interactions);
    }

    [Theory]
    [InlineData(1, 3, false)]
    [InlineData(1, 3, true)]
    [InlineData(3, 1, false)]
    public async Task Pending_responses_show_the_earlier_term_and_direct_invites_keep_theirs(int requestHours, int inviteHours, bool switchOff)
    {
        await RulesAsync(_site, rules => rules with
        {
            PartnerBoard = rules.PartnerBoard! with { RequestTtlHours = requestHours },
            Interactions = rules.Interactions with { InviteTtlHours = inviteHours },
        });
        var requestId = await PostedAsync();
        await _site.SendAsync(new RespondToPartnerRequest(_site.Players["petya"], requestId));
        await _site.SendAsync(new InviteToCoop(_site.Players["vasya"], _site.Players["petya"]));
        if (switchOff)
        {
            await RulesAsync(_site, Flags(features => features with { Coop = false, PartnerBoard = false }));
        }

        var state = await StateAsync(_site);
        var author = _site.Players["vasya"];
        var boardResponse = Assert.Single(state.Interactions.Values, interaction => interaction.ToPlayerId == author);
        var directInvite = Assert.Single(state.Interactions.Values, interaction => interaction.FromPlayerId == author);
        var request = state.PartnerRequests[requestId];
        var effectiveTerm = request.ExpiresAt < boardResponse.ExpiresAt ? request.ExpiresAt : boardResponse.ExpiresAt;
        foreach (var login in new[] { "vasya", "petya" })
        {
            var client = await _site.SignedInAsync(login);
            var list = (await ApiCalls.OkAsync(await client.GetAsync(Url("interactions"), Ct))).EnumerateArray().ToArray();
            var response = Assert.Single(list, row => row.GetProperty("id").GetGuid() == boardResponse.InteractionId);
            Assert.Equal(boardResponse.ExpiresAt, response.GetProperty("expiresAt").GetDateTimeOffset());
            Assert.Equal(effectiveTerm, response.GetProperty("effectiveExpiresAt").GetDateTimeOffset());
            var direct = Assert.Single(list, row => row.GetProperty("id").GetGuid() == directInvite.InteractionId);
            Assert.Equal(directInvite.ExpiresAt, direct.GetProperty("expiresAt").GetDateTimeOffset());
            Assert.False(direct.TryGetProperty("effectiveExpiresAt", out _));
        }
    }

    [Fact]
    public async Task A_response_to_a_reopened_request_uses_its_new_term()
    {
        await RulesAsync(_site, rules => rules with
        {
            PartnerBoard = rules.PartnerBoard! with { RequestTtlHours = 1 },
            Interactions = rules.Interactions with { InviteTtlHours = 24 },
        });
        var oldRequest = await PostedAsync();
        await _site.SendAsync(new ClosePartnerRequest(_site.Players["vasya"], oldRequest));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(10);
        await _site.SendAsync(new PostPartnerRequest(_site.Players["vasya"]));
        var request = Assert.Single((await StateAsync(_site)).PartnerRequests.Values, item => item.RequestId != oldRequest);
        await _site.SendAsync(new RespondToPartnerRequest(_site.Players["petya"], request.RequestId));
        var client = await _site.SignedInAsync("petya");

        var response = Assert.Single((await ApiCalls.OkAsync(await client.GetAsync(Url("interactions"), Ct))).EnumerateArray());

        Assert.Equal(request.ExpiresAt, response.GetProperty("effectiveExpiresAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("zritel")]
    [InlineData("masha")]
    public async Task Only_a_player_of_the_season_responds(string login)
    {
        var id = await PostedAsync();
        var client = await _site.SignedInAsync(login);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Board}/{id}/respond", new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Empty((await StateAsync(_site)).Interactions);
    }

    [Fact]
    public async Task Responding_requires_a_command_id_and_cannot_address_another_seasons_request()
    {
        var id = await PostedAsync();
        var client = await _site.SignedInAsync("petya");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Board}/{id}/respond", new { }, Ct)).StatusCode);
        await AssertConflictAsync(await client.PostAsJsonAsync($"{Board}/{Guid.NewGuid()}/respond", new { commandId = Guid.NewGuid() }, Ct), "partnerRequest.unknown");
        var other = await _site.CreateSeasonAsync();
        var bus = _site.Services.GetRequiredService<CommandBus>();
        foreach (var command in new GameEvent.Engine.Kernel.ICommand[]
        {
            new ChangeRuleset((await StateAsync(_site)).Rules),
            new ChangeSeasonStatus(SeasonStatus.Active),
            new AddSeasonPlayer(Guid.NewGuid(), _site.Users["petya"], "petya"),
        })
        {
            var outcome = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), other, command, null), Ct);
            Assert.True(outcome.IsAccepted, outcome.Rejection?.ToString());
        }
        await AssertConflictAsync(await client.PostAsJsonAsync($"/api/seasons/{other}/partner-requests/{id}/respond", new { commandId = Guid.NewGuid() }, Ct), "partnerRequest.unknown");
        Assert.Empty((await StateAsync(_site)).Interactions);
    }

    [Fact]
    public async Task The_author_closes_his_request_and_its_pending_response()
    {
        var id = await PostedAsync();
        await _site.SendAsync(new RespondToPartnerRequest(_site.Players["petya"], id));
        var client = await _site.SignedInAsync("vasya");

        var response = await client.PostAsJsonAsync($"{Board}/{id}/close", new { commandId = Guid.NewGuid() }, Ct);

        Assert.Contains("partner-request-closed", await TypesAsync(response));
        var state = await StateAsync(_site);
        Assert.Equal(PartnerRequestStatus.Closed, state.PartnerRequests[id].Status);
        Assert.Equal(InteractionStatus.Expired, Assert.Single(state.Interactions.Values).Status);
        var ended = Assert.Single((await ApiCalls.OkAsync(await client.GetAsync(Url("interactions"), Ct))).EnumerateArray());
        Assert.Equal("expired", ended.GetProperty("status").GetString());
        Assert.False(ended.TryGetProperty("effectiveExpiresAt", out _));
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_different_player_cannot_close_the_authors_request()
    {
        var id = await PostedAsync();
        var client = await _site.SignedInAsync("petya");
        await AssertConflictAsync(await client.PostAsJsonAsync($"{Board}/{id}/close", new { commandId = Guid.NewGuid() }, Ct), "partnerRequest.notAuthor");
        Assert.Equal(PartnerRequestStatus.Open, (await StateAsync(_site)).PartnerRequests[id].Status);
    }

    [Fact]
    public async Task Closing_requires_a_command_id_and_unknown_request_is_a_domain_refusal()
    {
        var id = await PostedAsync();
        var client = await _site.SignedInAsync("vasya");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Board}/{id}/close", new { }, Ct)).StatusCode);
        await AssertConflictAsync(await client.PostAsJsonAsync($"{Board}/{Guid.NewGuid()}/close", new { commandId = Guid.NewGuid() }, Ct), "partnerRequest.unknown");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Switching_the_board_off_hides_it_and_blocks_new_actions_but_not_the_authors_close(bool changeGame)
    {
        var id = await PostedAsync();
        await RulesAsync(_site, Flags(f => f with { Coop = false, PartnerBoard = false }));
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        if (changeGame)
        {
            var view = await ApiCalls.OkAsync(await vasya.GetAsync(Url(""), Ct));
            var game = view.GetProperty("me").GetProperty("activeRun").GetProperty("game");
            var admin = await _site.SignedInAsync("admin");
            var update = await admin.PutAsJsonAsync($"/api/admin/pool/{game.GetProperty("id").GetGuid()}", new
            {
                commandId = Guid.NewGuid(),
                title = game.GetProperty("title").GetString(),
                tags = new[] { "Test" },
                hours = 5,
                isCoop = false,
            }, Ct);
            await ApiCalls.OkAsync(update);
        }

        var ownerView = await ApiCalls.OkAsync(await vasya.GetAsync(Url(""), Ct));
        var otherView = await ApiCalls.OkAsync(await petya.GetAsync(Url(""), Ct));
        var owner = ownerView.GetProperty("me");
        Assert.False(ownerView.GetProperty("interactionFeatures").GetProperty("partnerBoard").GetBoolean());
        Assert.True(owner.GetProperty("hasInteractionActions").GetBoolean());
        Assert.Equal(id, owner.GetProperty("partnerRequest").GetProperty("id").GetGuid());
        Assert.False(otherView.GetProperty("me").TryGetProperty("partnerRequest", out var otherRequest)
            && otherRequest.ValueKind != JsonValueKind.Null);

        await AssertConflictAsync(await petya.GetAsync(Board, Ct), "feature.disabled");
        await AssertConflictAsync(await vasya.PostAsJsonAsync(Board, new { commandId = Guid.NewGuid() }, Ct), "feature.disabled");
        await AssertConflictAsync(await petya.PostAsJsonAsync($"{Board}/{id}/respond", new { commandId = Guid.NewGuid() }, Ct), "feature.disabled");
        await ApiCalls.PostOkAsync(vasya, $"{Board}/{id}/close");

        Assert.Equal(PartnerRequestStatus.Closed, (await StateAsync(_site)).PartnerRequests[id].Status);
        var after = (await ApiCalls.OkAsync(await vasya.GetAsync(Url(""), Ct))).GetProperty("me");
        Assert.False(after.GetProperty("hasInteractionActions").GetBoolean());
    }

    [Fact]
    public async Task The_deadline_stops_new_requests_and_responses()
    {
        var id = await PostedAsync();
        await _site.SendAsync(new SetSeasonDeadline(_site.Clock.UtcNow.AddHours(1)));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(2);
        var petya = await _site.SignedInAsync("petya");

        await AssertConflictAsync(await petya.PostAsJsonAsync($"{Board}/{id}/respond", new { commandId = Guid.NewGuid() }, Ct), "season.deadlinePassed");
        await AssertConflictAsync(await (await _site.SignedInAsync("vasya")).PostAsJsonAsync(Board, new { commandId = Guid.NewGuid() }, Ct), "season.deadlinePassed");
    }
}

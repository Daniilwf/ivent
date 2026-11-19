using System.Net;
using System.Text.Json;
using GameEvent.Engine.Duels;
using GameEvent.Engine.Interactions;
using static GameEvent.Web.Tests.Api.InteractionSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Task 5.20a, EV30/EV41/EV63: a duel is a challenge answered through the shared interaction API. The signed-in
/// challenger starts it, a fresh completed run wins, and every signed-in viewer may read this season's races.
/// </summary>
public sealed class DuelApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        await RulesAsync(_site, Flags(f => f with { Duels = true }));
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task The_signed_in_challenger_invites_and_the_recipient_accepts_a_public_duel()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");

        var response = await OkAsync(await PostAsync(vasya, "duels", new { commandId = Guid.NewGuid(), opponentId = _site.Players["petya"], playerId = _site.Players["petya"] }));

        Assert.Contains("interaction-created", Types(response));
        var challenge = Assert.Single((await StateAsync(_site)).Interactions.Values);
        Assert.Equal(InteractionKind.Duel, challenge.Kind);
        Assert.Equal(_site.Players["vasya"], challenge.FromPlayerId);
        Assert.Equal(_site.Players["petya"], challenge.ToPlayerId);
        Assert.Null(challenge.RunId);
        Assert.Empty((await GetAsync(vasya, "duels")).EnumerateArray());
        var incoming = Assert.Single((await GetAsync(petya, "interactions")).EnumerateArray());
        Assert.Equal(challenge.InteractionId, incoming.GetProperty("id").GetGuid());
        Assert.Equal("duel", incoming.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, incoming.GetProperty("runId").ValueKind);

        var accepted = await OkAsync(await PostAsync(petya, $"interactions/{challenge.InteractionId}/answer", new { commandId = Guid.NewGuid(), accept = true }));

        Assert.Contains("interaction-answered", Types(accepted));
        Assert.Contains("duel-started", Types(accepted));
        var duel = (await StateAsync(_site)).Duels[challenge.InteractionId];
        foreach (var login in new[] { "vasya", "petya", "zritel", "masha", "admin" })
        {
            var client = await _site.SignedInAsync(login);
            var card = Assert.Single((await GetAsync(client, "duels")).EnumerateArray());
            Assert.Equal(duel.DuelId, card.GetProperty("id").GetGuid());
            Assert.Equal(duel.ChallengerId, card.GetProperty("challengerId").GetGuid());
            Assert.Equal(duel.OpponentId, card.GetProperty("opponentId").GetGuid());
            Assert.Equal(duel.AcceptedAt, card.GetProperty("acceptedAt").GetDateTimeOffset());
            Assert.Equal(duel.ExpiresAt, card.GetProperty("expiresAt").GetDateTimeOffset());
            Assert.Equal("active", card.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, card.GetProperty("winnerId").ValueKind);
            Assert.Equal(JsonValueKind.Null, card.GetProperty("runId").ValueKind);
            Assert.Equal(0, card.GetProperty("coins").GetInt32());
            Assert.Equal(JsonValueKind.Null, card.GetProperty("end").ValueKind);
            Assert.Equal(JsonValueKind.Null, card.GetProperty("endedAt").ValueKind);
        }
    }

    [Fact]
    public async Task A_fresh_run_completed_over_http_wins_the_duel_and_its_read_names_the_result()
    {
        var id = await AcceptedDuelAsync(_site);
        var vasya = await _site.SignedInAsync("vasya");
        await ApiCalls.PostOkAsync(vasya, Url("roll"));
        await ApiCalls.PostOkAsync(vasya, Url("start"));
        var runId = (await StateAsync(_site)).Players[_site.Players["vasya"]].ActiveRunId!.Value;

        var completed = await OkAsync(await PostAsync(vasya, "complete", new { commandId = Guid.NewGuid(), difficulty = "normal" }));

        Assert.Contains("duel-settled", Types(completed));
        var state = await StateAsync(_site);
        var duel = state.Duels[id];
        Assert.Equal(DuelStatus.Won, duel.Status);
        Assert.Equal(_site.Players["vasya"], duel.WinnerId);
        Assert.Equal(runId, duel.RunId);
        Assert.Equal(state.Rules.Duels!.RewardCoins, duel.Coins);
        var card = Assert.Single((await GetAsync(await _site.SignedInAsync("zritel"), "duels")).EnumerateArray());
        Assert.Equal("won", card.GetProperty("status").GetString());
        Assert.Equal(duel.WinnerId, card.GetProperty("winnerId").GetGuid());
        Assert.Equal(runId, card.GetProperty("runId").GetGuid());
        Assert.Equal(duel.Coins, card.GetProperty("coins").GetInt32());
        Assert.Equal("completed", card.GetProperty("end").GetString());
        Assert.Equal(duel.EndedAt, card.GetProperty("endedAt").GetDateTimeOffset());
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_game_already_being_played_at_acceptance_does_not_win_the_duel()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await ApiCalls.PostOkAsync(vasya, Url("roll"));
        await ApiCalls.PostOkAsync(vasya, Url("start"));
        var id = await AcceptedDuelAsync(_site);

        await ApiCalls.PostOkAsync(vasya, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });

        Assert.Equal(DuelStatus.Active, (await StateAsync(_site)).Duels[id].Status);
        Assert.Equal("active", Assert.Single((await GetAsync(vasya, "duels")).EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_playing_participant_can_see_their_active_duel_on_their_turn_until_duels_are_switched_off()
    {
        var id = await AcceptedDuelAsync(_site);
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        await ApiCalls.PostOkAsync(vasya, Url("roll"));
        await ApiCalls.PostOkAsync(vasya, Url("start"));

        var card = Assert.Single((await GetAsync(vasya, "")).GetProperty("me").GetProperty("activeDuels").EnumerateArray());
        Assert.Equal(id, card.GetProperty("id").GetGuid());
        Assert.Equal(_site.Players["petya"], card.GetProperty("opponentId").GetGuid());
        Assert.Empty((await GetAsync(petya, "")).GetProperty("me").GetProperty("activeDuels").EnumerateArray());

        _site.Clock.UtcNow = card.GetProperty("expiresAt").GetDateTimeOffset();
        await TickAsync(_site);
        await RulesAsync(_site, Flags(f => f with { Duels = false }));

        var season = await GetAsync(vasya, "");
        Assert.False(season.GetProperty("interactionFeatures").GetProperty("duels").GetBoolean());
        Assert.Empty(season.GetProperty("me").GetProperty("activeDuels").EnumerateArray());
    }

    [Fact]
    public async Task A_public_duel_list_does_not_include_races_of_other_seasons_for_the_same_accounts()
    {
        var own = await AcceptedDuelAsync(_site);
        var foreign = await ForeignInvitationAsync(_site, duel: true, accept: true);
        var spectator = await _site.SignedInAsync("zritel");

        Assert.Equal(own, Assert.Single((await GetAsync(spectator, "duels")).EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(foreign.InvitationId, Assert.Single((await GetAsync(spectator, "duels", foreign.SeasonId)).EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_duel_list_requires_a_session_but_not_a_player_or_a_specific_role()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await (await _site.AnonymousAsync()).GetAsync(Url("duels"), Ct)).StatusCode);
        foreach (var login in new[] { "vasya", "petya", "zritel", "masha", "admin" })
        {
            Assert.Empty((await GetAsync(await _site.SignedInAsync(login), "duels")).EnumerateArray());
        }
    }

    [Fact]
    public async Task Unknown_or_malformed_season_ids_have_no_duel_list()
    {
        var client = await _site.SignedInAsync("vasya");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url("duels", Guid.NewGuid()), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/seasons/not-a-guid/duels", Ct)).StatusCode);
    }

    [Fact]
    public async Task Spectators_and_accounts_outside_the_season_cannot_send_a_challenge()
    {
        var body = new { commandId = Guid.NewGuid(), opponentId = _site.Players["petya"] };
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(await _site.AnonymousAsync(), "duels", body)).StatusCode);
        foreach (var login in new[] { "zritel", "masha", "admin" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(await _site.SignedInAsync(login), "duels", body)).StatusCode);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"commandId\":\"{id}\"}")]
    [InlineData("{\"opponentId\":\"{id}\"}")]
    [InlineData("{\"commandId\":\"{id}\",\"opponentId\":\"not-a-guid\"}")]
    public async Task A_challenge_with_invalid_input_is_a_bad_request(string body)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await RawAsync(await _site.SignedInAsync("vasya"), "duels", body)).StatusCode);
        Assert.Empty((await StateAsync(_site)).Interactions);
    }

    [Fact]
    public async Task An_empty_opponent_is_invalid_and_self_or_unknown_opponents_are_engine_refusals()
    {
        var vasya = await _site.SignedInAsync("vasya");
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(vasya, "duels", new { commandId = Guid.NewGuid(), opponentId = Guid.Empty })).StatusCode);
        await ConflictAsync(await PostAsync(vasya, "duels", new { commandId = Guid.NewGuid(), opponentId = _site.Players["vasya"] }), "duel.onSelf");
        await ConflictAsync(await PostAsync(vasya, "duels", new { commandId = Guid.NewGuid(), opponentId = Guid.NewGuid() }), "player.unknown");
    }

    [Fact]
    public async Task A_player_id_of_another_season_cannot_be_challenged_in_this_one()
    {
        var foreign = await ForeignInvitationAsync(_site, duel: true);
        var other = (await StateOfAsync(_site, foreign.SeasonId)).Interactions[foreign.InvitationId].ToPlayerId;

        await ConflictAsync(await PostAsync(await _site.SignedInAsync("vasya"), "duels", new { commandId = Guid.NewGuid(), opponentId = other }), "player.unknown");

        Assert.Empty((await StateAsync(_site)).Interactions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_duel_challenge_is_declined_or_withdrawn_through_the_shared_interaction_routes(bool withdraw)
    {
        var id = await ChallengedAsync(_site);
        var login = withdraw ? "vasya" : "petya";
        var action = withdraw ? $"interactions/{id}/withdraw" : $"interactions/{id}/answer";

        await OkAsync(await PostAsync(await _site.SignedInAsync(login), action, new { commandId = Guid.NewGuid(), accept = false }));

        var state = await StateAsync(_site);
        Assert.Equal(withdraw ? InteractionStatus.Withdrawn : InteractionStatus.Declined, state.Interactions[id].Status);
        Assert.Empty(state.Duels);
    }

    [Fact]
    public async Task Retrying_a_challenge_command_creates_only_one_invitation()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var body = new { commandId = Guid.NewGuid(), opponentId = _site.Players["petya"] };
        var first = await OkAsync(await PostAsync(vasya, "duels", body));
        var repeated = await OkAsync(await PostAsync(vasya, "duels", body));

        Assert.False(first.GetProperty("duplicate").GetBoolean());
        Assert.True(repeated.GetProperty("duplicate").GetBoolean());
        Assert.Equal(Types(first), Types(repeated));
        Assert.Single((await StateAsync(_site)).Interactions.Values);
    }

    [Fact]
    public async Task Switched_off_duels_refuse_new_challenges_and_the_duel_read()
    {
        await RulesAsync(_site, Flags(f => f with { Duels = false }));
        var vasya = await _site.SignedInAsync("vasya");

        await ConflictAsync(await PostAsync(vasya, "duels", new { commandId = Guid.NewGuid(), opponentId = _site.Players["petya"] }), "feature.disabled");
        await ConflictAsync(await vasya.GetAsync(Url("duels"), Ct), "feature.disabled");
        Assert.Empty((await StateAsync(_site)).Interactions);
    }
}

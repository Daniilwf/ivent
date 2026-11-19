using System.Net;
using System.Text.Json;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Seasons;
using Microsoft.EntityFrameworkCore;
using static GameEvent.Web.Tests.Api.InteractionSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Task 5.20a, EV25/EV28/EV43: private interaction reads and coop commands over HTTP. Actor ids always come from the
/// signed-in account; D-790 allows ending an existing coop or invitation after coop was switched off.
/// </summary>
public sealed class InteractionApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        await CoopOnAsync(_site);
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task The_coop_slice_invites_accepts_projects_and_leaves_one_shared_run()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        await ApiCalls.PostOkAsync(vasya, Url("roll"));
        await ApiCalls.PostOkAsync(vasya, Url("start"));
        var runId = (await StateAsync(_site)).Players[_site.Players["vasya"]].ActiveRunId!.Value;

        var sent = await OkAsync(await PostAsync(vasya, "coop/invite", new { commandId = Guid.NewGuid(), partnerId = _site.Players["petya"] }));

        Assert.Contains("interaction-created", Types(sent));
        var invitation = Assert.Single((await StateAsync(_site)).Interactions.Values);
        var incoming = Assert.Single((await GetAsync(petya, "interactions")).EnumerateArray());
        Assert.Equal(invitation.InteractionId, incoming.GetProperty("id").GetGuid());
        Assert.Equal("coop", incoming.GetProperty("kind").GetString());
        Assert.Equal(_site.Players["vasya"], incoming.GetProperty("fromPlayerId").GetGuid());
        Assert.Equal(_site.Players["petya"], incoming.GetProperty("toPlayerId").GetGuid());
        Assert.Equal(runId, incoming.GetProperty("runId").GetGuid());
        Assert.Equal("pending", incoming.GetProperty("status").GetString());
        Assert.Equal(invitation.CreatedAt, incoming.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(invitation.ExpiresAt, incoming.GetProperty("expiresAt").GetDateTimeOffset());

        var accepted = await OkAsync(await PostAsync(petya, $"interactions/{invitation.InteractionId}/answer", new { commandId = Guid.NewGuid(), accept = true }));

        Assert.Contains("interaction-answered", Types(accepted));
        Assert.Contains("coop-joined", Types(accepted));
        var state = await StateAsync(_site);
        Assert.Equal(InteractionStatus.Accepted, state.Interactions[invitation.InteractionId].Status);
        Assert.Equal(_site.Players["petya"], state.Runs[runId].PartnerId);
        Assert.Equal(runId, state.Players[_site.Players["petya"]].ActiveRunId);
        Assert.False((await GetAsync(vasya, "")).GetProperty("me").GetProperty("isCoopPartner").GetBoolean());
        Assert.True((await GetAsync(petya, "")).GetProperty("me").GetProperty("isCoopPartner").GetBoolean());
        await using (var db = _site.NewDb())
        {
            Assert.Equal(_site.Players["petya"], (await db.Runs.SingleAsync(r => r.Id == runId, Ct)).PartnerId);
            Assert.Equal(InteractionStatus.Accepted, (await db.Interactions.SingleAsync(i => i.Id == invitation.InteractionId, Ct)).Status);
        }

        foreach (var client in new[] { vasya, petya })
        {
            var coop = await GetAsync(client, "coop");
            Assert.Equal(runId, coop.GetProperty("runId").GetGuid());
            Assert.Equal(_site.Players["vasya"], coop.GetProperty("rollerId").GetGuid());
            Assert.Equal(_site.Players["petya"], coop.GetProperty("partnerId").GetGuid());
            Assert.Equal(state.Runs[runId].Coop!.JoinedAt, coop.GetProperty("joinedAt").GetDateTimeOffset());
        }

        var left = await OkAsync(await PostAsync(petya, "coop/leave", new { commandId = Guid.NewGuid() }));

        Assert.Contains("coop-left", Types(left));
        state = await StateAsync(_site);
        Assert.Null(state.Runs[runId].Coop);
        Assert.Equal(TurnPhase.Idle, state.Players[_site.Players["petya"]].Phase);
        Assert.Equal(JsonValueKind.Null, (await GetAsync(petya, "coop")).ValueKind);
        Assert.False((await GetAsync(petya, "")).GetProperty("me").GetProperty("isCoopPartner").GetBoolean());
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task The_private_list_has_only_the_viewers_incoming_and_outgoing_invitations()
    {
        var (_, mine) = await InvitedAsync(_site);
        await MashaAsync(_site);
        await PlayingAsync(_site, _site.Players["masha"]);
        var unrelated = await InviteAsync(_site, "masha", "petya");
        var foreign = await ForeignInvitationAsync(_site);
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");

        var outgoing = await GetAsync(vasya, $"interactions?playerId={_site.Players["petya"]}");
        var incoming = await GetAsync(petya, "interactions");

        Assert.Equal(mine, Assert.Single(outgoing.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(new[] { mine, unrelated }.Order(), incoming.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).Order());
        Assert.DoesNotContain(incoming.EnumerateArray(), x => x.GetProperty("id").GetGuid() == foreign.InvitationId);
    }

    [Fact]
    public async Task A_player_without_a_coop_reads_null_even_when_another_player_has_one()
    {
        await JoinedAsync(_site);
        await MashaAsync(_site);
        var masha = await _site.SignedInAsync("masha");

        Assert.Equal(JsonValueKind.Null, (await GetAsync(masha, $"coop?playerId={_site.Players["petya"]}")).ValueKind);
    }

    [Theory]
    [InlineData("interactions")]
    [InlineData("coop")]
    public async Task Private_reads_refuse_anonymous_spectators_and_accounts_outside_the_season(string action)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await (await _site.AnonymousAsync()).GetAsync(Url(action), Ct)).StatusCode);
        foreach (var login in new[] { "zritel", "masha", "admin" })
        {
            var client = await _site.SignedInAsync(login);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Url(action), Ct)).StatusCode);
        }
    }

    [Theory]
    [InlineData("interactions")]
    [InlineData("coop")]
    public async Task Private_reads_of_an_unknown_season_are_not_found(string action)
    {
        var client = await _site.SignedInAsync("vasya");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url(action, Guid.NewGuid()), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/seasons/not-a-guid/{action}", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_invitation_can_be_declined_and_remains_in_its_participants_list()
    {
        var (_, id) = await InvitedAsync(_site);
        var petya = await _site.SignedInAsync("petya");

        var response = await OkAsync(await PostAsync(petya, $"interactions/{id}/answer", new { commandId = Guid.NewGuid(), accept = false }));

        Assert.Contains("interaction-answered", Types(response));
        Assert.Equal(InteractionStatus.Declined, (await StateAsync(_site)).Interactions[id].Status);
        Assert.Equal("declined", Assert.Single((await GetAsync(petya, "interactions")).EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_inviter_withdraws_his_pending_invitation()
    {
        var (_, id) = await InvitedAsync(_site);
        var vasya = await _site.SignedInAsync("vasya");

        var response = await OkAsync(await PostAsync(vasya, $"interactions/{id}/withdraw", new { commandId = Guid.NewGuid() }));

        Assert.Contains("interaction-withdrawn", Types(response));
        Assert.Equal(InteractionStatus.Withdrawn, (await StateAsync(_site)).Interactions[id].Status);
    }

    [Theory]
    [InlineData("invite")]
    [InlineData("answer")]
    [InlineData("withdraw")]
    [InlineData("leave")]
    public async Task Every_coop_write_refuses_anonymous_spectators_and_players_outside_the_season(string action)
    {
        var (_, id) = action == "leave" ? await JoinedAsync(_site) : await InvitedAsync(_site);
        var path = PathFor(action, id);
        var body = new { commandId = Guid.NewGuid(), partnerId = _site.Players["petya"], accept = true };
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(await _site.AnonymousAsync(), path, body)).StatusCode);
        foreach (var login in new[] { "zritel", "masha", "admin" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(await _site.SignedInAsync(login), path, body)).StatusCode);
        }
    }

    [Theory]
    [InlineData("answer", "vasya")]
    [InlineData("withdraw", "petya")]
    public async Task Being_the_other_participant_does_not_authorize_an_invitation_action(string action, string login)
    {
        var (_, id) = await InvitedAsync(_site);
        var before = (await StateAsync(_site)).Interactions[id];

        await ConflictAsync(await PostAsync(await _site.SignedInAsync(login), PathFor(action, id), new { commandId = Guid.NewGuid(), accept = true }), "interaction.notYours");

        Assert.Equal(before, (await StateAsync(_site)).Interactions[id]);
    }

    [Fact]
    public async Task The_roller_cannot_leave_as_if_he_were_the_partner()
    {
        var (runId, _) = await JoinedAsync(_site);
        await ConflictAsync(await PostAsync(await _site.SignedInAsync("vasya"), "coop/leave", new { commandId = Guid.NewGuid() }), "coop.notPartner");
        Assert.NotNull((await StateAsync(_site)).Runs[runId].Coop);
    }

    [Fact]
    public async Task Actor_ids_in_the_body_never_replace_the_signed_in_player()
    {
        await PlayingAsync(_site, _site.Players["vasya"]);
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        await OkAsync(await PostAsync(vasya, "coop/invite", new { commandId = Guid.NewGuid(), partnerId = _site.Players["petya"], playerId = _site.Players["petya"] }));
        var invitation = Assert.Single((await StateAsync(_site)).Interactions.Values);
        Assert.Equal(_site.Players["vasya"], invitation.FromPlayerId);
        await OkAsync(await PostAsync(petya, $"interactions/{invitation.InteractionId}/answer", new { commandId = Guid.NewGuid(), accept = true, playerId = _site.Players["vasya"] }));
        await OkAsync(await PostAsync(petya, "coop/leave", new { commandId = Guid.NewGuid(), playerId = _site.Players["vasya"] }));
        await OkAsync(await PostAsync(vasya, "coop/invite", new { commandId = Guid.NewGuid(), partnerId = _site.Players["petya"] }));
        var second = (await StateAsync(_site)).Interactions.Values.Single(i => i.Status == InteractionStatus.Pending);
        await OkAsync(await PostAsync(vasya, $"interactions/{second.InteractionId}/withdraw", new { commandId = Guid.NewGuid(), playerId = _site.Players["petya"] }));
        Assert.Equal(InteractionStatus.Withdrawn, (await StateAsync(_site)).Interactions[second.InteractionId].Status);
    }

    [Theory]
    [InlineData("answer")]
    [InlineData("withdraw")]
    public async Task An_invitation_id_from_another_season_cannot_be_used_here(string action)
    {
        var foreign = await ForeignInvitationAsync(_site);
        var login = action == "answer" ? "petya" : "vasya";

        await ConflictAsync(await PostAsync(await _site.SignedInAsync(login), PathFor(action, foreign.InvitationId), new { commandId = Guid.NewGuid(), accept = true }), "interaction.unknown");

        Assert.Equal(InteractionStatus.Pending, (await StateOfAsync(_site, foreign.SeasonId)).Interactions[foreign.InvitationId].Status);
    }

    [Theory]
    [InlineData("answer")]
    [InlineData("withdraw")]
    public async Task Empty_invitation_ids_are_bad_input_and_unknown_ids_are_engine_conflicts(string action)
    {
        var client = await _site.SignedInAsync(action == "answer" ? "petya" : "vasya");
        var body = new { commandId = Guid.NewGuid(), accept = true };
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, PathFor(action, Guid.Empty), body)).StatusCode);
        await ConflictAsync(await PostAsync(client, PathFor(action, Guid.NewGuid()), new { commandId = Guid.NewGuid(), accept = true }), "interaction.unknown");
    }

    [Theory]
    [InlineData("coop/invite", "{}")]
    [InlineData("coop/invite", "{\"commandId\":\"{id}\"}")]
    [InlineData("coop/invite", "{\"commandId\":\"{id}\",\"partnerId\":\"not-a-guid\"}")]
    [InlineData("coop/leave", "{}")]
    [InlineData("interactions/{id}/answer", "{\"commandId\":\"{id}\"}")]
    [InlineData("interactions/{id}/answer", "{\"commandId\":\"{id}\",\"accept\":\"yes\"}")]
    [InlineData("interactions/{id}/withdraw", "{}")]
    public async Task Invalid_write_bodies_are_bad_requests_before_commands(string action, string body)
    {
        var (_, id) = await InvitedAsync(_site);
        var login = action.Contains("answer", StringComparison.Ordinal) || action == "coop/leave" ? "petya" : "vasya";
        var client = await _site.SignedInAsync(login);
        Assert.Equal(HttpStatusCode.BadRequest, (await RawAsync(client, action.Replace("{id}", id.ToString(), StringComparison.Ordinal), body)).StatusCode);
    }

    [Fact]
    public async Task Empty_partner_ids_are_bad_input_and_self_or_missing_partners_are_refused()
    {
        await PlayingAsync(_site, _site.Players["vasya"]);
        var vasya = await _site.SignedInAsync("vasya");
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(vasya, "coop/invite", new { commandId = Guid.NewGuid(), partnerId = Guid.Empty })).StatusCode);
        await ConflictAsync(await PostAsync(vasya, "coop/invite", new { commandId = Guid.NewGuid(), partnerId = _site.Players["vasya"] }), "coop.partnerInvalid");
        await ConflictAsync(await PostAsync(vasya, "coop/invite", new { commandId = Guid.NewGuid(), partnerId = Guid.NewGuid() }), "coop.partnerInvalid");
    }

    [Fact]
    public async Task Switching_coop_off_refuses_new_invites_but_still_lists_and_declines_existing_ones()
    {
        var (_, id) = await InvitedAsync(_site);
        await RulesAsync(_site, Flags(f => f with { Coop = false }));
        var petya = await _site.SignedInAsync("petya");
        var vasya = await _site.SignedInAsync("vasya");
        Assert.Equal(id, Assert.Single((await GetAsync(petya, "interactions")).EnumerateArray()).GetProperty("id").GetGuid());
        await ConflictAsync(await PostAsync(vasya, "coop/invite", new { commandId = Guid.NewGuid(), partnerId = _site.Players["petya"] }), "feature.disabled");
        await ConflictAsync(await PostAsync(petya, $"interactions/{id}/answer", new { commandId = Guid.NewGuid(), accept = true }), "feature.disabled");
        await OkAsync(await PostAsync(petya, $"interactions/{id}/answer", new { commandId = Guid.NewGuid(), accept = false }));
        Assert.Equal(InteractionStatus.Declined, (await StateAsync(_site)).Interactions[id].Status);
    }

    [Fact]
    public async Task Switching_coop_off_still_allows_withdrawing_an_existing_invitation()
    {
        var (_, id) = await InvitedAsync(_site);
        await RulesAsync(_site, Flags(f => f with { Coop = false }));

        await OkAsync(await PostAsync(await _site.SignedInAsync("vasya"), $"interactions/{id}/withdraw", new { commandId = Guid.NewGuid() }));

        Assert.Equal(InteractionStatus.Withdrawn, (await StateAsync(_site)).Interactions[id].Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Switching_coop_off_keeps_the_live_coop_readable_and_the_partner_may_leave(bool duelsOn)
    {
        var (runId, _) = await JoinedAsync(_site);
        await RulesAsync(_site, Flags(f => f with { Coop = false, Duels = duelsOn }));
        var petya = await _site.SignedInAsync("petya");
        Assert.Equal(runId, (await GetAsync(petya, "coop")).GetProperty("runId").GetGuid());
        Assert.True((await GetAsync(petya, "")).GetProperty("me").GetProperty("hasInteractionActions").GetBoolean());

        await OkAsync(await PostAsync(petya, "coop/leave", new { commandId = Guid.NewGuid() }));

        Assert.Null((await StateAsync(_site)).Runs[runId].Coop);
    }

    [Theory]
    [InlineData("invite")]
    [InlineData("answer")]
    [InlineData("withdraw")]
    [InlineData("leave")]
    public async Task Retrying_the_same_command_returns_its_result_without_applying_it_twice(string action)
    {
        Guid id;
        if (action == "invite")
        {
            await PlayingAsync(_site, _site.Players["vasya"]);
            id = Guid.Empty;
        }
        else
        {
            (_, id) = action == "leave" ? await JoinedAsync(_site) : await InvitedAsync(_site);
        }

        var login = action is "answer" or "leave" ? "petya" : "vasya";
        var client = await _site.SignedInAsync(login);
        var body = new { commandId = Guid.NewGuid(), partnerId = _site.Players["petya"], accept = true };
        var first = await OkAsync(await PostAsync(client, PathFor(action, id), body));
        var state = await StateAsync(_site);
        var repeated = await OkAsync(await PostAsync(client, PathFor(action, id), body));

        Assert.False(first.GetProperty("duplicate").GetBoolean());
        Assert.True(repeated.GetProperty("duplicate").GetBoolean());
        Assert.Equal(Types(first), Types(repeated));
        Assert.Equal(state, await StateAsync(_site));
    }

    [Fact]
    public async Task A_spammy_inviter_is_limited_without_blocking_the_recipient()
    {
        await PlayingAsync(_site, _site.Players["vasya"]);
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        for (var i = 0; i < 20; i++)
        {
            var response = await PostAsync(vasya, "coop/invite", new { commandId = Guid.NewGuid(), partnerId = _site.Players["petya"] });
            Assert.Equal(i == 0 ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode);
        }

        var limited = await PostAsync(vasya, "coop/invite", new { commandId = Guid.NewGuid(), partnerId = _site.Players["petya"] });

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var invitation = Assert.Single((await StateAsync(_site)).Interactions.Values);
        await OkAsync(await PostAsync(petya, $"interactions/{invitation.InteractionId}/answer", new { commandId = Guid.NewGuid(), accept = false }));
        Assert.Equal(InteractionStatus.Declined, (await StateAsync(_site)).Interactions[invitation.InteractionId].Status);
    }

    [Fact]
    public async Task The_coop_openapi_response_preserves_its_view_and_accepts_the_runtime_null()
    {
        var response = await _site.CreateClient().GetAsync("/openapi/v1.json", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = document.RootElement;
        var schema = root.GetProperty("paths").GetProperty("/api/seasons/{seasonId}/coop")
            .GetProperty("get").GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");

        Assert.True(HasCoopViewReference(schema), "The generated client must retain the CoopView response, not unknown or any.");
        var viewProperties = root.GetProperty("components").GetProperty("schemas").GetProperty("CoopView").GetProperty("properties");
        foreach (var property in new[] { "runId", "rollerId", "partnerId", "joinedAt" })
        {
            Assert.True(viewProperties.TryGetProperty(property, out _), $"CoopView must keep its {property} property.");
        }

        Assert.True(OnlyCoopViewOrNull(schema), "Every response alternative must be the coop view or null, not an unbounded schema.");
        Assert.True(AllowsNull(schema, root), "A player without a coop receives JSON null: OpenAPI 200 must allow it too.");
    }

    private static bool HasCoopViewReference(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
        && ((schema.TryGetProperty("$ref", out var reference) && reference.GetString() == "#/components/schemas/CoopView")
            || new[] { "anyOf", "oneOf", "allOf" }.Any(name => schema.TryGetProperty(name, out var branches)
                && branches.EnumerateArray().Any(HasCoopViewReference)));

    private static bool OnlyCoopViewOrNull(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.False)
        {
            return true;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (schema.TryGetProperty("$ref", out var reference))
        {
            return reference.GetString() == "#/components/schemas/CoopView";
        }

        if (schema.TryGetProperty("type", out var type)
            && (type.ValueKind == JsonValueKind.String && type.GetString() == "null"
                || type.ValueKind == JsonValueKind.Array && type.GetArrayLength() > 0
                && type.EnumerateArray().All(value => value.GetString() == "null")))
        {
            return true;
        }

        foreach (var name in new[] { "anyOf", "oneOf" })
        {
            if (schema.TryGetProperty(name, out var branches))
            {
                return branches.GetArrayLength() > 0 && branches.EnumerateArray().All(OnlyCoopViewOrNull);
            }
        }

        return schema.TryGetProperty("allOf", out var intersection) && intersection.EnumerateArray().Any(OnlyCoopViewOrNull);
    }

    private static bool AllowsNull(JsonElement schema, JsonElement root)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return schema.GetBoolean();
        }

        if (schema.TryGetProperty("nullable", out var nullable) && nullable.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        var allowed = true;
        if (schema.TryGetProperty("type", out var type))
        {
            allowed &= type.ValueKind == JsonValueKind.String
                ? type.GetString() == "null"
                : type.EnumerateArray().Any(value => value.GetString() == "null");
        }

        if (schema.TryGetProperty("$ref", out var reference))
        {
            var target = root;
            var path = reference.GetString()!;
            Assert.StartsWith("#/", path, StringComparison.Ordinal);
            foreach (var part in path[2..].Split('/'))
            {
                target = target.GetProperty(part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
            }

            allowed &= AllowsNull(target, root);
        }

        foreach (var name in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (schema.TryGetProperty(name, out var branches))
            {
                var count = branches.EnumerateArray().Count(branch => AllowsNull(branch, root));
                allowed &= name switch
                {
                    "anyOf" => count > 0,
                    "oneOf" => count == 1,
                    _ => count == branches.GetArrayLength(),
                };
            }
        }

        return allowed;
    }

    private static string PathFor(string action, Guid invitationId) => action switch
    {
        "invite" => "coop/invite",
        "answer" => $"interactions/{invitationId}/answer",
        "withdraw" => $"interactions/{invitationId}/withdraw",
        "leave" => "coop/leave",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}

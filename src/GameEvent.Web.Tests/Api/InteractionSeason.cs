using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Queue;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Public-contract setup for task 5.20: preparation uses commands and existing endpoints, while the new HTTP calls
/// are plain JSON and compile before the interactions endpoints exist.
/// </summary>
internal static class InteractionSeason
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string Url(string action, Guid? seasonId = null) => $"/api/seasons/{seasonId ?? SiteFactory.SeasonId}/{action}";

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string action, object body, Guid? seasonId = null) =>
        client.PostAsJsonAsync(Url(action, seasonId), body, Ct);

    public static Task<HttpResponseMessage> RawAsync(HttpClient client, string action, string body) =>
        client.PostAsync(Url(action), new StringContent(body.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal), System.Text.Encoding.UTF8, "application/json"), Ct);

    public static async Task<JsonElement> GetAsync(HttpClient client, string action, Guid? seasonId = null) =>
        await ApiCalls.OkAsync(await client.GetAsync(Url(action, seasonId), Ct));

    public static async Task<JsonElement> OkAsync(HttpResponseMessage response) => await ApiCalls.OkAsync(response);

    public static IReadOnlyList<string> Types(JsonElement body) =>
        [.. body.GetProperty("events").EnumerateArray().Select(item => item.GetProperty("type").GetString()!)];

    public static async Task ConflictAsync(HttpResponseMessage response, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    public static async Task<(Guid RunId, Guid InvitationId)> InvitedAsync(SiteFactory site)
    {
        var run = await PlayingAsync(site, site.Players["vasya"]);
        var id = await InviteAsync(site, "vasya", "petya");
        return (run, id);
    }

    public static async Task<Guid> InviteAsync(SiteFactory site, string from, string to)
    {
        var before = (await StateAsync(site)).Interactions.Keys.ToHashSet();
        await site.SendAsync(new InviteToCoop(site.Players[from], site.Players[to]));
        return (await StateAsync(site)).Interactions.Keys.Single(id => !before.Contains(id));
    }

    public static async Task<(Guid RunId, Guid InvitationId)> JoinedAsync(SiteFactory site)
    {
        var prepared = await InvitedAsync(site);
        await site.SendAsync(new AnswerInvitation(site.Players["petya"], prepared.InvitationId, true));
        return prepared;
    }

    public static async Task<Guid> ChallengedAsync(SiteFactory site)
    {
        var before = (await StateAsync(site)).Interactions.Keys.ToHashSet();
        await site.SendAsync(new Engine.Duels.ChallengeToDuel(site.Players["vasya"], site.Players["petya"]));
        return (await StateAsync(site)).Interactions.Keys.Single(id => !before.Contains(id));
    }

    public static async Task<Guid> AcceptedDuelAsync(SiteFactory site)
    {
        var challenge = await ChallengedAsync(site);
        await site.SendAsync(new AnswerInvitation(site.Players["petya"], challenge, true));
        return challenge;
    }

    public static async Task<SeasonState> StateOfAsync(SiteFactory site, Guid seasonId)
    {
        await using var db = site.NewDb();
        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, seasonId, Ct);
        return state;
    }

    /// <summary>The same accounts in another season have their own player and interaction ids.</summary>
    public static async Task<(Guid SeasonId, Guid InvitationId)> ForeignInvitationAsync(SiteFactory site, bool duel = false, bool accept = false)
    {
        var id = await site.CreateSeasonAsync();
        var rules = RulesetJson.Default();
        await SendAsync(new ChangeRuleset(rules with { Features = rules.Features with { Coop = true, Duels = true } }));
        await SendAsync(new ChangeSeasonStatus(SeasonStatus.Active));
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        await SendAsync(new AddSeasonPlayer(from, site.Users["vasya"], "vasya"));
        await SendAsync(new AddSeasonPlayer(to, site.Users["petya"], "petya"));
        if (duel)
        {
            await SendAsync(new Engine.Duels.ChallengeToDuel(from, to));
        }
        else
        {
            await SendAsync(new Engine.Rolls.RollGame(from));
            await SendAsync(new StartRun(from));
            await SendAsync(new InviteToCoop(from, to));
        }

        var invitationId = Assert.Single((await StateOfAsync(site, id)).Interactions.Values).InteractionId;
        if (accept)
        {
            await SendAsync(new AnswerInvitation(to, invitationId, true));
        }

        return (id, invitationId);

        async Task SendAsync(ICommand command)
        {
            var bus = site.Services.GetRequiredService<CommandBus>();
            var result = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), id, command, AuthorId: null), Ct);
            Assert.True(result.IsAccepted, result.Rejection?.ToString());
        }
    }
}

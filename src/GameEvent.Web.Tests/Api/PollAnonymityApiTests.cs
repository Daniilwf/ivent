using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static GameEvent.Web.Tests.Api.PollSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The anonymous poll over the whole site (task 5.19; D-515, D-541, D-726, D-831): nobody, the admin included, learns who
/// voted or for what — not from the poll's card and list, the feed, the hub (a changed vote is not even listed), the admin's
/// command journal, the undo's refusal, the «Ошибки» page or a player's last activity. The user is still kept by the
/// engine for «one vote» and the undo (the season export, for debugging, is the one place that names them). Every vote
/// here goes through the vote endpoint as the voter's own account, as on the site.
/// </summary>
public sealed class PollAnonymityApiTests : IAsyncLifetime
{
    private readonly FailingClock _clock;
    private readonly SiteFactory _site;

    public PollAnonymityApiTests()
    {
        FailingClock? clock = null;
        _site = new SiteFactory
        {
            ExtraServices = services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(_ => clock!);
            },
        };
        _clock = clock = new FailingClock(_site.Clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        await PollsOnAsync(_site);
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    /// <summary>Everything that names Вася: his account, his player, his login and name.</summary>
    private string[] Vasya() => [_site.Users["vasya"].ToString(), _site.Players["vasya"].ToString(), "vasya"];

    private void AssertNamesNobody(string text, string where, params string[] secrets)
    {
        foreach (var secret in Vasya().Concat(secrets))
        {
            Assert.False(text.Contains(secret, StringComparison.OrdinalIgnoreCase), $"{where} reveals «{secret}»: {text}");
        }
    }

    /// <summary>An anonymous poll in which Вася voted «Финиш» and then changed it to «Реролл», by known command ids.</summary>
    private async Task<(Guid PollId, Guid Cast, Guid Change)> VotedAsync()
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson(anonymous: true));
        var vasya = await _site.SignedInAsync("vasya");
        var (cast, change) = (Guid.NewGuid(), Guid.NewGuid());
        await VoteOkAsync(vasya, pollId, "Финиш", cast);
        await VoteOkAsync(vasya, pollId, "Реролл", change);
        return (pollId, cast, change);
    }

    [Fact]
    public async Task The_card_and_the_list_name_no_voter_to_anyone_and_show_no_counts_before_the_close()
    {
        var (pollId, _, _) = await VotedAsync();

        foreach (var login in new[] { "admin", "petya", "zritel" })
        {
            var client = await _site.SignedInAsync(login);
            var card = await CardAsync(client, pollId);
            Assert.True(card.GetProperty("anonymous").GetBoolean());
            Assert.Equal(JsonValueKind.Null, card.GetProperty("counts").ValueKind);
            Assert.Equal(JsonValueKind.Null, card.GetProperty("myVote").ValueKind);
            AssertNamesNobody(card.GetRawText(), $"the card to {login}");
            AssertNamesNobody(await client.GetStringAsync(Url("polls"), Ct), $"the list to {login}");
        }

        // The voter himself sees his own vote, and only it
        Assert.Equal("Реролл", (await CardAsync(await _site.SignedInAsync("vasya"), pollId)).GetProperty("myVote").GetString());
    }

    [Fact]
    public async Task After_the_close_the_counts_are_public_and_still_name_nobody()
    {
        var (pollId, _, _) = await VotedAsync();
        var admin = await _site.SignedInAsync("admin");
        await ApiCalls.PostOkAsync(admin, AdminUrl($"polls/{pollId}/close"));

        var card = await CardAsync(admin, pollId);

        Assert.Equal("Реролл", card.GetProperty("result").GetString());
        Assert.Equal(1, card.GetProperty("counts").EnumerateArray().Single(c => c.GetProperty("optionId").GetString() == "Реролл").GetProperty("votes").GetInt32());
        AssertNamesNobody(card.GetRawText(), "the closed card");
    }

    [Fact]
    public async Task The_feed_shows_a_vote_cast_without_its_voter_and_no_changed_vote_at_all()
    {
        await VotedAsync();

        foreach (var login in new[] { "admin", "petya", "vasya" })
        {
            using var feed = JsonDocument.Parse(await (await _site.SignedInAsync(login)).GetStringAsync(Url("feed"), Ct));
            var entries = feed.RootElement.GetProperty("entries").EnumerateArray().ToList();
            Assert.DoesNotContain(entries, e => e.GetProperty("type").GetString() == "vote-changed");
            var vote = Assert.Single(entries, e => e.GetProperty("type").GetString() == "vote-cast");
            AssertNamesNobody(vote.GetRawText(), $"the feed to {login}", "Финиш", "Реролл");
        }
    }

    [Fact]
    public async Task The_hub_never_lists_a_changed_vote_live_or_on_a_resume()
    {
        // D-726: watchers would learn that someone changed an anonymous vote
        var (connection, updates) = await WatchAsync(_site, "petya");
        await using var hub = connection;
        var (pollId, _, _) = await VotedAsync();
        await ApiCalls.PostOkAsync(await _site.SignedInAsync("admin"), AdminUrl($"polls/{pollId}/close"));

        await WaitForAsync(updates, "poll-closed");
        lock (updates)
        {
            Assert.DoesNotContain(updates, u => u.Types.Contains("vote-changed"));
            Assert.Contains(updates, u => u.Types.Contains("vote-cast"));
        }

        var resumed = await connection.InvokeAsync<Realtime.SeasonJoin>(nameof(Realtime.SeasonHub.Resume), SiteFactory.SeasonId, 0L, Ct);
        Assert.DoesNotContain(resumed.Missed, u => u.Types.Contains("vote-changed"));
    }

    [Fact]
    public async Task The_admins_command_journal_names_no_author_or_player_of_an_anonymous_vote()
    {
        var (_, cast, change) = await VotedAsync();
        var admin = await _site.SignedInAsync("admin");

        using var journal = JsonDocument.Parse(await admin.GetStringAsync(AdminUrl("commands"), Ct));
        var rows = journal.RootElement.EnumerateArray().ToList();

        foreach (var id in new[] { cast, change })
        {
            var row = Assert.Single(rows, r => r.GetProperty("commandId").GetGuid() == id);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("authorName").ValueKind);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("playerName").ValueKind);
            AssertNamesNobody(row.GetRawText(), "the journal row");
        }
    }

    [Fact]
    public async Task The_undo_refusal_names_the_votes_by_command_only_and_an_undone_vote_names_nobody_in_the_feed()
    {
        var admin = await _site.SignedInAsync("admin");
        var start = Guid.NewGuid();
        await ApiCalls.PostOkAsync(admin, AdminUrl("polls"), new { commandId = start, poll = NominationJson(anonymous: true) });
        var pollId = Assert.Single((await StateAsync(_site)).Polls.Keys);
        var cast = Guid.NewGuid();
        await VoteOkAsync(await _site.SignedInAsync("vasya"), pollId, "Финиш", cast);

        // The start waits for its votes (D-722): the refusal lists them, but not whose they are
        var refused = await admin.PostAsJsonAsync(AdminUrl("undo"), new { commandId = Guid.NewGuid(), targetCommandId = start, comment = "Ошибка" }, Ct);
        await AssertConflictAsync(refused, "undo.dependents");
        var body = await refused.Content.ReadAsStringAsync(Ct);
        Assert.Contains(cast.ToString(), body, StringComparison.OrdinalIgnoreCase);
        AssertNamesNobody(body, "the undo refusal", "Финиш");

        await ApiCalls.PostOkAsync(admin, AdminUrl("undo"), new { commandId = Guid.NewGuid(), targetCommandId = cast, comment = "Ошибка" });

        using var feed = JsonDocument.Parse(await admin.GetStringAsync(Url("feed"), Ct));
        foreach (var entry in feed.RootElement.GetProperty("entries").EnumerateArray().Where(e => e.GetProperty("type").GetString() is "command-undone" or "vote-cast"))
        {
            AssertNamesNobody(entry.GetRawText(), "the feed after the undo", "Финиш");
        }
    }

    [Fact]
    public async Task A_failed_vote_on_the_errors_page_names_no_user()
    {
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson(anonymous: true));
        var vasya = await _site.SignedInAsync("vasya");

        _clock.Armed = true;
        HttpResponseMessage failed;
        try
        {
            failed = await VoteAsync(vasya, pollId, "Финиш");
        }
        finally
        {
            _clock.Armed = false;
        }

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        using var errors = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/errors", Ct));
        var entry = Assert.Single(errors.RootElement.EnumerateArray(), e => e.GetProperty("path").GetString()!.EndsWith("/vote", StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("userLogin").ValueKind);
        AssertNamesNobody(entry.GetRawText(), "the errors page");
    }

    [Fact]
    public async Task An_anonymous_vote_is_not_the_voters_last_activity()
    {
        await VotedAsync();

        using var players = JsonDocument.Parse(await (await _site.SignedInAsync("admin")).GetStringAsync(AdminUrl("players"), Ct));

        var vasya = players.RootElement.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == _site.Players["vasya"]);
        Assert.Equal(JsonValueKind.Null, vasya.GetProperty("lastActionAt").ValueKind);
    }

    [Fact]
    public async Task In_a_poll_that_is_not_anonymous_the_feed_names_the_voter()
    {
        // The other side of D-515: the rule hides only what it must
        var admin = await _site.SignedInAsync("admin");
        var pollId = await StartPollAsync(_site, admin, NominationJson());
        await VoteOkAsync(await _site.SignedInAsync("vasya"), pollId, "Финиш");

        using var feed = JsonDocument.Parse(await admin.GetStringAsync(Url("feed"), Ct));

        var vote = Assert.Single(feed.RootElement.GetProperty("entries").EnumerateArray(), e => e.GetProperty("type").GetString() == "vote-cast");
        Assert.Contains("vasya", vote.GetRawText(), StringComparison.Ordinal);
    }
}

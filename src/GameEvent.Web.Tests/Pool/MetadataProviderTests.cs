using System.Net;
using System.Text;
using GameEvent.Infrastructure.Pool.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameEvent.Web.Tests.Pool;

/// <summary>
/// The pool's outside services with the network replaced (D-6, D-28, D-29, D-118): what each reads from an answer, and
/// that a bad answer throws for the caller to turn into «no data».
/// </summary>
public sealed class MetadataProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Steam ----

    [Fact]
    public async Task Steam_prefers_the_exact_title_and_reads_the_year()
    {
        var steam = new SteamCoverProvider(Http(r => r.RequestUri!.AbsolutePath switch
        {
            "/api/storesearch/" => Json("""{"total":2,"items":[{"id":620,"name":"Portal 2"},{"id":400,"name":"Portal"}]}"""),
            "/api/appdetails" => Json("""{"400":{"success":true,"data":{"name":"Portal","release_date":{"coming_soon":false,"date":"10 Oct, 2007"}}}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }), NullLogger<SteamCoverProvider>.Instance);

        var cover = await steam.FindCoverAsync("portal", Ct);

        Assert.Equal(
            new CoverCandidate(
                "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/400/library_600x900.jpg",
                2007,
                "400",
                "steam",
                "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/400/header.jpg"),
            cover);
    }

    [Fact]
    public async Task Steam_without_an_exact_title_takes_the_first_and_survives_missing_details()
    {
        var steam = new SteamCoverProvider(Http(r => r.RequestUri!.AbsolutePath == "/api/storesearch/"
            ? Json("""{"items":[{"id":1,"name":"Silent Hill 2"}]}""")
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)), NullLogger<SteamCoverProvider>.Instance);

        var cover = await steam.FindCoverAsync("Silent Hill", Ct);

        Assert.Equal(("1", null), (cover?.SteamAppId, cover?.Year));
    }

    [Fact]
    public async Task Steam_details_that_time_out_or_come_odd_leave_the_cover_without_a_year()
    {
        foreach (var details in new Func<HttpResponseMessage>[] { () => throw new TaskCanceledException("timed out"), () => Json("[1, 2]") })
        {
            var steam = new SteamCoverProvider(
                Http(r => r.RequestUri!.AbsolutePath == "/api/storesearch/" ? Json("""{"items":[{"id":400,"name":"Portal"}]}""") : details()),
                NullLogger<SteamCoverProvider>.Instance);

            var cover = await steam.FindCoverAsync("Portal", Ct);

            Assert.Equal(("400", null), (cover?.SteamAppId, cover?.Year));
        }
    }

    [Fact]
    public async Task Steam_with_nothing_found_has_no_cover()
    {
        var steam = new SteamCoverProvider(Http(_ => Json("""{"total":0,"items":[]}""")), NullLogger<SteamCoverProvider>.Instance);

        Assert.Null(await steam.FindCoverAsync("Nonexistent", Ct));
    }

    [Fact]
    public async Task Steam_that_is_down_throws_for_the_caller()
    {
        var steam = new SteamCoverProvider(Http(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), NullLogger<SteamCoverProvider>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => steam.FindCoverAsync("Portal", Ct));
    }

    [Fact]
    public async Task Steam_answering_garbage_throws_for_the_caller()
    {
        var steam = new SteamCoverProvider(Http(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>oops</html>") }), NullLogger<SteamCoverProvider>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => steam.FindCoverAsync("Portal", Ct));
    }

    [Theory]
    [InlineData("10 Oct, 2007", 2007)]
    [InlineData("Oct 10, 2007", 2007)]
    [InlineData("Q1 2025", 2025)]
    [InlineData("1998", 1998)]
    [InlineData("Coming soon", null)]
    [InlineData(null, null)]
    public void Steam_year_is_read_from_its_dates(string? date, int? year) => Assert.Equal(year, SteamCoverProvider.YearOf(date));

    // ---- IGDB ----

    [Fact]
    public async Task Igdb_without_keys_asks_nothing()
    {
        var asked = 0;
        var igdb = new IgdbCoverProvider(Http(_ =>
        {
            asked++;
            return Json("{}");
        }), new MetadataSettings(), Clock());

        Assert.Null(await igdb.FindCoverAsync("Portal", Ct));
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task Igdb_gets_a_token_once_and_reads_the_cover_and_the_year()
    {
        var tokens = 0;
        var queries = new List<string>();
        var igdb = new IgdbCoverProvider(Http(r =>
        {
            if (r.RequestUri!.Host == "id.twitch.tv")
            {
                tokens++;
                return Json("""{"access_token":"t","expires_in":5000000}""");
            }

            queries.Add(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("Bearer t", r.Headers.Authorization?.ToString());
            return Json("""[{"name":"Portal","first_release_date":1191974400,"cover":{"image_id":"co1x5c"}}]""");
        }), new MetadataSettings { IgdbClientId = "id", IgdbClientSecret = "secret" }, Clock());

        var first = await igdb.FindCoverAsync("Portal \"GOTY\"", Ct);
        await igdb.FindCoverAsync("Portal", Ct);

        Assert.Equal(new CoverCandidate("https://images.igdb.com/igdb/image/upload/t_cover_big/co1x5c.jpg", 2007, null, "igdb"), first);
        Assert.Equal(1, tokens);
        Assert.Contains("search \"Portal \\\"GOTY\\\"\";", queries[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Igdb_drops_a_revoked_token_and_asks_once_more()
    {
        var tokens = 0;
        var searches = 0;
        var igdb = new IgdbCoverProvider(Http(r =>
        {
            if (r.RequestUri!.Host == "id.twitch.tv")
            {
                tokens++;
                return Json($$"""{"access_token":"t{{tokens}}","expires_in":5000000}""");
            }

            searches++;
            return r.Headers.Authorization?.Parameter == "t1"
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Json("""[{"name":"Portal","cover":{"image_id":"co1"}}]""");
        }), new MetadataSettings { IgdbClientId = "id", IgdbClientSecret = "secret" }, Clock());

        var cover = await igdb.FindCoverAsync("Portal", Ct);

        Assert.NotNull(cover);
        Assert.Equal((2, 2), (tokens, searches));
    }

    [Fact]
    public void The_settings_never_print_the_igdb_secret()
    {
        Assert.DoesNotContain("top-secret", new MetadataSettings { IgdbClientId = "id", IgdbClientSecret = "top-secret" }.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Igdb_refuses_an_odd_image_id()
    {
        var igdb = new IgdbCoverProvider(Http(r => r.RequestUri!.Host == "id.twitch.tv"
            ? Json("""{"access_token":"t","expires_in":3600}""")
            : Json("""[{"name":"Portal","cover":{"image_id":"../../evil"}}]""")), new MetadataSettings { IgdbClientId = "id", IgdbClientSecret = "secret" }, Clock());

        Assert.Null(await igdb.FindCoverAsync("Portal", Ct));
    }

    // ---- HowLongToBeat ----

    [Fact]
    public async Task Hltb_is_off_without_its_address()
    {
        var asked = 0;
        var hltb = new HltbHoursProvider(Http(_ =>
        {
            asked++;
            return Json("{}");
        }), new MetadataSettings());

        Assert.Null(await hltb.FindHoursAsync("Portal", Ct));
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task Hltb_asks_only_its_own_site()
    {
        var asked = 0;
        var hltb = new HltbHoursProvider(
            Http(_ =>
            {
                asked++;
                return Json("""{"data":[{"game_name":"Portal","comp_main":36000}]}""");
            }),
            new MetadataSettings { HltbSearchUrl = "https://evil.example/api/search" });

        Assert.Null(await hltb.FindHoursAsync("Portal", Ct));
        Assert.Equal(0, asked);
    }

    [Theory]
    [InlineData(36000, 10)]
    [InlineData(11000, 3)]
    [InlineData(12700, 3.5)]
    public async Task Hltb_main_story_is_rounded_to_half_an_hour(long seconds, double hours)
    {
        var hltb = new HltbHoursProvider(
            Http(_ => Json($$"""{"data":[{"game_name":"Portal","comp_main":{{seconds}}}]}""")),
            new MetadataSettings { HltbSearchUrl = "https://howlongtobeat.com/api/search" });

        Assert.Equal((decimal)hours, await hltb.FindHoursAsync("Portal", Ct));
    }

    [Fact]
    public async Task Hltb_without_a_main_story_time_has_no_hours()
    {
        var hltb = new HltbHoursProvider(
            Http(_ => Json("""{"data":[{"game_name":"Portal","comp_main":0}]}""")),
            new MetadataSettings { HltbSearchUrl = "https://howlongtobeat.com/api/search" });

        Assert.Null(await hltb.FindHoursAsync("Portal", Ct));
    }

    [Fact]
    public async Task Manual_hours_are_always_for_the_admin_to_enter()
    {
        Assert.Null(await new ManualHours().FindHoursAsync("Portal", Ct));
    }

    // ---- Helpers ----

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> answer) => new(new Answering(answer));

    private static TimeProvider Clock() => TimeProvider.System;

    private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }
}

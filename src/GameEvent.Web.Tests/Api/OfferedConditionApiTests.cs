using System.Text.Json;
using static GameEvent.Web.Tests.Api.EventSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// SPEC «Условия от ивентов», D-633: a rolled condition must be visible before the player fixes the starting difficulty.
/// The waiting modifier is consumed by the roll, but its snapshot remains on the single offer or each game option.
/// </summary>
public sealed class OfferedConditionApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_rolled_condition_is_visible_on_every_offer_before_starting_or_choosing(int choiceCount)
    {
        var (_, vasya) = await RollAsync(NotEvenSweating, choiceCount);

        var me = await MeAsync(vasya);

        Assert.Empty(me.GetProperty("waitingConditions").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("activeRun").ValueKind);
        var offers = Offers(me, choiceCount);
        await AssertIntactAsync(_site);
        foreach (var offer in offers)
        {
            var condition = Assert.Single(offer.GetProperty("conditions").EnumerateArray());
            Assert.Equal("not-even-sweating", condition.GetProperty("objectId").GetString());
            Assert.Equal("Даже не вспотел", condition.GetProperty("name").GetString());
            Assert.Equal("bad", condition.GetProperty("kind").GetString());
            Assert.False(condition.GetProperty("hostile").GetBoolean());
            Assert.Equal("hard", condition.GetProperty("difficultyAtLeast").GetString());
            Assert.Equal(JsonValueKind.Null, condition.GetProperty("difficultyAtMost").ValueKind);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Switching_events_off_hides_the_offer_conditions_without_discarding_the_offer(int choiceCount)
    {
        var (admin, vasya) = await RollAsync(NotEvenSweating, choiceCount);
        var before = Offers(await MeAsync(vasya), choiceCount).Select(o => o.GetProperty("id").GetGuid()).ToArray();

        await SwitchEventsAsync(admin, on: false);

        var me = await MeAsync(vasya);
        var offers = Offers(me, choiceCount);
        Assert.False(me.GetProperty("eventsEnabled").GetBoolean());
        Assert.Equal(before, offers.Select(o => o.GetProperty("id").GetGuid()));
        Assert.All(offers, AssertNoConditions);
        await AssertIntactAsync(_site);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_roll_without_a_condition_has_no_offer_conditions(int choiceCount)
    {
        var (_, vasya) = await RollAsync(Tailwind, choiceCount);

        var me = await MeAsync(vasya);

        Assert.True(me.GetProperty("eventsEnabled").GetBoolean());
        Assert.All(Offers(me, choiceCount), AssertNoConditions);
        await AssertIntactAsync(_site);
    }

    private async Task<(HttpClient Admin, HttpClient Vasya)> RollAsync(string badEvent, int choiceCount)
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, badEvent);
        await DropAsync(vasya);
        if (choiceCount > 1)
        {
            await RulesAsync(_site, r => r with { Roll = r.Roll with { ChoiceCount = choiceCount } });
        }
        await ApiCalls.PostOkAsync(vasya, $"{Season}/roll");
        return (admin, vasya);
    }

    private static JsonElement[] Offers(JsonElement me, int choiceCount)
    {
        if (choiceCount == 1)
        {
            Assert.Equal(JsonValueKind.Null, me.GetProperty("choice").ValueKind);
            Assert.Equal(JsonValueKind.Object, me.GetProperty("offer").ValueKind);
            return [me.GetProperty("offer")];
        }

        Assert.Equal(JsonValueKind.Null, me.GetProperty("offer").ValueKind);
        var choice = me.GetProperty("choice");
        Assert.Equal("game", choice.GetProperty("kind").GetString());
        var offers = choice.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("game")).ToArray();
        Assert.Equal(choiceCount, offers.Length);
        return offers;
    }

    private static void AssertNoConditions(JsonElement offer)
    {
        if (offer.TryGetProperty("conditions", out var conditions) && conditions.ValueKind != JsonValueKind.Null)
        {
            Assert.Empty(conditions.EnumerateArray());
        }
    }
}

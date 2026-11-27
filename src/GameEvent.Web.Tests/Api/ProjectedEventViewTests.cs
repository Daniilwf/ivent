using System.Text.Json;
using static GameEvent.Web.Tests.Api.EventSeason;

namespace GameEvent.Web.Tests.Api;

/// <summary>D-828: the event parts of the player view come from the projected turn, runs and manual effects.</summary>
public sealed class ProjectedEventViewTests : IAsyncLifetime
{
    private const string Video = "https://youtu.be/projected-proof";

    private readonly SqlReadCapture _sql = new();
    private readonly SiteFactory _site;

    public ProjectedEventViewTests() => _site = new SiteFactory { ExtraServices = _sql.AddTo };

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Waiting_and_checked_conditions_are_read_without_folding_the_season()
    {
        var admin = await _site.SignedInAsync("admin");
        var player = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, NotEvenSweating);
        await DropAsync(player);

        var waiting = await ReadProjectedTurnAsync(player);
        Assert.Equal("Даже не вспотел", Assert.Single(waiting.GetProperty("waitingConditions").EnumerateArray()).GetProperty("name").GetString());

        await ApiCalls.PostOkAsync(player, $"{Season}/roll");
        await ApiCalls.PostOkAsync(player, $"{Season}/start");
        var playing = await ReadProjectedTurnAsync(player);
        Assert.Equal("hard", Assert.Single(playing.GetProperty("activeRun").GetProperty("conditions").EnumerateArray())
            .GetProperty("difficultyAtLeast").GetString());
        await ApiCalls.PostOkAsync(player, $"{Season}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });
        var completed = (await ReadProjectedTurnAsync(player)).GetProperty("lastCompleted");
        Assert.False(completed.GetProperty("conditionCheck").GetProperty("met").GetBoolean());
        var id = completed.GetProperty("id").GetGuid();

        await CompleteAsync(player);
        var older = Assert.Single((await ReadProjectedTurnAsync(player)).GetProperty("checks").EnumerateArray());
        Assert.Equal(id, older.GetProperty("id").GetGuid());
        Assert.Equal("not-even-sweating", Assert.Single(older.GetProperty("conditions").EnumerateArray()).GetProperty("objectId").GetString());
        Assert.False(older.GetProperty("conditionCheck").GetProperty("met").GetBoolean());
    }

    [Fact]
    public async Task The_owner_reads_an_irl_proof_from_the_manual_effect_projection()
    {
        var admin = await _site.SignedInAsync("admin");
        var player = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Karaoke);
        await DropAsync(player);

        var effect = Assert.Single((await ReadProjectedTurnAsync(player)).GetProperty("manualEffects").EnumerateArray());
        Assert.True(effect.GetProperty("irl").GetBoolean());
        Assert.Equal("Караоке", effect.GetProperty("name").GetString());
        var effectId = effect.GetProperty("id").GetGuid();
        await PostOkAsync(player, $"{Season}/effects/{effectId}/irl-proof", new { commandId = Guid.NewGuid(), link = Video });

        var proof = Assert.Single((await ReadProjectedTurnAsync(player)).GetProperty("manualEffects").EnumerateArray()).GetProperty("irlProof");
        Assert.Equal("pending", proof.GetProperty("status").GetString());
        Assert.Equal(Video, proof.GetProperty("link").GetString());
        Assert.Empty((await ReadProjectedTurnAsync(await _site.SignedInAsync("petya"))).GetProperty("manualEffects").EnumerateArray());
    }

    private async Task<JsonElement> ReadProjectedTurnAsync(HttpClient player)
    {
        _sql.Clear();
        var turn = await MeAsync(player);
        Assert.NotEmpty(_sql.From("SeasonPlayer"));
        Assert.Single(_sql.From("GameEvent"), read => read.FiltersEventType("content-published")
            && read.Text.Contains("MAX(", StringComparison.Ordinal));
        Assert.DoesNotContain(_sql.From("GameEvent"), read =>
            read.Text.Contains("\"Data\"", StringComparison.Ordinal)
            && !read.Text.Contains("\"Type\" =", StringComparison.Ordinal)
            && !read.Text.Contains("\"Type\" IN", StringComparison.Ordinal));
        return turn;
    }
}

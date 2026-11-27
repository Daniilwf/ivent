using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using static GameEvent.Web.Tests.Api.EventSeason;

namespace GameEvent.Web.Tests.Api;

/// <summary>The IRL queue and marks in the effects list read season projections (D-828, task 5.24).</summary>
public sealed class AdminIrlProjectionReadsTests : IAsyncLifetime
{
    private readonly SqlReadCapture _reads = new();
    private readonly SiteFactory _site;

    public AdminIrlProjectionReadsTests() => _site = new SiteFactory { ExtraServices = _reads.AddTo };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task The_queue_uses_projected_proofs_in_submission_order_and_keeps_the_latest_deleted_card()
    {
        var admin = await _site.SignedInAsync("admin");
        var owner = await _site.SignedInAsync("vasya");
        var other = await _site.SignedInAsync("petya");
        await SetUpAsync(admin, Karaoke);

        var ownerEffect = await DrawAsync(owner, "vasya");
        var otherEffect = await DrawAsync(other, "petya");
        await PostOkAsync(other, $"{Season}/effects/{otherEffect}/irl-proof", new { commandId = Guid.NewGuid(), link = "https://youtu.be/first" });
        _site.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostOkAsync(owner, $"{Season}/effects/{ownerEffect}/irl-proof", new { commandId = Guid.NewGuid(), link = "https://youtu.be/second" });

        var queue = await LoadProjectedAsync(admin, "irl-proofs");
        Assert.Equal(new[] { otherEffect, ownerEffect }, queue.Select(item => item.GetProperty("effectId").GetGuid()));
        Assert.Equal(new[] { "petya", "vasya" }, queue.Select(item => item.GetProperty("playerName").GetString()));
        Assert.All(queue, item => Assert.Equal("Караоке", item.GetProperty("name").GetString()));

        const string Renamed = "Ночное караоке";
        var content = $"/api/test/seasons/{SiteFactory.SeasonId}/content";
        await PostOkAsync(admin, content, Pack(Karaoke.Replace("Караоке", Renamed, StringComparison.Ordinal)));
        await PostOkAsync(admin, content, Pack(Toast));

        var withRemovedObject = await LoadProjectedAsync(admin, "irl-proofs");
        Assert.Equal(new[] { otherEffect, ownerEffect }, withRemovedObject.Select(item => item.GetProperty("effectId").GetGuid()));
        Assert.All(withRemovedObject, item => Assert.Equal(Renamed, item.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task The_effects_list_knows_which_published_objects_require_media_including_deleted_ones()
    {
        var admin = await _site.SignedInAsync("admin");
        var owner = await _site.SignedInAsync("vasya");
        var other = await _site.SignedInAsync("petya");
        await SetUpAsync(admin, Karaoke);
        var irlEffect = await DrawAsync(owner, "vasya");
        await PostOkAsync(admin, $"/api/test/seasons/{SiteFactory.SeasonId}/content", Pack(Toast));
        var ordinaryEffect = await DrawAsync(other, "petya");

        var effects = (await LoadProjectedAsync(admin, "effects")).ToDictionary(item => item.GetProperty("id").GetGuid());
        Assert.Equal(2, effects.Count);
        Assert.True(effects[irlEffect].GetProperty("irl").GetBoolean());
        Assert.False(effects[ordinaryEffect].GetProperty("irl").GetBoolean());
    }

    private async Task<Guid> DrawAsync(HttpClient player, string login)
    {
        await DropAsync(player);
        await using var db = _site.NewDb();
        return await db.ManualEffects.AsNoTracking().Where(effect => effect.PlayerId == _site.Players[login]).Select(effect => effect.Id).SingleAsync(Ct);
    }

    private async Task<JsonElement[]> LoadProjectedAsync(HttpClient admin, string endpoint)
    {
        _reads.Clear();
        var result = await ApiCalls.OkAsync(await admin.GetAsync($"{Admin}/{endpoint}", Ct));
        Assert.All(_reads.From("GameEvent"), read => Assert.True(read.FiltersEventType("content-published"), read.Text));
        Assert.NotEmpty(_reads.From("PendingManualEffect"));
        return [.. result.EnumerateArray()];
    }
}

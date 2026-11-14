using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.EventLog;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Api.EventSeason;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The cached fold of a season for the site's reads (D-828): after every command it is the fold of the whole log, the
/// same as replaying it from scratch; a log it does not know any more (another database) is folded anew.
/// </summary>
public sealed class SeasonStateReaderTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task After_each_command_the_cached_fold_is_the_fold_of_the_whole_log()
    {
        var reader = _site.Services.GetRequiredService<SeasonStateReader>();
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await AssertSameAsync(reader);

        await SetUpAsync(admin, CoinOrStep);
        await AssertSameAsync(reader);

        await ApiCalls.PostOkAsync(vasya, $"{Season}/roll");
        await AssertSameAsync(reader);
        await ApiCalls.PostOkAsync(vasya, $"{Season}/start");
        await ApiCalls.PostOkAsync(vasya, $"{Season}/drop");
        await AssertSameAsync(reader);

        var me = await MeAsync(vasya);
        await PostOkAsync(vasya, $"{Season}/choose", new { commandId = Guid.NewGuid(), choiceId = me.GetProperty("choice").GetProperty("id").GetGuid(), optionId = "монетки" });
        await AssertSameAsync(reader);
        await AssertSameAsync(reader);
    }

    [Fact]
    public async Task A_log_the_cache_does_not_know_is_folded_anew()
    {
        // The same reader over another site's database: the season id is the same, its log is not
        var reader = _site.Services.GetRequiredService<SeasonStateReader>();
        var vasya = await _site.SignedInAsync("vasya");
        await ApiCalls.PostOkAsync(vasya, $"{Season}/roll");
        await AssertSameAsync(reader);

        await using var other = new SiteFactory();
        await other.SeedAsync();
        await using var db = other.NewDb();
        var state = await reader.ReadAsync(db, SiteFactory.SeasonId, Ct);
        var (replayed, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);

        Assert.Equal(Json(replayed), Json(state));
    }

    private async Task AssertSameAsync(SeasonStateReader reader)
    {
        await using var db = _site.NewDb();
        var state = await reader.ReadAsync(db, SiteFactory.SeasonId, Ct);
        var (replayed, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
        Assert.Equal(Json(replayed), Json(state));
    }

    private static string Json(SeasonState state) => JsonSerializer.Serialize(state, EngineJson.Options);
}

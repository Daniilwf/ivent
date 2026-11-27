using System.Data.Common;
using System.Text.Json;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.Api;

public sealed class RollDisclosureFeedRaceTests
{
    [Fact]
    public async Task A_roll_committed_during_a_spectators_feed_read_does_not_disclose_the_game()
    {
        var stop = new PauseAfterPlayerRead();
        await using var site = new SiteFactory { ExtraServices = stop.AddTo };
        await site.SeedAsync();
        using var owner = await site.SignedInAsync("vasya");
        using var spectator = await site.SignedInAsync("zritel");
        var season = $"/api/seasons/{SiteFactory.SeasonId}";
        var rollCommand = Guid.NewGuid();
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeout = TimeSpan.FromSeconds(15);

        stop.Arm();
        var feedInProgress = spectator.GetAsync($"{season}/feed", cancellationToken);
        JsonElement turn;
        try
        {
            // The spectator has started reading the old projection. The owner must commit before that read resumes.
            await stop.Reached.WaitAsync(timeout, cancellationToken);
            await PostOkAsync(owner, $"{season}/roll", new { commandId = rollCommand }).WaitAsync(timeout, cancellationToken);
            using var ownerResponse = await owner.GetAsync(season, cancellationToken);
            turn = (await OkAsync(ownerResponse)).GetProperty("me");
        }
        finally
        {
            stop.Release();
        }

        var offer = turn.GetProperty("offer");
        var game = offer.GetProperty("id").GetGuid();
        var title = offer.GetProperty("title").GetString()!;
        Assert.False(turn.GetProperty("roll").GetProperty("revealed").GetBoolean());

        using var concurrentResponse = await feedInProgress;
        var concurrent = await OkAsync(concurrentResponse);
        var entries = concurrent.GetProperty("entries").EnumerateArray();
        Assert.Contains(entries, entry => entry.GetProperty("type").GetString() == "season-created");
        Assert.All(entries.Where(entry => Matches(entry, rollCommand)), AssertHiddenRoll);
        AssertNoGame(concurrent, game, title);

        using var nextResponse = await spectator.GetAsync($"{season}/feed", cancellationToken);
        var next = await OkAsync(nextResponse);
        var placeholder = Assert.Single(next.GetProperty("entries").EnumerateArray(), entry => Matches(entry, rollCommand));
        AssertHiddenRoll(placeholder);
        AssertNoGame(next, game, title);
    }

    private static bool Matches(JsonElement entry, Guid commandId) => entry.GetProperty("commandId").GetGuid() == commandId;

    private static void AssertHiddenRoll(JsonElement entry)
    {
        Assert.Equal("game-roll-hidden", entry.GetProperty("type").GetString());
        Assert.False(entry.GetProperty("data").TryGetProperty("gameId", out _));
    }

    private static void AssertNoGame(JsonElement feed, Guid game, string title)
    {
        Assert.DoesNotContain(feed.GetProperty("games").EnumerateArray(), item => item.GetProperty("id").GetGuid() == game);
        Assert.DoesNotContain(game.ToString("D"), feed.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(title, feed.GetRawText(), StringComparison.Ordinal);
    }

    private sealed class PauseAfterPlayerRead : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public Task Reached => _reached.Task;

        public void AddTo(IServiceCollection services) =>
            services.AddDbContextFactory<GameEventDbContext>(options => options.AddInterceptors(this));

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _resume.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"SeasonPlayer\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _reached.TrySetResult();
                await _resume.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}

using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.Api;

public sealed class HallOfFameLargestRollApiTests : IAsyncLifetime
{
    private readonly CompletionDice _random = new();
    private readonly SiteFactory _site;

    public HallOfFameLargestRollApiTests() => _site = new() { Random = _random };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync(withSeason: false);
        await using var db = _site.NewDb();
        await db.Games.ExecuteUpdateAsync(games => games.SetProperty(game => game.Hours, (decimal?)null), Ct);
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Largest_uses_the_sum_ignores_rejected_rolls_and_keeps_ties_from_different_seasons()
    {
        using var player = await _site.SignedInAsync("vasya");
        using var spectator = await _site.SignedInAsync("zritel");
        using var admin = await _site.SignedInAsync("admin");

        var firstSeason = await StartSeasonAsync();
        var firstRoll = await CompleteAsync(firstSeason, "hard", 6, [2, 2]);
        Assert.Empty(await LargestAsync());
        await FinishSeasonAsync(firstSeason, firstRoll, null);
        await SetStatusAsync(firstSeason, "archived");

        var secondSeason = await StartSeasonAsync();
        var secondRoll = await CompleteAsync(secondSeason, "normal", 3, [4]);
        await FinishSeasonAsync(secondSeason, secondRoll, null);

        var thirdSeason = await StartSeasonAsync();
        var smallerMaximum = await CompleteAsync(thirdSeason, "easy", 3, [2]);
        var rejected = await CompleteAsync(thirdSeason, "hard", 6, [6, 6]);
        await FinishSeasonAsync(thirdSeason, smallerMaximum, rejected);

        var records = await LargestAsync();
        Assert.Equal(2, records.Length);
        Assert.Contains((firstSeason, firstRoll, 4), records);
        Assert.Contains((secondSeason, secondRoll, 4), records);

        async Task<Guid> StartSeasonAsync()
        {
            var id = await _site.CreateSeasonAsync();
            var bus = _site.Services.GetRequiredService<CommandBus>();
            var entry = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), id,
                new AddSeasonPlayer(Guid.NewGuid(), _site.Users["vasya"], "Вася"), _site.Users["admin"]), Ct);
            Assert.True(entry.IsAccepted, entry.Rejection?.Detail);
            await SetStatusAsync(id, "active");
            return id;
        }

        async Task<Guid> CompleteAsync(Guid seasonId, string difficulty, int hours, int[] faces)
        {
            var season = $"/api/seasons/{seasonId}";
            _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(1);
            await PostOkAsync(player, $"{season}/roll");
            await PostOkAsync(player, $"{season}/start");
            _random.Set(faces);
            await PostOkAsync(player, $"{season}/complete", new
            {
                commandId = Guid.NewGuid(),
                difficulty,
                estimatedHours = hours,
                hoursSource = "https://howlongtobeat.com/game/2231",
            });
            Assert.Equal(0, _random.Remaining);
            using var response = await player.GetAsync(season, Ct);
            return (await OkAsync(response)).GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetGuid();
        }

        async Task FinishSeasonAsync(Guid seasonId, Guid approved, Guid? rejected)
        {
            await SetStatusAsync(seasonId, "closing");
            var runs = $"/api/admin/seasons/{seasonId}/runs";
            await PostOkAsync(admin, $"{runs}/{approved}/approve", new { commandId = Guid.NewGuid(), comment = "Confirmed" });
            if (rejected is { } runId)
            {
                await PostOkAsync(admin, $"{runs}/{runId}/reject", new { commandId = Guid.NewGuid(), comment = "Rejected" });
            }

            await SetStatusAsync(seasonId, "finished");
        }

        Task SetStatusAsync(Guid seasonId, string to) =>
            PostOkAsync(admin, $"/api/admin/seasons/{seasonId}/status", new { commandId = Guid.NewGuid(), to });

        async Task<(Guid SeasonId, Guid RunId, int Sum)[]> LargestAsync()
        {
            using var response = await spectator.GetAsync("/api/hall-of-fame/records", Ct);
            var result = await OkAsync(response);
            Assert.True(result.TryGetProperty("largestRolls", out var rolls), "The records response must include largestRolls.");
            return [.. rolls.EnumerateArray().Select(roll =>
                (roll.GetProperty("seasonId").GetGuid(), roll.GetProperty("runId").GetGuid(), roll.GetProperty("sum").GetInt32()))];
        }
    }

    private sealed class CompletionDice : IRandomSource
    {
        private readonly Queue.SeededRandom _fallback = new(11);
        private Queue<int> _faces = new();

        public int Remaining => _faces.Count;

        public void Set(int[] faces) => _faces = new Queue<int>(faces);

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (!_faces.TryDequeue(out var face))
            {
                return _fallback.NextInt(minInclusive, maxExclusive);
            }

            Assert.InRange(face, minInclusive, maxExclusive - 1);
            return face;
        }
    }
}
